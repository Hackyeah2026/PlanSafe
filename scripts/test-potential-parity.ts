import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFileSync, mkdtempSync, unlinkSync, rmdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import {
  rasterizeObstacles,
  seedPotentialField,
  solvePotentialField,
} from "../src/PlanSafe.App/wwwroot/js/potentialField.js";

const root = fileURLToPath(new URL("../", import.meta.url));
const temp = mkdtempSync(join(tmpdir(), "plansafe-fields-"));
const referencePath = join(temp, "reference.json");
const referenceRun = spawnSync(
  "dotnet",
  [
    "test",
    "tests/PlanSafe.Tests",
    "--no-restore",
    "--filter",
    "FullyQualifiedName~PotentialFieldParityTests",
    "--verbosity",
    "quiet",
  ],
  {
    cwd: root,
    env: { ...process.env, PLANSAFE_FIELD_REFERENCE: referencePath },
    encoding: "utf8",
  },
);
assert.equal(referenceRun.status, 0, referenceRun.stdout + referenceRun.stderr);
const references = JSON.parse(readFileSync(referencePath, "utf8"));
for (const ref of references) {
  for (const key of ["staticField", "dynamicField", "density", "penalty"])
    ref[key] = ref[key].map(Math.fround);
}
unlinkSync(referencePath);
rmdirSync(temp);

for (const ref of references) {
  test(`${ref.name}: production WebGPU static/dynamic field solver matches every WASM cell`, () => {
    const blocked = rasterizeObstacles(
      ref.cols,
      ref.rows,
      ref.cellSize,
      ref.obstacles,
    );
    assert.deepEqual(Array.from(blocked), ref.blocked);
    const hasCapacity = ref.targets.some(
      (t) => t.isActive && t.currentOccupancy < t.capacity,
    );
    const sinks = ref.targets
      .filter((t) => t.isActive)
      .map((t) => ({
        zone: [t.x, t.y, t.width, t.height],
        potential: Math.fround(
          ((Math.max(0, ref.weightOccupancy) /
            Math.max(0.001, ref.weightDistance)) *
            ref.width *
            t.currentOccupancy) /
            Math.max(1, t.capacity) +
            (t.currentOccupancy >= Math.max(1, t.capacity) && hasCapacity
              ? 100000
              : 0),
        ),
      }));
    const seeds = seedPotentialField(
      ref.cols,
      ref.rows,
      ref.cellSize,
      blocked,
      sinks,
    );
    const staticField = solvePotentialField(
      ref.cols,
      ref.rows,
      ref.cellSize,
      blocked,
      seeds,
    );
    for (let i = 0; i < staticField.length; i++)
      assert.equal(
        staticField[i],
        ref.staticField[i],
        `${ref.name} static cell ${i}`,
      );
    const dynamicField = ref.penalty.some((p) => p > 0.5)
      ? solvePotentialField(
          ref.cols,
          ref.rows,
          ref.cellSize,
          blocked,
          ref.targets.length === 1
            ? seedPotentialField(
                ref.cols,
                ref.rows,
                ref.cellSize,
                blocked,
                sinks.map((s) => ({ ...s, potential: 0 })),
              )
            : seeds,
          Float32Array.from(ref.penalty),
        )
      : staticField;
    for (let i = 0; i < dynamicField.length; i++)
      assert.equal(
        dynamicField[i],
        ref.dynamicField[i],
        `${ref.name} dynamic cell ${i}`,
      );
  });
}
