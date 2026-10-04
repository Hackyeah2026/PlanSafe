import assert from "node:assert/strict";
import { test, readReference } from "./fixtures.js";
import type { GpuTestEngine, EncodedSnapshot } from "./gpu-types.js";

interface CollisionReference {
  name: string;
  embedded: boolean;
  snapshot: EncodedSnapshot;
}
let fixtures: CollisionReference[];
test.beforeAll(() => {
  fixtures = readReference<CollisionReference[]>(
    "ContinuousMapCollisionTests",
    "PLANSAFE_MAP_COLLISION_REFERENCE",
  );
  assert.equal(fixtures.length, 6);
});

test("GPU map collisions reject embedded centers and movement across thin buildings", async ({
  gpuPage: page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  const result = await page.evaluate(async (fixtures) => {
    const gpuModule = "/crowdSimulatorGpu.js";
    const { GpuSimulationEngine, parseMapScenario } = (await import(
      gpuModule
    )) as typeof import("../../src/PlanSafe.App/TypeScript/crowdSimulatorGpu.js");
    const engine = new GpuSimulationEngine() as unknown as GpuTestEngine;
    await engine.boot();
    const errors: string[] = [];
    engine.device.addEventListener("uncapturederror", (event) =>
      errors.push(event.error.message),
    );
    const failures: string[] = [];
    for (const fixture of fixtures) {
      const bytes = (value: string) =>
        Uint8Array.from(atob(value), (char) => char.charCodeAt(0));
      const snapshot = {
        ...fixture.snapshot,
        scenario: bytes(fixture.snapshot.scenario),
        agents: bytes(fixture.snapshot.agents),
        fields: bytes(fixture.snapshot.fields),
        blocked: bytes(fixture.snapshot.blocked),
      };
      engine.initializeMap(snapshot);
      const map = parseMapScenario(snapshot.scenario);
      for (let tick = 0; tick < 100; tick++) {
        await engine.advanceFixedTicks(1);
        const preview = await engine.capturePreview();
        const x = preview.values[0],
          y = preview.values[preview.count],
          radius = preview.values[preview.count * 4];
        if (!fixture.embedded && x >= 30 && y > 5 && y < 35)
          failures.push(
            `${fixture.name}: crossed wall at tick ${tick}: (${x},${y})`,
          );
        for (
          let row = Math.max(0, Math.floor((y - radius) / map.cellSize));
          row <=
          Math.min(map.rows - 1, Math.floor((y + radius) / map.cellSize));
          row++
        ) {
          for (
            let col = Math.max(0, Math.floor((x - radius) / map.cellSize));
            col <=
            Math.min(map.columns - 1, Math.floor((x + radius) / map.cellSize));
            col++
          ) {
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
            if (Math.hypot(dx, dy) < radius - 0.00001)
              failures.push(
                `${fixture.name}: overlaps blocked cell at tick ${tick}: (${x},${y}), cell (${col},${row})`,
              );
          }
        }
        if (failures.length) break;
      }
    }
    engine.dispose();
    return { errors, failures };
  }, fixtures);
  assert.deepEqual(errors, []);
  assert.deepEqual(result.errors, []);
  assert.deepEqual(result.failures, []);
});
