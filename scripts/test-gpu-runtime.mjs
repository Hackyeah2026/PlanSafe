import test from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

test(
  "GPU telemetry, direct rendering and camera changes use the active GPU state",
  {
    skip: !process.env.PLANSAFE_PLAYWRIGHT_MODULE,
  },
  async () => {
    const { chromium } = await import(process.env.PLANSAFE_PLAYWRIGHT_MODULE);
    const assets = fileURLToPath(
      new URL("../src/PlanSafe.App/wwwroot/js/", import.meta.url),
    );
    const server = createServer((req, res) => {
      if (req.url === "/") {
        res.end('<!doctype html><canvas width="800" height="500"></canvas>');
        return;
      }
      const name = req.url?.slice(1);
      if (!name || !/^[a-zA-Z]+\.js$/.test(name)) {
        res.writeHead(404).end();
        return;
      }
      try {
        res.setHeader("Content-Type", "text/javascript");
        res.end(readFileSync(assets + name));
      } catch {
        res.writeHead(404).end();
      }
    });
    await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
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
      const errors = [];
      page.on("pageerror", (error) => errors.push(error.message));
      await page.goto(`http://127.0.0.1:${server.address().port}`);
      const result = await page.evaluate(async () => {
        const { GpuSimulationEngine } = await import("/crowdSimulatorGpu.js");
        const { initSimulator } = await import("/crowdSimulatorInterop.js");
        const { agentRenderMinRadius } = await import("/agentRenderSize.js");
        const { CrowdWebGLRenderer } = await import("/webglRenderer.js");
        const engine = new GpuSimulationEngine();
        await engine.boot();
        const device = engine.device;
        const gpuErrors = [];
        device.addEventListener("uncapturederror", (e) =>
          gpuErrors.push(e.error.message),
        );
        device.pushErrorScope("validation");
        engine.dispatch(
          "test",
          "init",
          { count: 197, granulation: 3, worldWidth: 200, worldHeight: 200 },
          {},
        );
        const values = new Float32Array(66 * 8);
        const flags = new Uint32Array(values.buffer);
        let sumSpeed = 0,
          sumSpeedSq = 0,
          sumDensity = 0,
          sumDensitySq = 0,
          peak = 0;
        for (let i = 0; i < 66; i++) {
          const active = i !== 15 && i !== 65;
          const speed = Math.fround(i / 33),
            density = Math.fround(i / 17);
          values[i * 8] = 1000;
          values[i * 8 + 1] = 1000;
          values[i * 8 + 2] = speed;
          values[i * 8 + 4] = active ? 2 : 0;
          flags[i * 8 + 6] = active ? 1 : 0;
          values[i * 8 + 7] = density;
          if (active) {
            sumSpeed += speed;
            sumSpeedSq += speed ** 2;
            sumDensity += density;
            sumDensitySq += density ** 2;
            peak = Math.max(peak, density);
          }
        }
        values[0] = 20;
        values[1] = 20;
        device.queue.writeBuffer(engine.agentsBuffer, 0, values);
        const telemetry = await engine.captureTelemetry();
        const expected = {
          meanSpeed: sumSpeed / 64,
          meanDensity: sumDensity / 64,
          speedStdDev: Math.sqrt(sumSpeedSq / 64 - (sumSpeed / 64) ** 2),
          densityStdDev: Math.sqrt(sumDensitySq / 64 - (sumDensity / 64) ** 2),
          peakDensity: peak,
        };
        const output = document.createElement("canvas");
        output.width = 200;
        output.height = 150;
        const ctx = output.getContext("2d");
        const surface = await engine.drawAgents(
          200,
          150,
          2,
          2,
          10,
          15,
          3.2,
          3,
          true,
          2.5,
        );
        ctx.drawImage(surface, 0, 0);
        const firstPixel = [...ctx.getImageData(50, 55, 1, 1).data];
        const emptyPixel = [...ctx.getImageData(90, 55, 1, 1).data];
        const zoomed = await engine.drawAgents(
          200,
          150,
          3,
          3,
          20,
          10,
          3.2,
          3,
          false,
          2.5,
        );
        ctx.clearRect(0, 0, 200, 150);
        ctx.drawImage(zoomed, 0, 0);
        const zoomedPixel = [...ctx.getImageData(80, 70, 1, 1).data];
        const oldPixel = [...ctx.getImageData(50, 55, 1, 1).data];
        // At overview zoom, neither the body nor cluster outline may retain
        // the old four-pixel radius. Check actual GPU pixel coverage.
        const overviewCoverage = [];
        for (const scale of [1, 0.25, 0.0625]) {
          const distant = await engine.drawAgents(
            200,
            150,
            scale,
            scale,
            50,
            50,
            agentRenderMinRadius(scale, scale, 4),
            3,
            false,
            2.5,
          );
          ctx.clearRect(0, 0, 200, 150);
          ctx.drawImage(distant, 0, 0);
          const center = 50 + 20 * scale;
          const pixels = ctx.getImageData(
            Math.floor(center) - 4,
            Math.floor(center) - 4,
            9,
            9,
          ).data;
          let coverage = 0;
          for (let i = 3; i < pixels.length; i += 4)
            coverage += pixels[i] / 255;
          overviewCoverage.push(coverage);
        }
        const glCanvas = document.createElement("canvas");
        glCanvas.width = 200;
        glCanvas.height = 150;
        const glRenderer = new CrowdWebGLRenderer(glCanvas);
        const glCoverage = [];
        const position = new Float32Array([20]);
        const zero = new Float32Array([0]);
        const speed = new Float32Array([1.4]);
        const active = new Uint8Array([1]);
        const options = {
          showDensityHeatmap: false,
          showFlowField: false,
          showSpawnZones: false,
          agentRadius: 0.35,
        };
        for (const scale of [1, 0.25, 0.0625]) {
          glRenderer.setGrid(
            200 / scale,
            150 / scale,
            1,
            0,
            0,
            new Uint8Array(0),
          );
          const render = (count) => {
            glRenderer.render(
              count,
              position,
              position,
              zero,
              zero,
              speed,
              zero,
              zero,
              active,
              options,
            );
            ctx.clearRect(0, 0, 200, 150);
            ctx.drawImage(glCanvas, 0, 0);
            return ctx.getImageData(0, 0, 200, 150).data;
          };
          const baseline = render(0);
          const pixels = render(1);
          let coverage = 0;
          for (let i = 0; i < pixels.length; i++)
            coverage += Math.abs(pixels[i] - baseline[i]);
          glCoverage.push(coverage);
        }
        const glError = glCanvas.getContext("webgl2").getError();
        // Default rendering and paused zoom must not request the full CPU preview.
        const original = GpuSimulationEngine.prototype.capturePreview;
        GpuSimulationEngine.prototype.capturePreview = () => {
          throw new Error("Unexpected agent readback");
        };
        const simulator = initSimulator(document.querySelector("canvas"));
        await simulator.initGpu(197, 3, 4.5, {
          worldWidth: 200,
          worldHeight: 200,
        });
        await simulator.renderGpu("agents", false, 2.5, 3, false);
        simulator.zoom(0.8);
        await new Promise((resolve) => setTimeout(resolve, 50));
        const initial = await simulator.getGpuTelemetry();
        await simulator.stepGpu(2);
        const stepped = await simulator.getGpuTelemetry();
        await simulator.resetGpu(197, 3, 4.5);
        const reset = await simulator.getGpuTelemetry();
        simulator.dispose();
        GpuSimulationEngine.prototype.capturePreview = original;
        const validation = await device.popErrorScope();
        engine.dispose();
        return {
          telemetry,
          expected,
          firstPixel,
          emptyPixel,
          zoomedPixel,
          oldPixel,
          overviewCoverage,
          glCoverage,
          glError,
          initial,
          stepped,
          reset,
          gpuErrors,
          validation: validation?.message,
        };
      });
      assert.deepEqual(errors, []);
      assert.deepEqual(result.gpuErrors, []);
      assert.equal(result.validation, undefined);
      assert.equal(result.glError, 0);
      assert.ok(result.glCoverage[0] > result.glCoverage[1]);
      assert.ok(result.glCoverage[1] > result.glCoverage[2]);
      assert.ok(result.overviewCoverage[0] > result.overviewCoverage[1]);
      assert.ok(
        result.overviewCoverage[1] > result.overviewCoverage[2],
        JSON.stringify(result.overviewCoverage),
      );
      assert.ok(
        result.overviewCoverage[2] > 0 && result.overviewCoverage[2] < 3,
      );
      assert.equal(result.telemetry.activeDots, 64);
      assert.equal(result.telemetry.evacuatedAgents, 6);
      assert.equal(result.telemetry.activeAgents, 191);
      for (const [key, expected] of Object.entries(result.expected)) {
        assert.ok(
          Math.abs(result.telemetry[key] - expected) < 0.00001,
          `${key}: ${result.telemetry[key]} vs ${expected}`,
        );
      }
      assert.ok(
        result.firstPixel[3] > 240,
        `Agent missing: ${result.firstPixel}`,
      );
      assert.equal(result.emptyPixel[3], 0);
      assert.ok(
        result.zoomedPixel[3] > 240,
        `Zoomed agent missing: ${result.zoomedPixel}`,
      );
      assert.equal(result.oldPixel[3], 0);
      assert.equal(result.initial.activeDots, 66);
      assert.equal(result.initial.simulationTime, 0);
      assert.ok(Math.abs(result.stepped.simulationTime - 0.032) < 1e-7);
      assert.equal(result.reset.simulationTime, 0);
      assert.equal(result.reset.evacuatedAgents, 0);
    } finally {
      await browser?.close();
      await new Promise((resolve) => server.close(resolve));
    }
  },
);
