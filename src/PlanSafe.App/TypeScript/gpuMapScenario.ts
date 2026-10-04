import {
  impassablePotential,
  rasterizeObstacles,
  seedPotentialField,
  solvePotentialField,
  type PotentialSink,
  type Rectangle,
} from "./potentialField.js";

// --- Potential Field Generator (16-Direction Dijkstra Flood Fill matching C# PotentialFieldGrid.cs) ---

export function buildPotentialField(
  worldWidth: number,
  worldHeight: number,
  obs1: [number, number, number, number],
  obs2: [number, number, number, number],
  exitZone: [number, number, number, number],
  cols = 100,
  rows = 100,
  cellSize?: number,
): Float32Array {
  const size = cellSize ?? worldWidth / cols;
  const blocked = rasterizeObstacles(cols, rows, size, [obs1, obs2]);
  const seeds = seedPotentialField(cols, rows, size, blocked, [
    { zone: exitZone, potential: 0 },
  ]);
  return solvePotentialField(cols, rows, size, blocked, seeds);
}

// --- Map Scenario Helpers ---

export interface ParsedMapExit {
  readonly x: number;
  readonly y: number;
  readonly radius: number;
  readonly capacity?: number;
  readonly initialOccupancy?: number;
  readonly basePotential?: number;
}

export interface ParsedMapSpawnZone {
  readonly people: number;
  readonly xs: number[];
  readonly ys: number[];
}

export interface ParsedMapScenario {
  readonly originLatitude: number;
  readonly originLongitude: number;
  readonly metersPerDegreeLatitude: number;
  readonly metersPerDegreeLongitude: number;
  readonly cellSize: number;
  readonly columns: number;
  readonly rows: number;
  readonly blocked: Uint8Array;
  readonly streets?: Uint8Array;
  readonly exits: readonly ParsedMapExit[];
  readonly spawnZones: readonly ParsedMapSpawnZone[];
  readonly worldWidth: number;
  readonly worldHeight: number;
  readonly totalPeople: number;
}

/**
 * Decodes C# EFMAP v1/v2 snapshots into world coordinates in meters.
 * Version 1 exits use the same capacity and occupancy defaults as the C# reader.
 * @throws If the format version is unsupported or the snapshot is truncated.
 */
export function parseMapScenario(data: Uint8Array): ParsedMapScenario {
  const magic = "EFMAP";
  for (let i = 0; i < 5; i++) {
    if (data[i] !== magic.charCodeAt(i))
      throw new Error("Invalid EFMAP magic.");
  }
  const view = new DataView(data.buffer, data.byteOffset, data.byteLength);
  let offset = 5;
  const version = view.getInt32(offset, true);
  offset += 4;
  if (version !== 1 && version !== 2 && version !== 3)
    throw new Error(`Unsupported EFMAP version: ${version}`);

  const originLatitude = view.getFloat64(offset, true);
  offset += 8;
  const originLongitude = view.getFloat64(offset, true);
  offset += 8;
  const metersPerDegreeLatitude = view.getFloat64(offset, true);
  offset += 8;
  const metersPerDegreeLongitude = view.getFloat64(offset, true);
  offset += 8;
  const cellSize = view.getFloat64(offset, true);
  offset += 8;
  const columns = view.getInt32(offset, true);
  offset += 4;
  const rows = view.getInt32(offset, true);
  offset += 4;

  const runCount = view.getInt32(offset, true);
  offset += 4;
  const totalCells = columns * rows;
  const blocked = new Uint8Array(totalCells);
  let blockedCellOffset = 0;
  let blockedRunValue = 0;
  for (let i = 0; i < runCount; i++) {
    const run = view.getInt32(offset, true);
    offset += 4;
    if (blockedRunValue === 1) {
      blocked.fill(1, blockedCellOffset, blockedCellOffset + run);
    }
    blockedCellOffset += run;
    blockedRunValue = 1 - blockedRunValue;
  }

  const exitCount = view.getInt32(offset, true);
  offset += 4;
  const exits: ParsedMapExit[] = [];
  for (let i = 0; i < exitCount; i++) {
    const x = view.getFloat64(offset, true);
    offset += 8;
    const y = view.getFloat64(offset, true);
    offset += 8;
    const radius = view.getFloat64(offset, true);
    offset += 8;
    let capacity = 1000,
      initialOccupancy = 0,
      basePotential = 0;
    if (version >= 2) {
      capacity = view.getInt32(offset, true);
      initialOccupancy = view.getInt32(offset + 4, true);
      basePotential = view.getFloat64(offset + 8, true);
      offset += 16;
    }
    exits.push({ x, y, radius, capacity, initialOccupancy, basePotential });
  }

  const zoneCount = view.getInt32(offset, true);
  offset += 4;
  const spawnZones: ParsedMapSpawnZone[] = [];
  let totalPeople = 0;
  for (let i = 0; i < zoneCount; i++) {
    const people = view.getInt32(offset, true);
    offset += 4;
    const points = view.getInt32(offset, true);
    offset += 4;
    const xs = new Array<number>(points);
    const ys = new Array<number>(points);
    for (let p = 0; p < points; p++) {
      xs[p] = view.getFloat64(offset, true);
      offset += 8;
      ys[p] = view.getFloat64(offset, true);
      offset += 8;
    }
    totalPeople += people;
    spawnZones.push({ people, xs, ys });
  }

  let streets: Uint8Array | undefined;
  if (version >= 3) {
    const runs = view.getInt32(offset, true);
    offset += 4;
    if (runs < 1 || runs > totalCells + 1)
      throw new Error("Invalid street raster encoding.");
    streets = new Uint8Array(totalCells);
    let index = 0,
      value = 0;
    for (let i = 0; i < runs; i++) {
      const run = view.getInt32(offset, true);
      offset += 4;
      if (run < 0 || run > totalCells - index)
        throw new Error("Invalid street raster encoding.");
      if (value) streets.fill(1, index, index + run);
      index += run;
      value = 1 - value;
    }
    if (index !== totalCells)
      throw new Error("Invalid street raster encoding.");
  }
  return {
    originLatitude,
    originLongitude,
    metersPerDegreeLatitude,
    metersPerDegreeLongitude,
    cellSize,
    columns,
    rows,
    blocked,
    streets,
    exits,
    spawnZones,
    worldWidth: columns * cellSize,
    worldHeight: rows * cellSize,
    totalPeople,
  };
}

