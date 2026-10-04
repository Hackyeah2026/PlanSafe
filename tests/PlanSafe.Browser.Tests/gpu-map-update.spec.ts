import { expect } from "@playwright/test";
import { test, readReference } from "./fixtures.js";
import type { EncodedSnapshot, GpuTestEngine } from "./gpu-types.js";
import type { MapGpuSnapshot } from "../../src/PlanSafe.App/TypeScript/crowdSimulatorGpu.js";

test("exit proximity filtering preserves GPU physics across map updates", async ({
  gpuPage: page,
}) => {
  const reference = readReference<{
    before: EncodedSnapshot;
    after: EncodedSnapshot;
    offsetX: number;
    offsetY: number;
  }>("MapEnvironmentUpdateTests", "PLANSAFE_MAP_UPDATE_REFERENCE");
  const result = await page.evaluate(async (reference) => {
    const module = "/crowdSimulatorGpu.js";
    const shaderModule = "/gpuPhysicsShader.js";
    const { GpuSimulationEngine } = (await import(
      module
    )) as typeof import("../../src/PlanSafe.App/TypeScript/crowdSimulatorGpu.js");
    const { stepPhysicsShader } = (await import(
      shaderModule
    )) as typeof import("../../src/PlanSafe.App/TypeScript/gpuPhysicsShader.js");
    const count = 200;
    const agents = new Float32Array(count * 8);
    const flags = new Uint32Array(agents.buffer);
    for (let index = 0; index < count; index++) {
      // Include either side of each square arrival boundary, plus distant cells.
      const edge = [-3.01, -3, -2.99, 0, 2.99, 3, 3.01][index % 7];
      agents[index * 8] = index < 98 ? 40 + edge : 2 + (index % 40);
      agents[index * 8 + 1] =
        index < 98 ? 10 + ((Math.floor(index / 7) % 7) - 3) : 30 + (index % 15);
      agents[index * 8 + 4] = 0.35;
      flags[index * 8 + 6] = 1;
    }
    const decode = (snapshot: EncodedSnapshot): MapGpuSnapshot => {
      const bytes = (value: string) =>
        Uint8Array.from(atob(value), (char) => char.charCodeAt(0));
      return {
        ...snapshot,
        count,
        granulation: 1,
        scenario: bytes(snapshot.scenario),
        agents: new Uint8Array(agents.buffer),
        fields: bytes(snapshot.fields),
        blocked: bytes(snapshot.blocked),
      };
    };
    const run = async (filterExits: boolean) => {
      const engine = new GpuSimulationEngine() as unknown as GpuTestEngine & {
        physicsPipeline: GPUComputePipeline;
        recreateSpatialBindGroups(): void;
      };
      await engine.boot();
      engine.device.pushErrorScope("validation");
      try {
        if (!filterExits) {
          engine.physicsPipeline = engine.device.createComputePipeline({
            layout: "auto",
            compute: {
              module: engine.device.createShaderModule({
                code: stepPhysicsShader.replace(
                  "nearExit && e < params.numExits",
                  "e < params.numExits",
                ),
              }),
              entryPoint: "main",
            },
          });
        }
        engine.initializeMap(decode(reference.before));
        await engine.advanceFixedTicks(1);
        const before = Array.from((await engine.capturePreview()).values);
        await engine.updateMap(
          decode(reference.after),
          reference.offsetX,
          reference.offsetY,
        );
        await engine.advanceFixedTicks(3);
        const after = Array.from((await engine.capturePreview()).values);
        const validation = await engine.device.popErrorScope();
        return { before, after, validation: validation?.message };
      } finally {
        engine.dispose();
      }
    };
    return { filtered: await run(true), exhaustive: await run(false) };
  }, reference);
  expect(result.filtered.validation).toBeUndefined();
  expect(result.exhaustive.validation).toBeUndefined();
  expect(result.filtered.before).toEqual(result.exhaustive.before);
  expect(result.filtered.after).toEqual(result.exhaustive.after);
});

test("repeated GPU map updates release replaced buffers", async ({
  gpuPage: page,
}) => {
  const reference = readReference<{
    before: EncodedSnapshot;
    after: EncodedSnapshot;
    offsetX: number;
    offsetY: number;
  }>("MapEnvironmentUpdateTests", "PLANSAFE_MAP_UPDATE_REFERENCE");
  const result = await page.evaluate(async (reference) => {
    const module = "/crowdSimulatorGpu.js";
    const { GpuSimulationEngine } = (await import(
      module
    )) as typeof import("../../src/PlanSafe.App/TypeScript/crowdSimulatorGpu.js");
    const live = new Map<GPUBuffer, number>();
    const createBuffer = GPUDevice.prototype.createBuffer;
    const destroyBuffer = GPUBuffer.prototype.destroy;
    GPUDevice.prototype.createBuffer = function (descriptor) {
      const buffer = createBuffer.call(this, descriptor);
      live.set(buffer, buffer.size);
      return buffer;
    };
    GPUBuffer.prototype.destroy = function () {
      live.delete(this);
      destroyBuffer.call(this);
    };
    const usage = () => ({
      count: live.size,
      bytes: [...live.values()].reduce((sum, size) => sum + size, 0),
    });
    const engine = new GpuSimulationEngine() as unknown as GpuTestEngine;
    try {
      await engine.boot();
      const errors: string[] = [];
      engine.device.addEventListener("uncapturederror", (event) =>
        errors.push(event.error.message),
      );
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
      engine.initializeMap(decode(reference.before));
      await engine.captureTelemetry();
      const initial = usage();
      const samples = [];
      for (let index = 0; index < 12; index++) {
        const expanding = index % 2 === 0;
        await engine.updateMap(
          decode(expanding ? reference.after : reference.before),
          expanding ? reference.offsetX : -reference.offsetX,
          expanding ? reference.offsetY : -reference.offsetY,
        );
        await engine.advanceFixedTicks(1);
        await engine.captureTelemetry();
        samples.push(usage());
      }
      await engine.device.queue.onSubmittedWorkDone();
      engine.dispose();
      return { initial, samples, disposed: usage(), errors };
    } finally {
      engine.dispose();
      GPUDevice.prototype.createBuffer = createBuffer;
      GPUBuffer.prototype.destroy = destroyBuffer;
    }
  }, reference);
  expect(result.errors).toEqual([]);
  for (let index = 2; index < result.samples.length; index++) {
    expect(result.samples[index]).toEqual(result.samples[index % 2]);
  }
  expect(result.samples[1]).toEqual(result.initial);
  expect(result.disposed).toEqual({ count: 0, bytes: 0 });
});

