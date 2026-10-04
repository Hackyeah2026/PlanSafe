import assert from "node:assert/strict";
import { test, readReference } from "./fixtures.js";
import type {
  GpuTestEngine,
  EncodedSnapshot,
  MapReference,
} from "./gpu-types.js";
import type { MapGpuSnapshot } from "../../src/PlanSafe.App/TypeScript/crowdSimulatorGpu.js";

let fixtures: MapReference[];
test.beforeAll(() => {
  fixtures = readReference<MapReference[]>(
    "MapGpuSnapshotTests",
    "PLANSAFE_MAP_GPU_REFERENCE",
  );
  assert.equal(fixtures.length, 2);
});
test("GPU map playback starts from WASM state and preserves raster, exits and reset", async ({
  gpuPage: page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(e.message));
  const results = await page.evaluate(async (fixtures) => {
    const gpuModule = "/crowdSimulatorGpu.js";
    const { GpuSimulationEngine } = (await import(
      gpuModule
    )) as typeof import("../../src/PlanSafe.App/TypeScript/crowdSimulatorGpu.js");
    const interopModule = "/crowdSimulatorInterop.js";
    const { initMapSimulator } = (await import(
      interopModule
    )) as typeof import("../../src/PlanSafe.App/wwwroot/js/crowdSimulatorInterop.js");
    const engine = new GpuSimulationEngine() as unknown as GpuTestEngine;
    await engine.boot();
    const gpuErrors: string[] = [];
    engine.device.addEventListener("uncapturederror", (e) =>
      gpuErrors.push(e.error.message),
    );
    engine.device.pushErrorScope("validation");
    let relaxDispatches = 0;
    const createEncoder = engine.device.createCommandEncoder.bind(
      engine.device,
    );
    engine.device.createCommandEncoder = (...args) => {
      const encoder = createEncoder(...args);
      const beginPass = encoder.beginComputePass.bind(encoder);
      encoder.beginComputePass = (...args) => {
        const pass = beginPass(...args);
        const setPipeline = pass.setPipeline.bind(pass);
        const dispatch = pass.dispatchWorkgroups.bind(pass);
        let pipeline: GPUComputePipeline | undefined;
        pass.setPipeline = (value) => {
          pipeline = value;
          return setPipeline(value);
        };
        pass.dispatchWorkgroups = (...args) => {
          if (pipeline === engine.relaxStepPipeline) relaxDispatches++;
          return dispatch(...args);
        };
        return pass;
      };
      return encoder;
    };
    const decode = (snapshot: EncodedSnapshot): MapGpuSnapshot => {
      const decodeBytes = (value: string) =>
        Uint8Array.from(atob(value), (c) => c.charCodeAt(0));
      return {
        ...snapshot,
        scenario: decodeBytes(snapshot.scenario),
        agents: decodeBytes(snapshot.agents),
        fields: decodeBytes(snapshot.fields),
        blocked: decodeBytes(snapshot.blocked),
      };
    };
    const read = async (buffer: GPUBuffer) => {
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
    const fieldError = async (snapshot: MapGpuSnapshot) => {
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
    const activePositions = (bytes: Uint8Array) => {
      const floats = new Float32Array(bytes.buffer),
        flags = new Uint32Array(bytes.buffer),
        points: number[][] = [];
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
      const preparedRouteField = await read(engine.potentialBufferA);
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
                  ...expected.map((q) => Math.hypot(p[0] - q[0], p[1] - q[1])),
                ),
              ),
            )
          : Infinity;
      await engine.advanceFixedTicks(63);
      const refinedFieldError = await fieldError(
        decode(fixture.afterRefinement),
      );
      const final = await engine.captureTelemetry();
      const routeFieldUnchanged = (await read(engine.potentialBufferA)).every(
        (value, i) => value === preparedRouteField[i],
      );
      const wallsClear = activePositions(await read(engine.agentsBuffer)).every(
        ([x, y]) =>
          !engine.mapScenario.blocked[
            Math.floor(y / engine.mapCellSize) * engine.mapCols +
              Math.floor(x / engine.mapCellSize)
          ],
      );
      // Exercise the interop reset path, including a live weight change.
      const canvas = document.querySelector("canvas")!;
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
        routeFieldUnchanged,
        final,
        wallsClear,
        reset,
      });
    }
    const validation = await engine.device.popErrorScope();
    engine.dispose();
    return {
      cases,
      gpuErrors,
      validation: validation?.message,
      relaxDispatches,
    };
  }, fixtures);
  assert.deepEqual(errors, []);
  assert.deepEqual(results.gpuErrors, []);
  assert.equal(results.validation, undefined);
  assert.equal(
    results.relaxDispatches,
    0,
    "Map playback must not recalculate routing",
  );
  for (const result of results.cases) {
    assert.equal(result.initialAgentsEqual, true, result.name);
    assert.equal(result.initialFieldError, 0, result.name);
    assert.equal(result.routeFieldUnchanged, true, result.name);
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
});

test("EFMAP v1 and v2 keep exit metadata and spawn polygons aligned", async ({
  gpuPage: page,
}) => {
  const result = await page.evaluate(async (encoded) => {
    const module = "/crowdSimulatorGpu.js";
    const { parseMapScenario } = (await import(
      module
    )) as typeof import("../../src/PlanSafe.App/TypeScript/crowdSimulatorGpu.js");
    const version2 = Uint8Array.from(atob(encoded), (c) => c.charCodeAt(0));
    const view = new DataView(version2.buffer);
    // Five doubles and two raster dimensions follow the magic and version.
    const runs = view.getInt32(57, true);
    const exitCountOffset = 61 + runs * 4;
    const exits = view.getInt32(exitCountOffset, true);
    const exitStart = exitCountOffset + 4;
    view.setInt32(exitStart + 24, 300, true);
    view.setInt32(exitStart + 28, 12, true);
    view.setFloat64(exitStart + 32, 7.5, true);
    const parsed2 = parseMapScenario(version2);

    const version1 = new Uint8Array(version2.length - exits * 16);
    version1.set(version2.subarray(0, exitStart));
    new DataView(version1.buffer).setInt32(5, 1, true);
    for (let i = 0; i < exits; i++)
      version1.set(
        version2.subarray(exitStart + i * 40, exitStart + i * 40 + 24),
        exitStart + i * 24,
      );
    version1.set(
      version2.subarray(exitStart + exits * 40),
      exitStart + exits * 24,
    );
    return { parsed1: parseMapScenario(version1), parsed2 };
  }, fixtures[0].snapshot.scenario);
  assert.equal(result.parsed2.exits[0].capacity, 300);
  assert.equal(result.parsed2.exits[0].initialOccupancy, 12);
  assert.equal(result.parsed2.exits[0].basePotential, 7.5);
  assert.equal(result.parsed1.exits[0].capacity, 1000);
  assert.equal(result.parsed1.exits[0].initialOccupancy, 0);
  assert.equal(result.parsed1.exits[0].basePotential, 0);
  assert.deepEqual(result.parsed1.spawnZones, result.parsed2.spawnZones);
  assert.deepEqual(result.parsed1.blocked, result.parsed2.blocked);
  assert.equal(result.parsed1.totalPeople, 100);
});
