import { expect } from "@playwright/test";
import { test, readReference } from "./fixtures.js";
import type { EncodedSnapshot, GpuTestEngine } from "./gpu-types.js";
import type { MapGpuSnapshot } from "../../src/PlanSafe.App/TypeScript/crowdSimulatorGpu.js";

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