test("GPU terrain updates preserve agents, time, recovery and evacuation progress", async ({
  gpuPage: page,
}) => {
  const reference = readReference<{
    before: EncodedSnapshot;
    after: EncodedSnapshot;
    offsetX: number;
    offsetY: number;
  }>("MapEnvironmentUpdateTests", "PLANSAFE_MAP_UPDATE_REFERENCE");
  const result = await page.evaluate(async (reference) => {
    const module = "/crowdSimulatorGpu.js";
    const { GpuSimulationEngine } = (await import(
      module
    )) as typeof import("../../src/PlanSafe.App/TypeScript/crowdSimulatorGpu.js");
    const engine = new GpuSimulationEngine() as unknown as GpuTestEngine;
    await engine.boot();
    const errors: string[] = [];
    engine.device.addEventListener("uncapturederror", (event) =>
      errors.push(event.error.message),
    );
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
    const read = async (buffer: GPUBuffer) => {
      const staging = engine.device.createBuffer({
        size: buffer.size,
        usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ,
      });
      const encoder = engine.device.createCommandEncoder();
      encoder.copyBufferToBuffer(buffer, 0, staging, 0, buffer.size);
      engine.device.queue.submit([encoder.finish()]);
      await staging.mapAsync(GPUMapMode.READ);
      const values = Array.from(new Float32Array(staging.getMappedRange()));
      staging.unmap();
      staging.destroy();
      return values;
    };
    engine.initializeMap(decode(reference.before));
    await engine.advanceFixedTicks(10);
    const previewBefore = await engine.capturePreview();
    const statusBefore = engine.evacuationStatus();
    const agentsBefore = await read(engine.agentsBuffer);
    const recoveryBefore = await read(engine.recoveryBuffer);
    await engine.updateMap(
      decode(reference.after),
      reference.offsetX,
      reference.offsetY,
    );
    const statusAfter = engine.evacuationStatus();
    const previewAfter = await engine.capturePreview();
    const agentsAfter = await read(engine.agentsBuffer);
    const recoveryAfter = await read(engine.recoveryBuffer);
    const field = await read(engine.staticPotentialBuffer);
    await engine.advanceFixedTicks(1);
    const resumed = await engine.capturePreview();
    const validation = await engine.device.popErrorScope();
    if (validation) errors.push(validation.message);
    engine.dispose();
    return {
      statusBefore,
      statusAfter,
      tickBefore: previewBefore.tick,
      tickAfter: previewAfter.tick,
      resumedTick: resumed.tick,
      agentsBefore,
      agentsAfter,
      recoveryBefore,
      recoveryAfter,
      exitPotential: field[65 * reference.after.columns + 15],
      errors,
    };
  }, reference);
  expect(result.errors).toEqual([]);
  expect(result.statusBefore.evacuatedPeople).toBe(1);
  expect({ ...result.statusAfter, evacuatedPerExit: undefined }).toEqual({
    ...result.statusBefore,
    evacuatedPerExit: undefined,
  });
  expect(result.statusAfter.evacuatedPerExit).toEqual([
    result.statusBefore.evacuatedPerExit[0],
    0,
  ]);
  expect(result.tickAfter).toBe(result.tickBefore);
  expect(result.resumedTick).toBe(result.tickBefore + 1);
  expect(result.exitPotential).toBe(0);
  for (let agent = 0; agent < 3; agent++) {
    expect(result.agentsAfter[agent * 8]).toBeCloseTo(
      result.agentsBefore[agent * 8] + reference.offsetX,
      4,
    );
    expect(result.agentsAfter[agent * 8 + 1]).toBeCloseTo(
      result.agentsBefore[agent * 8 + 1] + reference.offsetY,
      4,
    );
    expect(result.agentsAfter.slice(agent * 8 + 2, agent * 8 + 8)).toEqual(
      result.agentsBefore.slice(agent * 8 + 2, agent * 8 + 8),
    );
    expect(result.recoveryAfter[agent * 4]).toBeCloseTo(
      result.recoveryBefore[agent * 4] + reference.offsetX,
      4,
    );
    expect(result.recoveryAfter[agent * 4 + 1]).toBeCloseTo(
      result.recoveryBefore[agent * 4 + 1] + reference.offsetY,
      4,
    );
    expect(result.recoveryAfter.slice(agent * 4 + 2, agent * 4 + 4)).toEqual(
      result.recoveryBefore.slice(agent * 4 + 2, agent * 4 + 4),
    );
  }
});
