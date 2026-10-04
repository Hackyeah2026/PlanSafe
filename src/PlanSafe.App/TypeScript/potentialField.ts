// Grid construction follows CrowdSimulationEngine.PotentialFieldGrid. Round each
// edge operation to float32, as C# does before comparing Dijkstra candidates.
export const impassablePotential = Math.fround(3.4028234663852886e38);
export type Rectangle = readonly [number, number, number, number];
export interface PotentialSink {
  readonly zone: Rectangle;
  readonly potential: number;
}

export function rasterizeObstacles(
  cols: number,
  rows: number,
  cellSize: number,
  obstacles: readonly Rectangle[],
): Uint32Array {
  const blocked = new Uint32Array(cols * rows);
  for (let r = 0; r < rows; r++) {
    for (let c = 0; c < cols; c++) {
      const x = (c + 0.5) * cellSize;
      const y = (r + 0.5) * cellSize;
      if (
        obstacles.some(([ox, oy, w, h]) =>
          w < cellSize || h < cellSize
            ? c * cellSize < ox + w &&
              (c + 1) * cellSize > ox &&
              r * cellSize < oy + h &&
              (r + 1) * cellSize > oy
            : x > ox + 0.001 &&
              x < ox + w - 0.001 &&
              y > oy + 0.001 &&
              y < oy + h - 0.001,
        )
      )
        blocked[r * cols + c] = 1;
    }
  }
  return blocked;
}

export function seedPotentialField(
  cols: number,
  rows: number,
  cellSize: number,
  blocked: Uint32Array,
  sinks: readonly PotentialSink[],
  padding = 0,
): Float32Array {
  const seeds = new Float32Array(cols * rows).fill(impassablePotential);
  const invCell = 1 / cellSize;
  const bound = (v: number, max: number) =>
    Math.max(0, Math.min(max - 1, Math.trunc(v * invCell)));
  for (const {
    zone: [x, y, w, h],
    potential,
  } of sinks) {
    for (
      let r = bound(y - padding * cellSize, rows);
      r <= bound(y + h + padding * cellSize, rows);
      r++
    ) {
      for (
        let c = bound(x - padding * cellSize, cols);
        c <= bound(x + w + padding * cellSize, cols);
        c++
      ) {
        const idx = r * cols + c;
        if (!blocked[idx])
          seeds[idx] = Math.min(seeds[idx], Math.fround(potential));
      }
    }
  }
  return seeds;
}

export function solvePotentialField(
  cols: number,
  rows: number,
  cellSize: number,
  blocked: Uint32Array,
  seeds: Float32Array,
  penalty?: Float32Array,
): Float32Array {
  const field = seeds.slice();
  const indices: number[] = [];
  const costs: number[] = [];
  function push(idx: number, cost: number) {
    let i = indices.length;
    indices.push(idx);
    costs.push(cost);
    while (i > 0) {
      const p = (i - 1) >> 1;
      if (costs[p] <= cost) break;
      indices[i] = indices[p];
      costs[i] = costs[p];
      i = p;
    }
    indices[i] = idx;
    costs[i] = cost;
  }
  for (let i = 0; i < field.length; i++) {
    if (field[i] < impassablePotential) push(i, field[i]);
  }
  const dc = [1, -1, 0, 0, 1, 1, -1, -1, 1, 1, -1, -1, 2, 2, -2, -2];
  const dr = [0, 0, 1, -1, 1, -1, 1, -1, 2, -2, 2, -2, 1, -1, 1, -1];
  const step = Math.fround(cellSize);
  const diagonal = Math.fround(cellSize * 1.41421356);
  const knight = Math.fround(cellSize * 2.23606798);
  while (indices.length) {
    const idx = indices[0],
      cost = costs[0];
    const lastIdx = indices.pop()!,
      lastCost = costs.pop()!;
    if (indices.length) {
      let i = 0;
      while (2 * i + 1 < indices.length) {
        let child = 2 * i + 1;
        if (child + 1 < indices.length && costs[child + 1] < costs[child])
          child++;
        if (lastCost <= costs[child]) break;
        indices[i] = indices[child];
        costs[i] = costs[child];
        i = child;
      }
      indices[i] = lastIdx;
      costs[i] = lastCost;
    }
    if (cost > field[idx]) continue;
    const c = idx % cols,
      r = Math.floor(idx / cols);
    for (let i = 0; i < (penalty ? 8 : 16); i++) {
      const nc = c + dc[i],
        nr = r + dr[i];
      if (nc < 0 || nc >= cols || nr < 0 || nr >= rows) continue;
      const ni = nr * cols + nc;
      if (blocked[ni]) continue;
      if (i >= 4 && i < 8 && blocked[r * cols + nc] && blocked[nr * cols + c])
        continue;
      if (i >= 8) {
        const mc = c + Math.sign(dc[i]),
          mr = r + Math.sign(dr[i]);
        if (
          blocked[mr * cols + mc] ||
          blocked[r * cols + mc] ||
          blocked[mr * cols + c]
        )
          continue;
      }
      let edge = i < 4 ? step : i < 8 ? diagonal : knight;
      if (penalty) {
        const avg = Math.fround(0.5 * Math.fround(penalty[idx] + penalty[ni]));
        edge = Math.fround(edge * Math.fround(1 + avg));
      }
      const candidate = Math.fround(cost + edge);
      if (candidate < field[ni]) {
        field[ni] = candidate;
        push(ni, candidate);
      }
    }
  }
  return field;
}
