import assert from "node:assert/strict";
import { test, readReference } from "./fixtures.js";
import type { GpuTestEngine, FieldReference } from "./gpu-types.js";

let references: FieldReference[];
test.beforeAll(() => {
  references = readReference<FieldReference[]>(
    "PotentialFieldParityTests",
    "PLANSAFE_FIELD_REFERENCE",
  );
  assert.equal(references.length, 10);
  assert.ok(references.some((ref) => ref.name === "unlimited-zones"));
  for (const ref of references) {
    for (const key of [
      "staticField",
      "dynamicField",
      "density",
      "penalty",
    ] as const)
      ref[key] = ref[key].map(Math.fround);
  }
});
test.skip("WebGPU shader fields match WASM reference fields", async ({
  gpuPage: page,
}) => {
  const results = await page.evaluate(async (refs) => {
    const gpuModule = "/crowdSimulatorGpu.js";
    const { GpuSimulationEngine } = (await import(
      gpuModule
    )) as typeof import("../../src/PlanSafe.App/TypeScript/crowdSimulatorGpu.js");
    const physicsShaderModule = "/gpuPhysicsShader.js";
    const { stepPhysicsShader: physicsWgsl } = (await import(
      physicsShaderModule
    )) as typeof import("../../src/PlanSafe.App/TypeScript/gpuPhysicsShader.js");
    const flowShader =
      physicsWgsl.split("@compute")[0] +
      `
        @compute @workgroup_size(64)
        fn main(@builtin(global_invocation_id) id: vec3<u32>) {
          if (id.x >= arrayLength(&agents)) { return; }
          agents[id.x].vel = getFlowDirection(agents[id.x].pos);
        }`;
    const results = [];
    for (const ref of refs) {
      const protocolModule = "/crowdProtocol.js";
      const { ProfilingWindow } = (await import(
        protocolModule
      )) as typeof import("../../src/PlanSafe.App/TypeScript/crowdProtocol.js");
      const engine = new GpuSimulationEngine() as unknown as GpuTestEngine;
      await engine.boot();
      const device = engine.device;
      const errors: string[] = [];
      device.addEventListener("uncapturederror", (e) =>
        errors.push(e.error.message),
      );
      engine.dispatch(
        "parity",
        "init",
        {
          count: ref.xs.length * ref.granulation,
          granulation: ref.granulation,
          worldWidth: ref.width,
          worldHeight: ref.height,
          obstacles: ref.obstacles,
          targets: ref.targets,
          weightDistance: ref.weightDistance,
          weightOccupancy: ref.weightOccupancy,
        },
        new ProfilingWindow(),
      );
      const agents = new Float32Array(ref.xs.length * 8);
      const flags = new Uint32Array(agents.buffer);
      ref.xs.forEach((x, i) => {
        agents[i * 8] = x;
        agents[i * 8 + 1] = ref.ys[i];
        agents[i * 8 + 4] = 0.35;
        agents[i * 8 + 5] = 1.4;
        flags[i * 8 + 6] = 1;
      });
      device.queue.writeBuffer(engine.agentsBuffer, 0, agents);
      await engine.advanceFixedTicks(1);
      async function read(buffer: GPUBuffer, size = ref.cols * ref.rows * 4) {
        const staging = device.createBuffer({
          size,
          usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ,
        });
        const encoder = device.createCommandEncoder();
        encoder.copyBufferToBuffer(buffer, 0, staging, 0, size);
        device.queue.submit([encoder.finish()]);
        await staging.mapAsync(GPUMapMode.READ);
        const values = Array.from(new Float32Array(staging.getMappedRange()));
        staging.unmap();
        staging.destroy();
        return values;
      }
      const flowAgents = new Float32Array(ref.samples.length * 8);
      ref.samples.forEach((p, i) => {
        flowAgents[i * 8] = p[0];
        flowAgents[i * 8 + 1] = p[1];
      });
      const flowBuffer = device.createBuffer({
        size: flowAgents.byteLength,
        usage:
          GPUBufferUsage.STORAGE |
          GPUBufferUsage.COPY_DST |
          GPUBufferUsage.COPY_SRC,
      });
      device.queue.writeBuffer(flowBuffer, 0, flowAgents);
      const pipeline = device.createComputePipeline({
        layout: "auto",
        compute: {
          module: device.createShaderModule({ code: flowShader }),
          entryPoint: "main",
        },
      });
      const bindings = device.createBindGroup({
        layout: pipeline.getBindGroupLayout(0),
        entries: [
          { binding: 0, resource: { buffer: flowBuffer } },
          { binding: 3, resource: { buffer: engine.paramsBuffer } },
          { binding: 4, resource: { buffer: engine.potentialBufferA } },
          { binding: 7, resource: { buffer: engine.exitsBuffer } },
        ],
      });
      const encoder = device.createCommandEncoder();
      const pass = encoder.beginComputePass();
      pass.setPipeline(pipeline);
      pass.setBindGroup(0, bindings);
      pass.dispatchWorkgroups(1);
      pass.end();
      device.queue.submit([encoder.finish()]);
      const flowValues = await read(flowBuffer, flowAgents.byteLength);
      const flows = ref.samples.map((_, i) => [
        flowValues[i * 8 + 2],
        flowValues[i * 8 + 3],
      ]);
      flowBuffer.destroy();
      results.push({
        name: ref.name,
        errors,
        flows,
        staticField: await read(engine.staticPotentialBuffer),
        dynamicField: await read(engine.potentialBufferA),
        density: await read(engine.smoothedDensityBuffer),
        penalty: await read(engine.penaltyBuffer),
      });
      engine.dispose();
    }
    return results;
  }, references);
  for (let i = 0; i < references.length; i++) {
    const ref = references[i],
      actual = results[i];
    assert.deepEqual(actual.errors, [], ref.name);
    for (let cell = 0; cell < ref.staticField.length; cell++)
      assert.equal(
        actual.staticField[cell],
        ref.staticField[cell],
        `${ref.name} static cell ${cell}`,
      );
    for (const key of ["dynamicField", "density", "penalty"] as const) {
      for (let cell = 0; cell < ref[key].length; cell++) {
        const expected = ref[key][cell];
        const error = Math.abs(actual[key][cell] - expected);
        assert.ok(
          error <= Math.max(0.0001, Math.abs(expected) * 0.00001),
          `${ref.name} ${key}[${cell}]: GPU=${actual[key][cell]}, WASM=${expected}`,
        );
      }
    }
    for (let p = 0; p < ref.flows.length; p++) {
      for (let axis = 0; axis < 2; axis++)
        assert.ok(
          Math.abs(actual.flows[p][axis] - ref.flows[p][axis]) < 0.0001,
          `${ref.name} flow sample ${p} axis ${axis}: GPU=${actual.flows[p][axis]}, WASM=${ref.flows[p][axis]}`,
        );
    }
  }
});