export function isPointInPolygon(
  x: number,
  y: number,
  xs: readonly number[],
  ys: readonly number[],
): boolean {
  let inside = false;
  for (let i = 0, j = xs.length - 1; i < xs.length; j = i++) {
    if (
      ys[i] > y !== ys[j] > y &&
      x < ((xs[j] - xs[i]) * (y - ys[i])) / (ys[j] - ys[i]) + xs[i]
    ) {
      inside = !inside;
    }
  }
  return inside;
}

export function buildMapPotentialField(
  scenario: ParsedMapScenario,
): Float32Array {
  const { columns, rows, cellSize, exits, blocked } = scenario;
  const totalCells = columns * rows;
  const field = new Float32Array(totalCells).fill(impassablePotential);

  const heapIdx = new Int32Array(totalCells * 4);
  const heapCost = new Float32Array(totalCells * 4);
  let heapSize = 0;

  function pushHeap(idx: number, cost: number) {
    let i = heapSize++;
    heapIdx[i] = idx;
    heapCost[i] = cost;
    while (i > 0) {
      const p = (i - 1) >> 1;
      if (heapCost[p] <= heapCost[i]) break;
      const ti = heapIdx[i];
      heapIdx[i] = heapIdx[p];
      heapIdx[p] = ti;
      const tc = heapCost[i];
      heapCost[i] = heapCost[p];
      heapCost[p] = tc;
      i = p;
    }
  }

  function popHeap(): { idx: number; cost: number } | null {
    if (heapSize === 0) return null;
    const retIdx = heapIdx[0];
    const retCost = heapCost[0];
    heapSize--;
    if (heapSize > 0) {
      heapIdx[0] = heapIdx[heapSize];
      heapCost[0] = heapCost[heapSize];
      let i = 0;
      while (true) {
        let best = i;
        const left = 2 * i + 1;
        const right = 2 * i + 2;
        if (left < heapSize && heapCost[left] < heapCost[best]) best = left;
        if (right < heapSize && heapCost[right] < heapCost[best]) best = right;
        if (best === i) break;
        const ti = heapIdx[i];
        heapIdx[i] = heapIdx[best];
        heapIdx[best] = ti;
        const tc = heapCost[i];
        heapCost[i] = heapCost[best];
        heapCost[best] = tc;
        i = best;
      }
    }
    return { idx: retIdx, cost: retCost };
  }

  for (const exit of exits) {
    const minCol = Math.max(
      0,
      Math.min(columns - 1, Math.floor((exit.x - exit.radius) / cellSize)),
    );
    const maxCol = Math.max(
      0,
      Math.min(columns - 1, Math.floor((exit.x + exit.radius) / cellSize)),
    );
    const minRow = Math.max(
      0,
      Math.min(rows - 1, Math.floor((exit.y - exit.radius) / cellSize)),
    );
    const maxRow = Math.max(
      0,
      Math.min(rows - 1, Math.floor((exit.y + exit.radius) / cellSize)),
    );

    for (let r = minRow; r <= maxRow; r++) {
      for (let c = minCol; c <= maxCol; c++) {
        const idx = r * columns + c;
        if (blocked[idx] === 0 && field[idx] !== 0) {
          field[idx] = 0;
          pushHeap(idx, 0);
        }
      }
    }
  }

  const step = cellSize;
  const diagStep = cellSize * 1.41421356;
  const knightStep = cellSize * 2.23606798;

  const dCol = [1, -1, 0, 0, 1, 1, -1, -1, 1, 1, -1, -1, 2, 2, -2, -2];
  const dRow = [0, 0, 1, -1, 1, -1, 1, -1, 2, -2, 2, -2, 1, -1, 1, -1];
  const costs = [
    step,
    step,
    step,
    step,
    diagStep,
    diagStep,
    diagStep,
    diagStep,
    knightStep,
    knightStep,
    knightStep,
    knightStep,
    knightStep,
    knightStep,
    knightStep,
    knightStep,
  ];

  while (heapSize > 0) {
    const top = popHeap();
    if (!top) break;
    const { idx: currIdx, cost: currCost } = top;
    if (currCost > field[currIdx]) continue;

    const currCol = currIdx % columns;
    const currRow = Math.floor(currIdx / columns);

    for (let i = 0; i < 16; i++) {
      const nCol = currCol + dCol[i];
      const nRow = currRow + dRow[i];
      if (nCol < 0 || nCol >= columns || nRow < 0 || nRow >= rows) continue;

      const nIdx = nRow * columns + nCol;
      if (blocked[nIdx] !== 0) continue;

      if (i >= 4 && i < 8) {
        if (
          blocked[currRow * columns + nCol] !== 0 &&
          blocked[nRow * columns + currCol] !== 0
        ) {
          continue;
        }
      } else if (i >= 8) {
        const midCol = currCol + Math.sign(dCol[i]);
        const midRow = currRow + Math.sign(dRow[i]);
        if (
          blocked[midRow * columns + midCol] !== 0 ||
          blocked[currRow * columns + midCol] !== 0 ||
          blocked[midRow * columns + currCol] !== 0
        ) {
          continue;
        }
      }

      const candPot = currCost + costs[i];
      if (candPot < field[nIdx]) {
        field[nIdx] = candPot;
        pushHeap(nIdx, candPot);
      }
    }
  }

  return field;
}

