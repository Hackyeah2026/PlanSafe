import { expect } from "@playwright/test";
import { test, readReference } from "./fixtures.js";
import type { EncodedSnapshot, GpuTestEngine } from "./gpu-types.js";
import type { MapGpuSnapshot } from "../../src/PlanSafe.App/TypeScript/crowdSimulatorGpu.js";

test("GPU safe-zone routing matches CPU distance, blended and occupancy-only assignments", async ({
  gpuPage: page,
}) => {
  const references = readReference<
    {
      snapshot: EncodedSnapshot;
      assignments: number[];
      after: EncodedSnapshot;
    }[]
  >("MapSafeZoneRoutingTests", "PLANSAFE_ROUTING_REFERENCE");
  const results = await page.evaluate(async (references) => {
    const module = "/crowdSimulatorGpu.js";
    const { GpuSimulationEngine } = (await import(
      module
    )) as typeof import("../../src/PlanSafe.App/TypeScript/crowdSimulatorGpu.js");
    const decode = (snapshot: EncodedSnapshot): MapGpuSnapshot => {
      const bytes = (value: string) =>
        Uint8Array.from(atob(value), (char) => char.charCodeAt(0));
      return {
        ...snapshot,
        scenario: bytes(snapshot.scenario),
        agents: bytes(snapshot.agents),
        fields: bytes(snapshot.fields),
        blocked: bytes(snapshot.blocked),
      };
    };
    const results = [];
    for (const reference of references) {
      const engine = new GpuSimulationEngine() as unknown as GpuTestEngine & {
        mapAssignedTargets?: Float32Array;
      };
      await engine.boot();
      engine.device.pushErrorScope("validation");
      try {
        engine.initializeMap(decode(reference.snapshot));
        const assignments = Array.from(
          engine.mapAssignedTargets ??
            new Float32Array(reference.snapshot.count),
        );
        await engine.advanceFixedTicks(1);
        const actual = Array.from((await engine.capturePreview()).values);
        const expected = new Float32Array(
          decode(reference.after).agents.buffer,
        );
        const count = reference.snapshot.count;
        let error = 0;
        for (let agent = 0; agent < count; agent++) {
          for (let lane = 0; lane < 4; lane++)
            error = Math.max(
              error,
              Math.abs(
                actual[lane * count + agent] - expected[agent * 8 + lane],
              ),
            );
        }
        const validation = await engine.device.popErrorScope();
        results.push({ assignments, error, validation: validation?.message });
      } finally {
        engine.dispose();
      }
    }
    const engine = new GpuSimulationEngine() as unknown as GpuTestEngine & {
      mapAssignedTargets?: Float32Array;
    };
    await engine.boot();
    engine.device.pushErrorScope("validation");
    try {
      engine.initializeMap(decode(references[0].snapshot));
      await engine.advanceFixedTicks(3);
      const before = await engine.capturePreview();
      const valuesBefore = Array.from(before.values);
      await engine.updateMapRouting(decode(references[2].snapshot));
      const after = await engine.capturePreview();
      const assignments = Array.from(engine.mapAssignedTargets!);
      const validation = await engine.device.popErrorScope();
      return {
        results,
        changed: {
          tickBefore: before.tick,
          tickAfter: after.tick,
          valuesBefore,
          valuesAfter: Array.from(after.values),
          assignments,
          validation: validation?.message,
        },
      };
    } finally {
      engine.dispose();
    }
  }, references);
  for (let index = 0; index < results.results.length; index++) {
    expect(results.results[index].validation).toBeUndefined();
    expect(results.results[index].assignments).toEqual(
      references[index].assignments,
    );
    // Use the same tolerance as the existing CPU/GPU map playback checks.
    expect(results.results[index].error).toBeLessThan(0.001);
  }
  expect(results.changed.validation).toBeUndefined();
  expect(results.changed.tickAfter).toBe(results.changed.tickBefore);
  expect(results.changed.valuesAfter).toEqual(results.changed.valuesBefore);
  expect(
    results.changed.assignments.filter((target) => target === 0),
  ).toHaveLength(10);
  expect(
    results.changed.assignments.filter((target) => target === 1),
  ).toHaveLength(10);
});

