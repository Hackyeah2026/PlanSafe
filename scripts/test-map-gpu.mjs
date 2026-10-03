import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createServer } from "node:http";
import { readFileSync, mkdtempSync, rmSync, rmdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

test(
  "GPU map playback starts from WASM state and preserves raster, exits and reset",
  {
    skip: !process.env.PLANSAFE_PLAYWRIGHT_MODULE,
  },
  async () => {
    const temp = mkdtempSync(join(tmpdir(), "plansafe-map-"));
    const referencePath = join(temp, "reference.json");
    let browser;
    const assets = fileURLToPath(
      new URL("../src/PlanSafe.App/wwwroot/js/", import.meta.url),
    );
    const server = createServer((req, res) => {
      if (req.url === "/")
        return res.end(
          '<!doctype html><canvas width="800" height="500"></canvas>',
        );
      const name = req.url?.slice(1);
      if (!name || !/^[a-zA-Z]+\.js$/.test(name))
        return res.writeHead(404).end();
      try {
        res.setHeader("Content-Type", "text/javascript");
        res.end(readFileSync(assets + name));
      } catch {
        res.writeHead(404).end();
      }
    });
    try {
      const run = spawnSync(
        "dotnet",
        [
          "test",
          "tests/PlanSafe.Tests",
          "--no-restore",
          "--filter",
          "FullyQualifiedName~MapGpuSnapshotTests",
          "--verbosity",
          "quiet",
        ],
        {
          cwd: fileURLToPath(new URL("../", import.meta.url)),
          env: { ...process.env, PLANSAFE_MAP_GPU_REFERENCE: referencePath },
          encoding: "utf8",
        },
      );
      assert.equal(run.status, 0, run.stdout + run.stderr);
      const fixtures = JSON.parse(readFileSync(referencePath, "utf8"));
      await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
      const { chromium } = await import(process.env.PLANSAFE_PLAYWRIGHT_MODULE);
      browser = await chromium.launch({
        headless: true,
        executablePath: process.env.PLANSAFE_BROWSER_PATH,
        args: [
          "--enable-unsafe-webgpu",
          "--disable-dawn-features=disallow_unsafe_apis",
        ],
      });
      const page = await browser.newPage();
      const errors = [];
      page.on("pageerror", (e) => errors.push(e.message));
      await page.goto(`http://127.0.0.1:${server.address().port}`);
      const results = await page.evaluate(async (fixtures) => {
        const { GpuSimulationEngine } = await import("/crowdSimulatorGpu.js");
        const { initMapSimulator } = await import("/crowdSimulatorInterop.js");
        const engine = new GpuSimulationEngine();
        await engine.boot();
        const gpuErrors = [];
        engine.device.addEventListener("uncapturederror", (e) =>
          gpuErrors.push(e.error.message),
        );
        engine.device.pushErrorScope("validation");
        const decode = (snapshot) =>
          Object.fromEntries(
            Object.entries(snapshot).map(([k, v]) => [
              k,
              ["scenario", "agents", "fields", "blocked"].includes(k)
                ? Uint8Array.from(atob(v), (c) => c.charCodeAt(0))
                : v,
            ]),
          );
        const read = async (buffer) => {
          const staging = engine.device.createBuffer({
            size: buffer.size,
            usage: GPUBufferUsage.MAP_READ | GPUBufferUsage.COPY_DST,
          });
          const encoder = engine.device.createCommandEncoder();
          encoder.copyBufferToBuffer(buffer, 0, staging, 0, buffer.size);
          engine.device.queue.submit([encoder.finish()]);
          await staging.mapAsync(GPUMapMode.READ);
          const bytes = new Uint8Array(staging.getMappedRange()).slice();
          staging.unmap();
          staging.destroy();
          return bytes;
        };
        const fieldError = async (snapshot) => {
          const expected = new Float32Array(snapshot.fields.buffer);
          const cells = snapshot.columns * snapshot.rows;
          let maxError = 0;
          for (const [lane, buffer] of [
            engine.staticPotentialBuffer,
            engine.potentialBufferA,
            engine.penaltyBuffer,
            engine.smoothedDensityBuffer,
          ].entries()) {
            const values = new Float32Array((await read(buffer)).buffer);
            for (let i = 0; i < cells; i++) {
              const target = expected[lane * cells + i];
              maxError = Math.max(
                maxError,
                Math.abs(values[i] - target) / Math.max(1, Math.abs(target)),
              );
            }
          }
          return maxError;
        };
        const activePositions = (bytes) => {
          const floats = new Float32Array(bytes.buffer),
            flags = new Uint32Array(bytes.buffer),
            points = [];
          for (let i = 0; i < floats.length / 8; i++)
            if (flags[i * 8 + 6] & 1)
              points.push([floats[i * 8], floats[i * 8 + 1]]);
          return points.sort((a, b) => a[0] - b[0] || a[1] - b[1]);
        };
        const cases = [];
        for (const fixture of fixtures) {
          const snapshot = decode(fixture.snapshot);
          engine.initializeMap(snapshot);
          const initialAgentsEqual = (await read(engine.agentsBuffer)).every(
            (value, i) => value === snapshot.agents[i],
          );
          const initialFieldError = await fieldError(snapshot);
          const exitCount = engine.numExits;
          const coarseGrid = engine.potCols < engine.mapCols;
          await engine.advanceFixedTicks(1);
          const telemetry = await engine.captureTelemetry();
          const actual = activePositions(await read(engine.agentsBuffer));
          const expected = activePositions(decode(fixture.afterOneTick).agents);
          // WASM compacts evacuated agents while GPU storage keeps their slots.
          const positionError =
            actual.length === expected.length
              ? Math.max(
                  ...actual.map((p) =>
                    Math.min(
                      ...expected.map((q) =>
                        Math.hypot(p[0] - q[0], p[1] - q[1]),
                      ),
                    ),
                  ),
                )
              : Infinity;
          await engine.advanceFixedTicks(63);
          const refinedFieldError = await fieldError(
            decode(fixture.afterRefinement),
          );
          const final = await engine.captureTelemetry();
          const wallsClear = activePositions(
            await read(engine.agentsBuffer),
          ).every(
            ([x, y]) =>
              !engine.mapScenario.blocked[
                Math.floor(y / engine.mapCellSize) * engine.mapCols +
                  Math.floor(x / engine.mapCellSize)
              ],
          );
          // Exercise the interop reset path, including a live weight change.
          const canvas = document.querySelector("canvas");
          const simulator = initMapSimulator(canvas, "map");
          await simulator.initMapGpu(snapshot);
          await simulator.stepGpu(2);
          await simulator.setGpuWeight(6);
          await simulator.resetMapGpu();
          const reset = await simulator.getGpuTelemetry();
          await simulator.renderGpu(
            "agents",
            false,
            2.5,
            snapshot.granulation,
            false,
          );
          canvas.style.display = "none";
          canvas.width = canvas.height = 0;
          await simulator.renderGpu(
            "agents",
            false,
            2.5,
            snapshot.granulation,
            false,
          );
          canvas.style.display = "block";
          canvas.width = 800;
          canvas.height = 500;
          simulator.dispose();
          cases.push({
            name: fixture.name,
            initialAgentsEqual,
            initialFieldError,
            exitCount,
            coarseGrid,
            telemetry,
            positionError,
            refinedFieldError,
            final,
            wallsClear,
            reset,
          });
        }
        const validation = await engine.device.popErrorScope();
        engine.dispose();
        return { cases, gpuErrors, validation: validation?.message };
      }, fixtures);
      assert.deepEqual(errors, []);
      assert.deepEqual(results.gpuErrors, []);
      assert.equal(results.validation, undefined);
      for (const result of results.cases) {
        assert.equal(result.initialAgentsEqual, true, result.name);
        assert.equal(result.initialFieldError, 0, result.name);
        assert.equal(result.exitCount, 2);
        assert.equal(result.coarseGrid, result.name === "coarse-potential");
        assert.equal(result.telemetry.evacuatedAgents, 3);
        assert.equal(result.telemetry.activeAgents, 34);
        assert.ok(
          result.positionError < 0.001,
          `${result.name}: first tick positions differ by ${result.positionError}`,
        );
        assert.ok(
          result.refinedFieldError < 0.001,
          `${result.name}: refined field differs by ${result.refinedFieldError}`,
        );
        assert.ok(Math.abs(result.final.simulationTime - 1.024) < 1e-6);
        assert.ok(Number.isFinite(result.final.meanSpeed));
        assert.equal(result.wallsClear, true);
        assert.equal(result.reset.simulationTime, 0);
        assert.equal(result.reset.activeAgents, 37);
        assert.equal(result.reset.evacuatedAgents, 0);
      }
    } finally {
      await browser?.close();
      if (server.listening)
        await new Promise((resolve) => server.close(resolve));
      rmSync(referencePath, { force: true });
      rmdirSync(temp);
    }
  },
);
