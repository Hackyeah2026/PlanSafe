import test from "node:test";
import assert from "node:assert/strict";
import {
  buildMapPotentialField,
  spawnMapAgents,
} from "../src/PlanSafe.App/wwwroot/js/crowdSimulatorGpu.js";

function scenario(columns, rows, cellSize, blocked, spawnZones, exits) {
  return {
    originLatitude: 50,
    originLongitude: 20,
    metersPerDegreeLatitude: 111320,
    metersPerDegreeLongitude: 71555,
    columns,
    rows,
    cellSize,
    worldWidth: columns * cellSize,
    worldHeight: rows * cellSize,
    blocked,
    spawnZones,
    exits,
    totalPeople: spawnZones.reduce((sum, zone) => sum + zone.people, 0),
  };
}

function inside(x, y, zone) {
  let result = false;
  for (let i = 0, j = zone.xs.length - 1; i < zone.xs.length; j = i++) {
    if (
      zone.ys[i] > y !== zone.ys[j] > y &&
      x <
        ((zone.xs[j] - zone.xs[i]) * (y - zone.ys[i])) /
          (zone.ys[j] - zone.ys[i]) +
          zone.xs[i]
    )
      result = !result;
  }
  return result;
}

function assertSafe(map, agents, radius) {
  const clearance = radius + 0.6;
  const potential = buildMapPotentialField(map);
  for (let offset = 0; offset < agents.length; offset += 8) {
    const x = agents[offset],
      y = agents[offset + 1];
    assert.ok(x >= clearance && x <= map.worldWidth - clearance);
    assert.ok(y >= clearance && y <= map.worldHeight - clearance);
    assert.ok(map.spawnZones.some((zone) => inside(x, y, zone)));
    assert.ok(
      potential[
        Math.floor(y / map.cellSize) * map.columns +
          Math.floor(x / map.cellSize)
      ] < 900000,
    );
    // Measure geometric wall distance independently of spawn validation.
    for (let row = 0; row < map.rows; row++) {
      for (let col = 0; col < map.columns; col++) {
        if (!map.blocked[row * map.columns + col]) continue;
        const dx = Math.max(
          col * map.cellSize - x,
          x - (col + 1) * map.cellSize,
          0,
        );
        const dy = Math.max(
          row * map.cellSize - y,
          y - (row + 1) * map.cellSize,
          0,
        );
        assert.ok(
          Math.hypot(dx, dy) >= clearance - 1e-5,
          `Spawn (${x}, ${y}) overlaps wall clearance at (${col}, ${row})`,
        );
      }
    }
  }
}

test("GPU random spawns avoid wall edges, corners, courtyards and polygon boundaries", () => {
  const blocked = new Uint8Array(80 * 80);
  for (let y = 0; y < 65; y++)
    for (let x = 50; x < 57; x++) blocked[y * 80 + x] = 1;
  for (let y = 10; y <= 25; y++)
    for (let x = 10; x <= 25; x++)
      if (x === 10 || x === 25 || y === 10 || y === 25) blocked[y * 80 + x] = 1;
  const map = scenario(
    80,
    80,
    1,
    blocked,
    [
      { xs: [1, 50, 50, 1], ys: [1, 1, 79, 79], people: 1000 },
      { xs: [58, 79, 58], ys: [1, 1, 79], people: 1000 },
    ],
    [{ x: 72, y: 72, radius: 3 }],
  );
  for (const radius of [0.35, 0.35 * (1 + 0.2 * Math.sqrt(24))]) {
    const agents = spawnMapAgents(
      map,
      1000,
      1,
      buildMapPotentialField(map),
      radius,
    );
    assertSafe(map, agents, radius);
    for (let i = 0; i < agents.length; i += 8)
      assert.ok(
        !(
          agents[i] > 10 &&
          agents[i] < 25 &&
          agents[i + 1] > 10 &&
          agents[i + 1] < 25
        ),
      );
  }
});

test("GPU spawn allocation excludes narrow zones for larger agents", () => {
  const blocked = new Uint8Array(80 * 80).fill(1);
  for (let y = 2; y < 78; y++)
    for (let x = 10; x < 15; x++) blocked[y * 80 + x] = 0;
  for (let y = 60; y < 78; y++)
    for (let x = 10; x < 78; x++) blocked[y * 80 + x] = 0;
  const map = scenario(
    80,
    80,
    0.5,
    blocked,
    [
      { xs: [5, 7.5, 7.5, 5], ys: [5, 5, 20, 20], people: 100 },
      { xs: [15, 35, 35, 15], ys: [32, 32, 37, 37], people: 100 },
    ],
    [{ x: 35, y: 35, radius: 2 }],
  );
  const field = buildMapPotentialField(map);
  const small = spawnMapAgents(map, 200, 1, field, 0.35);
  assert.ok(small.some((value, i) => i % 8 === 1 && value < 20));
  const radius = 0.35 * (1 + 0.2 * Math.sqrt(24));
  const large = spawnMapAgents(map, 8, 25, field, radius);
  for (let i = 0; i < large.length; i += 8) assert.ok(large[i + 1] >= 32);
  assertSafe(map, large, radius);
});

test("GPU spawning fails if no exit-connected location has clearance", () => {
  const blocked = new Uint8Array(20 * 20).fill(1);
  for (let x = 0; x < 20; x++) blocked[10 * 20 + x] = 0;
  const map = scenario(
    20,
    20,
    1,
    blocked,
    [{ xs: [1, 5, 5, 1], ys: [10, 10, 11, 11], people: 1 }],
    [{ x: 18, y: 10.5, radius: 1 }],
  );
  assert.throws(
    () => spawnMapAgents(map, 1, 1, buildMapPotentialField(map), 0.35),
    /sufficient spawn clearance/,
  );
});
