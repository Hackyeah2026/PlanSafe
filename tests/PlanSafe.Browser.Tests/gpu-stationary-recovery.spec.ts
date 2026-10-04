import assert from "node:assert/strict";
import { test, readReference } from "./fixtures.js";
import type { GpuTestEngine, EncodedSnapshot } from "./gpu-types.js";
import type { MapGpuSnapshot } from "../../src/PlanSafe.App/TypeScript/crowdSimulatorGpu.js";

interface StationaryReference {
  name: string;
  snapshot: EncodedSnapshot;
  expectedX: number;
  expectedY: number;
}
let fixtures: StationaryReference[];
test.beforeAll(() => {
  fixtures = readReference<StationaryReference[]>(
    "StationaryMapRecoveryTests",
    "PLANSAFE_STATIONARY_REFERENCE",
  );
  assert.equal(fixtures.length, 2);
});

test("GPU stationary recovery uses 180 simulated seconds and the nearest reachable street", async ({
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
    const check = (condition: boolean, message: string) => {
      if (!condition) failures.push(message);
    };
    const decode = (encoded: EncodedSnapshot): MapGpuSnapshot => {
      const bytes = (value: string) =>
        Uint8Array.from(atob(value), (c) => c.charCodeAt(0));
      return {
        ...encoded,
        scenario: bytes(encoded.scenario),
        agents: bytes(encoded.agents),
        fields: bytes(encoded.fields),
        blocked: bytes(encoded.blocked),
      };
    };
    const advance = async (ticks: number) => {
      while (ticks > 0) {
        const batch = Math.min(ticks, 256);
        await engine.advanceFixedTicks(batch);
        ticks -= batch;
      }
    };
    const readRecovery = async () => {
      const device = engine.device;
      const buffer = device.createBuffer({
        size: engine.recoveryBuffer.size,
        usage: GPUBufferUsage.MAP_READ | GPUBufferUsage.COPY_DST,
      });
      const encoder = device.createCommandEncoder();
      encoder.copyBufferToBuffer(
        engine.recoveryBuffer,
        0,
        buffer,
        0,
        buffer.size,
      );
      device.queue.submit([encoder.finish()]);
      await buffer.mapAsync(GPUMapMode.READ);
      const values = new Uint32Array(buffer.getMappedRange()).slice();
      buffer.unmap();
      buffer.destroy();
      return values;
    };
    for (const fixture of fixtures) {
      const snapshot = decode(fixture.snapshot);
      engine.initializeMap(snapshot);
      const map = parseMapScenario(snapshot.scenario);
      check(
        map.streets?.some(Boolean) === true,
        fixture.name + ": street metadata missing",
      );
      await advance(11249);
      const before = await engine.capturePreview();
      check(
        Math.abs(before.values[0] - 10.5) < 0.001,
        fixture.name + ": recovered too early",
      );
      await advance(1);
      const after = await engine.capturePreview();
      check(
        Math.abs(after.values[0] - fixture.expectedX) < 0.001 &&
          Math.abs(after.values[1] - fixture.expectedY) < 0.001,
        fixture.name + ": recovery at " + JSON.stringify([...after.values]),
      );
      const telemetry = await engine.captureTelemetry();
      check(
        telemetry.evacuatedAgents === 0 &&
          telemetry.activeAgents === snapshot.count,
        fixture.name + ": counts changed",
      );
      engine.initializeMap(snapshot);
      check(
        (await readRecovery())[2] === 0,
        fixture.name + ": reset kept the old timer",
      );
      // Seed one tick before the threshold, then make genuine progress.
      engine.device.queue.writeBuffer(
        engine.recoveryBuffer,
        8,
        new Uint32Array([11249]),
      );
      engine.device.queue.writeBuffer(
        engine.agentsBuffer,
        0,
        new Float32Array([10.8]),
      );
      await advance(1);
      check(
        (await readRecovery())[2] === 0,
        fixture.name + ": movement did not reset the timer",
      );
      check(
        Math.abs((await engine.capturePreview()).values[0] - 10.8) < 0.001,
        fixture.name + ": moving agent was relocated",
      );
      engine.initializeMap(snapshot);
      engine.device.queue.writeBuffer(
        engine.recoveryBuffer,
        8,
        new Uint32Array([11249]),
      );
      engine.device.queue.writeBuffer(
        engine.agentsBuffer,
        0,
        new Float32Array([10.55]),
      );
      await advance(1);
      check(
        Math.abs(
          (await engine.capturePreview()).values[0] - fixture.expectedX,
        ) < 0.001,
        fixture.name + ": jitter prevented recovery",
      );
    }
    const original = decode(fixtures[0].snapshot);
    const pair = new Uint8Array(64);
    pair.set(original.agents);
    pair.set(original.agents, 32);
    new Float32Array(pair.buffer)[8] += 1;
    engine.initializeMap({
      ...original,
      count: 2,
      agents: pair,
      socialRepulsionWeight: 0,
    });
    for (const offset of [8, 24])
      engine.device.queue.writeBuffer(
        engine.recoveryBuffer,
        offset,
        new Uint32Array([11249]),
      );
    await advance(1);
    const queued = await engine.capturePreview();
    check(
      queued.values[0] < 15 && queued.values[1] < 15,
      "Queued agents were relocated",
    );
    const queueTimers = await readRecovery();
    check(
      queueTimers[2] === 0 && queueTimers[6] === 0,
      "Nearby agents did not reset stationary timers",
    );
    // Parallel recoveries cannot overlap, including an agent already on
    // the nearest street. A reservation conflict retries on the next tick.
    const simultaneous = pair.slice();
    new Float32Array(simultaneous.buffer)[8] = 37.5;
    engine.initializeMap({
      ...original,
      count: 2,
      agents: simultaneous,
      socialRepulsionWeight: 0,
    });
    for (const offset of [8, 24])
      engine.device.queue.writeBuffer(
        engine.recoveryBuffer,
        offset,
        new Uint32Array([11249]),
      );
    for (let tick = 0; tick < 3; tick++) {
      await advance(1);
      const preview = await engine.capturePreview();
      const gap = Math.hypot(
        preview.values[0] - preview.values[1],
        preview.values[2] - preview.values[3],
      );
      check(gap >= 0.799, "Parallel recoveries overlap at tick " + tick);
    }
    check(
      (await engine.capturePreview()).values[0] > 30,
      "Reservation conflict did not retry recovery",
    );
    engine.dispose();
    return { errors, failures };
  }, fixtures);
  assert.deepEqual(errors, []);
  assert.deepEqual(result.errors, []);
  assert.deepEqual(result.failures, []);
});
