import { createServer } from "node:http";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
const { chromium } = await import(
  process.env.PLANSAFE_PLAYWRIGHT_MODULE ?? "playwright"
);
const assets = new URL("../../src/PlanSafe.App/wwwroot/js/", import.meta.url);
const outputDirectory = new URL("../../.run/simulation-perf/", import.meta.url);
const argument = process.argv[3];
if (process.argv.length > 4 || (argument && !argument.startsWith("--variant=")))
  throw Error("Usage: benchmark.mjs gpu [--variant=contact-cull|fine-grid]");
const variant = argument?.slice("--variant=".length) ?? "results";
if (!["results", "contact-cull", "fine-grid"].includes(variant))
  throw Error(`Unknown benchmark variant: ${variant}`);
const fixtures = [
  "sparse-10k",
  "dense-10k",
  "city-16k-g1",
  "city-16k-g3",
  "city-16k-g16",
].map((name) => {
  const path = new URL(`${name}.json`, outputDirectory);
  if (!existsSync(path))
    throw Error("Missing CPU snapshots. Run npm run benchmark:cpu first.");
  return { name, snapshot: JSON.parse(readFileSync(path, "utf8")) };
});
const sourceErrors = [];
function replaceOnce(source, marker, replacement) {
  if (source.split(marker).length !== 2)
    throw Error(`Experimental shader marker changed: ${marker}`);
  return source.replace(marker, replacement);
}
const server = createServer((req, res) => {
  if (req.url === "/")
    return res.end(
      '<!doctype html><canvas width="1000" height="700"></canvas>',
    );
  if (!/^\/[a-zA-Z]+\.js$/.test(req.url ?? "")) return res.writeHead(404).end();
  try {
    let source = readFileSync(new URL(req.url.slice(1), assets), "utf8");
    if (req.url === "/crowdSimulatorGpu.js" && variant === "contact-cull") {
      const marker = "let repulsionThreshold = minDist * 2.2;";
      source = replaceOnce(
        source,
        marker,
        marker +
          "\n                let contactRadius = max(repulsionThreshold, minDist + rearPushThreshold);\n                if (distSq > contactRadius * contactRadius || distSq < 0.000001) { continue; }",
      );
    }
    if (req.url === "/crowdSimulatorGpu.js" && variant === "fine-grid") {
      source = replaceOnce(
        source,
        "const targetCellSize = 4.0;",
        "const targetCellSize = this.granulation === 1 ? 2.8 : 4.0;",
      );
      source = replaceOnce(
        source,
        "const maxDimension = 250;",
        "const maxDimension = this.granulation === 1 ? 600 : 250;",
      );
    }
    res.setHeader("Content-Type", "text/javascript");
    res.end(source);
  } catch (error) {
    sourceErrors.push(error.message);
    res.writeHead(500).end();
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
  await page.goto(`http://127.0.0.1:${server.address().port}`);
  const output = await page.evaluate(async (fixtures) => {
    const adapter = await navigator.gpu.requestAdapter({
      powerPreference: "high-performance",
    });
    if (!adapter) throw Error("No GPU adapter");
    const info = adapter.info;
    const hasTimestamp = adapter.features.has("timestamp-query");
    const originalRequest = GPUAdapter.prototype.requestDevice;
    GPUAdapter.prototype.requestDevice = function (desc = {}) {
      return originalRequest.call(this, {
        ...desc,
        requiredFeatures: [
          ...(desc.requiredFeatures ?? []),
          ...(hasTimestamp ? ["timestamp-query"] : []),
        ],
      });
    };
    const { GpuSimulationEngine } = await import("/crowdSimulatorGpu.js");
    const decode = (s) =>
      Object.fromEntries(
        Object.entries(s).map(([k, v]) => [
          k[0].toLowerCase() + k.slice(1),
          ["Scenario", "Agents", "Fields", "Blocked"].includes(k)
            ? Uint8Array.from(atob(v), (c) => c.charCodeAt(0))
            : v,
        ]),
      );
    const engine = new GpuSimulationEngine();
    await engine.boot();
    const device = engine.device;
    const errors = [];
    device.addEventListener("uncapturederror", (e) =>
      errors.push(e.error.message),
    );
    const query = hasTimestamp
      ? device.createQuerySet({ type: "timestamp", count: 4096 })
      : null;
    const resolve = hasTimestamp
      ? device.createBuffer({
          size: 32768,
          usage: GPUBufferUsage.QUERY_RESOLVE | GPUBufferUsage.COPY_SRC,
        })
      : null;
    const readback = hasTimestamp
      ? device.createBuffer({
          size: 32768,
          usage: GPUBufferUsage.MAP_READ | GPUBufferUsage.COPY_DST,
        })
      : null;
    let capture = false,
      records = [],
      next = 0;
    const originalEncoder = device.createCommandEncoder.bind(device);
    device.createCommandEncoder = (...args) => {
      const encoder = originalEncoder(...args),
        begin = encoder.beginComputePass.bind(encoder);
      encoder.beginComputePass = (desc = {}) => {
        if (!capture || !query) return begin(desc);
        const index = next;
        next += 2;
        const record = { index, label: "unknown" };
        records.push(record);
        const pass = begin({
          ...desc,
          timestampWrites: {
            querySet: query,
            beginningOfPassWriteIndex: index,
            endOfPassWriteIndex: index + 1,
          },
        });
        const set = pass.setPipeline.bind(pass);
        pass.setPipeline = (pipeline) => {
          for (const name of [
            "clearPipeline",
            "binPipeline",
            "physicsPipeline",
            "clearDensityPipeline",
            "accumulateDensityPipeline",
            "decodeDensityPipeline",
            "smoothDensityOnlyPipeline",
            "updatePenaltyPipeline",
            "initRelaxPipeline",
            "relaxStepPipeline",
          ])
            if (pipeline === engine[name]) record.label = name;
          return set(pipeline);
        };
        return pass;
      };
      return encoder;
    };
    const cases = [];
    for (const fixture of fixtures) {
      engine.initializeMap(decode(fixture.snapshot));
      await engine.advanceFixedTicks(32);
      await engine.syncInFlight(0);
      const samples = [];
      for (let run = 0; run < 3; run++) {
        records = [];
        next = 0;
        capture = hasTimestamp;
        const started = performance.now();
        await engine.advanceFixedTicks(32);
        await engine.syncInFlight(0);
        const totalMs = performance.now() - started;
        capture = false;
        const phases = {};
        if (query) {
          const encoder = originalEncoder();
          encoder.resolveQuerySet(query, 0, next, resolve, 0);
          encoder.copyBufferToBuffer(resolve, 0, readback, 0, next * 8);
          device.queue.submit([encoder.finish()]);
          await readback.mapAsync(GPUMapMode.READ);
          const values = new BigUint64Array(readback.getMappedRange());
          for (const r of records)
            phases[r.label] =
              (phases[r.label] ?? 0) +
              Number(values[r.index + 1] - values[r.index]) / 1e6 / 32;
          readback.unmap();
        }
        samples.push({
          tickMs: totalMs / 32,
          phases,
          computePasses: records.length,
        });
      }
      const telemetryStart = performance.now();
      await engine.captureTelemetry();
      const telemetryMs = performance.now() - telemetryStart;
      const previewStart = performance.now();
      await engine.capturePreview();
      const previewMs = performance.now() - previewStart;
      const renderStart = performance.now();
      await engine.drawAgents(
        1000,
        700,
        0.6,
        0.6,
        0,
        0,
        3.2,
        fixture.snapshot.Granulation,
        false,
        2.5,
      );
      await engine.syncInFlight(0);
      const renderMs = performance.now() - renderStart;
      cases.push({
        name: fixture.name,
        slots: engine.activeCount,
        grid: [engine.gridCols, engine.gridRows],
        potentialGrid: [engine.potCols, engine.potRows],
        samples,
        telemetryMs,
        previewMs,
        renderMs,
      });
    }
    const { initMapSimulator } = await import("/crowdSimulatorInterop.js");
    const simulator = initMapSimulator(
      document.querySelector("canvas"),
      "perf-map",
    );
    const renderFixture = fixtures.find((f) => f.name === "city-16k-g1");
    await simulator.initMapGpu(decode(renderFixture.snapshot));
    const rendering = {};
    for (const mode of ["agents", "density", "heatmap"]) {
      await simulator.renderGpu(mode, false, 2.5, 1, false);
      rendering[mode] = [];
      for (let i = 0; i < 5; i++) {
        const start = performance.now();
        await simulator.renderGpu(mode, false, 2.5, 1, false);
        rendering[mode].push(performance.now() - start);
      }
    }
    simulator.dispose();
    engine.dispose();
    return {
      adapter: {
        vendor: info.vendor,
        architecture: info.architecture,
        device: info.device,
        description: info.description,
        isFallback: info.isFallbackAdapter,
      },
      hasTimestamp,
      cases,
      errors,
      rendering,
    };
  }, fixtures);
  writeFileSync(
    new URL(`gpu-${variant}.json`, outputDirectory),
    JSON.stringify(output, null, 2),
  );
  console.log(
    JSON.stringify(
      {
        adapter: output.adapter,
        hasTimestamp: output.hasTimestamp,
        cases: output.cases.map((c) => ({
          name: c.name,
          grid: c.grid,
          tickMs: c.samples.map((s) => s.tickMs),
          physicsMs: c.samples.map((s) => s.phases.physicsPipeline),
        })),
        errors: output.errors,
        rendering: output.rendering,
      },
      null,
      2,
    ),
  );
  if (output.errors.length || sourceErrors.length)
    throw Error(
      `GPU benchmark errors: ${[...output.errors, ...sourceErrors].join("; ")}`,
    );
} finally {
  await browser?.close();
  await new Promise((resolve) => server.close(resolve));
}