test("weighted GPU routing preserves reservations and releases replaced buffers", async ({
  gpuPage: page,
}) => {
  const references = readReference<{ snapshot: EncodedSnapshot }[]>(
    "MapSafeZoneRoutingTests",
    "PLANSAFE_ROUTING_REFERENCE",
  );
  const result = await page.evaluate(async (references) => {
    const module = "/crowdSimulatorGpu.js";
    const { GpuSimulationEngine } = (await import(
      module
    )) as typeof import("../../src/PlanSafe.App/TypeScript/crowdSimulatorGpu.js");
    const live = new Map<GPUBuffer, number>();
    const create = GPUDevice.prototype.createBuffer;
    const destroy = GPUBuffer.prototype.destroy;
    GPUDevice.prototype.createBuffer = function (descriptor) {
      const buffer = create.call(this, descriptor);
      live.set(buffer, buffer.size);
      return buffer;
    };
    GPUBuffer.prototype.destroy = function () {
      live.delete(this);
      destroy.call(this);
    };
    const usage = () => ({
      count: live.size,
      bytes: [...live.values()].reduce((sum, bytes) => sum + bytes, 0),
    });
    const engine = new GpuSimulationEngine() as unknown as GpuTestEngine & {
      mapAssignedTargets: Float32Array;
    };
    try {
      await engine.boot();
      engine.device.pushErrorScope("validation");
      const decode = (snapshot: EncodedSnapshot): MapGpuSnapshot => {
        const bytes = (value: string) =>
          Uint8Array.from(atob(value), (char) => char.charCodeAt(0));
        return {
          ...snapshot,
          scenario: bytes(snapshot.scenario),
          agents: bytes(snapshot.agents),
          fields: bytes(snapshot.fields),
          blocked: bytes(snapshot.blocked),
        };
      };
      const snapshot = decode(references[2].snapshot);
      const agents = new Float32Array(snapshot.agents.buffer);
      // The first dot is assigned here, while the second must pass this zone on its way to the other one.
      agents[0] = agents[8] = 10;
      agents[1] = agents[9] = 30;
      engine.initializeMap(snapshot);
      await engine.advanceFixedTicks(1);
      const before = await engine.capturePreview();
      const passedZone = before.values[4 * snapshot.count + 1] > 0;
      await engine.updateMapRouting(snapshot);
      const remainingAssignments = Array.from(engine.mapAssignedTargets).filter(
        (target) => target >= 0,
      );
      const evacuated = engine.evacuationStatus().evacuatedPeople;
      const samples = [];
      for (let iteration = 0; iteration < 6; iteration++) {
        if (iteration % 2 === 0) await engine.updateMap(snapshot);
        else await engine.updateMapRouting(decode(references[0].snapshot));
        samples.push(usage());
      }
      await engine.device.queue.onSubmittedWorkDone();
      const validation = await engine.device.popErrorScope();
      engine.dispose();
      return {
        passedZone,
        remainingAssignments,
        evacuated,
        samples,
        disposed: usage(),
        validation: validation?.message,
      };
    } finally {
      engine.dispose();
      GPUDevice.prototype.createBuffer = create;
      GPUBuffer.prototype.destroy = destroy;
    }
  }, references);
  expect(result.validation).toBeUndefined();
  expect(result.passedZone).toBe(true);
  expect(result.evacuated).toBe(1);
  expect(
    result.remainingAssignments.filter((target) => target === 0),
  ).toHaveLength(9);
  expect(
    result.remainingAssignments.filter((target) => target === 1),
  ).toHaveLength(10);
  for (let index = 2; index < result.samples.length; index++)
    expect(result.samples[index]).toEqual(result.samples[index % 2]);
  expect(result.disposed).toEqual({ count: 0, bytes: 0 });
});
