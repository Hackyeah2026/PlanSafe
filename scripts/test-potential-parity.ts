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

// Optional hardware tests exercise the production WGSL pipelines and read back
// every cell. Set PLANSAFE_PLAYWRIGHT_MODULE to a Playwright module location.
test(
  "WebGPU shader fields match WASM reference fields",
  { skip: !process.env.PLANSAFE_PLAYWRIGHT_MODULE },
  async () => {
    const { chromium } = await import(process.env.PLANSAFE_PLAYWRIGHT_MODULE!);
    const { createServer } = await import("node:http");
    const server = createServer((req, res) => {
      if (req.url === "/") {
        res.end("<!doctype html><title>Potential-field parity</title>");
        return;
      }
      const filename = req.url?.split("/").pop();
      if (!filename || !/^[a-zA-Z]+\.js$/.test(filename)) {
        res.writeHead(404).end();
        return;
      }
      try {
        res.setHeader("Content-Type", "text/javascript");
        res.end(
          readFileSync(join(root, "src/PlanSafe.App/wwwroot/js", filename)),
        );
      } catch {
        res.writeHead(404).end();
      }
    });
    await new Promise<void>((resolve) =>
      server.listen(0, "127.0.0.1", resolve),
    );
    let browser;
    try {
      browser = await chromium.launch({
        headless: true,
        executablePath: process.env.PLANSAFE_BROWSER_PATH,
        args: [
          "--enable-unsafe-webgpu",
          "--disable-dawn-features=disallow_unsafe_apis",
        ],
      });
      const page = await browser.newPage();
      await page.goto(`http://127.0.0.1:${(server.address() as any).port}`);
      const results = await page.evaluate(async (refs) => {
        const { GpuSimulationEngine } = await import("/crowdSimulatorGpu.js");
        const { stepPhysicsShader: physicsWgsl } =
          await import("/gpuPhysicsShader.js");
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
          const engine = new GpuSimulationEngine();
          await engine.boot();
          const device = engine.device;
          const errors = [];
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
            {},
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
          async function read(buffer, size = ref.cols * ref.rows * 4) {
            const staging = device.createBuffer({
              size,
              usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ,
            });
            const encoder = device.createCommandEncoder();
            encoder.copyBufferToBuffer(buffer, 0, staging, 0, size);
            device.queue.submit([encoder.finish()]);
            await staging.mapAsync(GPUMapMode.READ);
            const values = Array.from(
              new Float32Array(staging.getMappedRange()),
            );
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
        for (const key of ["dynamicField", "density", "penalty"]) {
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
    } finally {
      await browser?.close();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
  },
);