export function spawnMapAgents(
  scenario: ParsedMapScenario,
  activeCount: number,
  granulation: number,
  potentialField: Float32Array,
  radius: number,
): Float32Array {
  const { columns, rows, cellSize, spawnZones, blocked } = scenario;
  const spawnCells: number[][] = [];
  const clearance = radius + 0.6;
  const isSafeSpawn = (x: number, y: number): boolean => {
    if (
      x < clearance ||
      y < clearance ||
      x > scenario.worldWidth - clearance ||
      y > scenario.worldHeight - clearance
    )
      return false;
    const index = Math.floor(y / cellSize) * columns + Math.floor(x / cellSize);
    if (!(potentialField[index] < 900000) || blocked[index] !== 0) return false;
    const minCol = Math.max(0, Math.floor((x - clearance) / cellSize));
    const maxCol = Math.min(
      columns - 1,
      Math.floor((x + clearance) / cellSize),
    );
    const minRow = Math.max(0, Math.floor((y - clearance) / cellSize));
    const maxRow = Math.min(rows - 1, Math.floor((y + clearance) / cellSize));
    for (let row = minRow; row <= maxRow; row++) {
      for (let col = minCol; col <= maxCol; col++) {
        if (blocked[row * columns + col] === 0) continue;
        const dx =
          x - Math.max(col * cellSize, Math.min(x, (col + 1) * cellSize));
        const dy =
          y - Math.max(row * cellSize, Math.min(y, (row + 1) * cellSize));
        if (dx * dx + dy * dy < clearance * clearance) return false;
      }
    }
    return true;
  };

  for (let z = 0; z < spawnZones.length; z++) {
    const zone = spawnZones[z];
    const cells: number[] = [];
    let minX = Infinity;
    let maxX = -Infinity;
    let minY = Infinity;
    let maxY = -Infinity;
    for (let i = 0; i < zone.xs.length; i++) {
      if (zone.xs[i] < minX) minX = zone.xs[i];
      if (zone.xs[i] > maxX) maxX = zone.xs[i];
      if (zone.ys[i] < minY) minY = zone.ys[i];
      if (zone.ys[i] > maxY) maxY = zone.ys[i];
    }

    const minCol = Math.max(
      0,
      Math.min(columns - 1, Math.floor(minX / cellSize)),
    );
    const maxCol = Math.max(
      0,
      Math.min(columns - 1, Math.floor(maxX / cellSize)),
    );
    const minRow = Math.max(0, Math.min(rows - 1, Math.floor(minY / cellSize)));
    const maxRow = Math.max(0, Math.min(rows - 1, Math.floor(maxY / cellSize)));

    for (let r = minRow; r <= maxRow; r++) {
      const cy = (r + 0.5) * cellSize;
      for (let c = minCol; c <= maxCol; c++) {
        const cx = (c + 0.5) * cellSize;
        const idx = r * columns + c;
        if (isSafeSpawn(cx, cy) && isPointInPolygon(cx, cy, zone.xs, zone.ys)) {
          cells.push(idx);
        }
      }
    }
    spawnCells.push(cells);
  }

  let totalWeight = 0;
  const weights = new Array<number>(spawnZones.length);
  for (let z = 0; z < spawnZones.length; z++) {
    if (spawnCells[z].length > 0) {
      weights[z] = spawnZones[z].people;
      totalWeight += weights[z];
    } else {
      weights[z] = 0;
    }
  }

  if (totalWeight <= 0) {
    for (let z = 0; z < spawnZones.length; z++) {
      if (spawnCells[z].length > 0) {
        weights[z] = 1;
        totalWeight += 1;
      }
    }
  }

  if (totalWeight <= 0) {
    throw new Error(
      "No street inside the evacuation zones is connected to an evacuation point with sufficient spawn clearance.",
    );
  }

  const allocation = new Array<number>(spawnZones.length).fill(0);
  const remainders: { remainder: number; zone: number }[] = [];
  let assigned = 0;

  for (let z = 0; z < spawnZones.length; z++) {
    const exact = (activeCount * weights[z]) / totalWeight;
    allocation[z] = Math.floor(exact);
    assigned += allocation[z];
    if (weights[z] > 0) {
      remainders.push({ remainder: exact - allocation[z], zone: z });
    }
  }

  remainders.sort((a, b) => b.remainder - a.remainder);
  const remaining = activeCount - assigned;
  for (let i = 0; i < remaining && i < remainders.length; i++) {
    allocation[remainders[i].zone]++;
  }

  const initData = new Float32Array(activeCount * 8);
  const u32View = new Uint32Array(initData.buffer);

  let seed = 12345;
  const nextRandom = () => {
    seed = (seed * 1664525 + 1013904223) >>> 0;
    return seed / 4294967296;
  };

  let agentIndex = 0;
  for (let z = 0; z < spawnZones.length; z++) {
    const cells = spawnCells[z];
    for (let k = 0; k < allocation[z]; k++, agentIndex++) {
      const cellIdx = cells[Math.floor(nextRandom() * cells.length)];
      const c = cellIdx % columns;
      const r = Math.floor(cellIdx / columns);
      let x = (c + 0.5) * cellSize;
      let y = (r + 0.5) * cellSize;

      for (let attempt = 0; attempt < 8; attempt++) {
        const candX = (c + nextRandom()) * cellSize;
        const candY = (r + nextRandom()) * cellSize;
        if (
          isSafeSpawn(candX, candY) &&
          isPointInPolygon(candX, candY, spawnZones[z].xs, spawnZones[z].ys)
        ) {
          x = candX;
          y = candY;
          break;
        }
      }

      const speed = 1.3 + nextRandom() * 0.4;
      const off = agentIndex * 8;
      initData[off + 0] = x;
      initData[off + 1] = y;
      initData[off + 2] = 0.0;
      initData[off + 3] = 0.0;
      initData[off + 4] = radius;
      initData[off + 5] = speed;
      u32View[off + 6] = 1; // flags = 1 (active)
      u32View[off + 7] = 0;
    }
  }

  return initData;
}
