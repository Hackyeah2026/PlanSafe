/// <reference types="@webgpu/types" />

import {
  impassablePotential,
  rasterizeObstacles,
  seedPotentialField,
  solvePotentialField,
  type PotentialSink,
  type Rectangle,
} from "./potentialField.js";

import type { CoreCommandValues } from "./crowdCoreCommand.js";
import { GpuTelemetryReader } from "./gpuTelemetryReader.js";
import { GpuAgentRenderer } from "./gpuAgentRenderer.js";
import {
  type BrowserCoreCommand,
  type ISimulationEngine,
  type OwnedPreview,
} from "./crowdRuntimeBridge.js";
import {
  protocolVersion,
  type DensityGridData,
  type EvacuationStatus,
  type ProfilingWindow,
} from "./crowdProtocol.js";

import { clearGridShader, binAgentsShader } from "./gpuSpatialShaders.js";
import {
  clearDensityShader,
  accumulateDensityShader,
  decodeDensityShader,
  smoothDensityOnlyShader,
  updatePenaltyShader,
} from "./gpuDensityShaders.js";
import {
  kdeClearShader,
  kdeDepositShader,
  kdeSmoothKernelShader,
} from "./gpuHeatmapShaders.js";
import { initRelaxShader, relaxStepShader } from "./gpuPotentialShaders.js";
import { stepPhysicsShader } from "./gpuPhysicsShader.js";
import {
  buildPotentialField,
  parseMapScenario,
  buildMapPotentialField,
  spawnMapAgents,
  type ParsedMapScenario,
} from "./gpuMapScenario.js";

export function isWebGpuSupported(): boolean {
  return (
    typeof navigator !== "undefined" &&
    typeof navigator.gpu !== "undefined" &&
    typeof navigator.gpu.requestAdapter === "function"
  );
}

export interface MapGpuSnapshot {
  count: number;
  granulation: number;
  socialRepulsionWeight: number;
  columns: number;
  rows: number;
  cellSize: number;
  scenario: Uint8Array;
  agents: Uint8Array;
  fields: Uint8Array;
  blocked: Uint8Array;
}

export {
  buildPotentialField,
  parseMapScenario,
  isPointInPolygon,
  buildMapPotentialField,
  spawnMapAgents,
} from "./gpuMapScenario.js";
export type {
  ParsedMapExit,
  ParsedMapSpawnZone,
  ParsedMapScenario,
} from "./gpuMapScenario.js";

// --- GPU Simulation Engine Implementation ---

export class GpuSimulationEngine implements ISimulationEngine {
  private device: GPUDevice | undefined;

  // Compute pipelines
  private clearPipeline: GPUComputePipeline | undefined;
  private binPipeline: GPUComputePipeline | undefined;
  private clearDensityPipeline: GPUComputePipeline | undefined;
  private accumulateDensityPipeline: GPUComputePipeline | undefined;
  private decodeDensityPipeline: GPUComputePipeline | undefined;
  private updatePenaltyPipeline: GPUComputePipeline | undefined;
  private smoothDensityOnlyPipeline: GPUComputePipeline | undefined;
  private initRelaxPipeline: GPUComputePipeline | undefined;
  private relaxStepPipeline: GPUComputePipeline | undefined;
  private physicsPipeline: GPUComputePipeline | undefined;

  // GPU Buffers
  private agentsBuffer: GPUBuffer | undefined;
  private recoveryBuffer: GPUBuffer | undefined;
  private mapRecoveryAvailable = false;
  private cellCountsBuffer: GPUBuffer | undefined;
  private cellAgentsBuffer: GPUBuffer | undefined;
  private paramsBuffer: GPUBuffer | undefined;
  private rawDensityBuffer: GPUBuffer | undefined;
  private smoothedDensityBuffer: GPUBuffer | undefined;
  private densityScratchBuffer: GPUBuffer | undefined;
  private seedBuffer: GPUBuffer | undefined;
  private protectedBuffer: GPUBuffer | undefined;
  private relaxStateBuffer: GPUBuffer | undefined;
  private relaxStateStaging: GPUBuffer | undefined;
  private penaltyBuffer: GPUBuffer | undefined;
  private blockedBuffer: GPUBuffer | undefined;
  private staticPotentialBuffer: GPUBuffer | undefined;
  private potentialBufferA: GPUBuffer | undefined;
  private potentialBufferB: GPUBuffer | undefined;
  private stagingBuffer: GPUBuffer | undefined;

  // Bind groups
  private clearBindGroup: GPUBindGroup | undefined;
  private binBindGroup: GPUBindGroup | undefined;
  private densityBindGroup: GPUBindGroup | undefined;
  private accumulateDensityBindGroup: GPUBindGroup | undefined;
  private decodeDensityBindGroup: GPUBindGroup | undefined;
  private smoothDensityReverseBindGroup: GPUBindGroup | undefined;
  private penaltyBindGroup: GPUBindGroup | undefined;
  private smoothDensityOnlyBindGroup: GPUBindGroup | undefined;
  private initRelaxBindGroup: GPUBindGroup | undefined;
  private relaxBindGroupA: GPUBindGroup | undefined;
  private relaxBindGroupB: GPUBindGroup | undefined;
  private bindGroup: GPUBindGroup | undefined;

  // Dedicated KDE Compute Pipelines and Buffers (Adaptive Grid & Smooth Kernel)
  private kdeClearPipeline: GPUComputePipeline | undefined;
  private kdeDepositPipeline: GPUComputePipeline | undefined;
  private kdeSmoothKernelPipeline: GPUComputePipeline | undefined;
  private kdeParamsBuffer: GPUBuffer | undefined;
  private kdeDepositMassBuffer: GPUBuffer | undefined;
  private kdeDepositSpeedBuffer: GPUBuffer | undefined;
  private kdeDensityOutputBuffer: GPUBuffer | undefined;
  private kdeStagingBuffer: GPUBuffer | undefined;
  private kdeClearBindGroup: GPUBindGroup | undefined;
  private kdeDepositBindGroup: GPUBindGroup | undefined;
  private kdeKernelBindGroup: GPUBindGroup | undefined;
  private kdeWidth = 256;
  private kdeHeight = 256;
  private currentRenderMode = "agents";

  private activeCount = 0;
  private rawCount = 1000;
  private activeSession: string | undefined;
  private activeRunGeneration = 1;
  private activeTick = 0;
  private dispatchSequence = 0;
  private dynamicFieldTimer = 0.2;
  private initialDensityPending = false;
  private inFlightBatches = 0;
  private inFlightWorkPromise: Promise<void> | null = null;
  private tickWork: Promise<number> = Promise.resolve(0);

  private worldWidth = 200;
  private worldHeight = 200;
  private socialWeight = 4.5;
  private granulation = 1;

  private gridCols = 50;
  private gridRows = 50;
  private readonly maxPerCell = 128;

  private exitsBuffer: GPUBuffer | undefined;
  private mapBlockedBuffer: GPUBuffer | undefined;
  private isMapScenario = false;
  private mapScenario: ParsedMapScenario | null = null;
  private mapCols = 100;
  private mapRows = 100;
  private mapCellSize = 2.0;
  private potCols = 100;
  private potRows = 100;
  private potCellSize = 2.0;
  private numExits = 1;
  private totalPeople = 0;
  private activeMapAgents = 0;
  private evacuatedCount = 0;
  private evacuatedPerExit: number[] = [];
  private evacuationTimes: number[] = [];
  private lastEvacuationTime = 0;
  private simulationTime = 0;
  private agentEvacuated: Uint8Array | undefined;

  private cpuValues: Float64Array | undefined;
  private isCapturing = false;
  private agentRenderer?: GpuAgentRenderer;
  private telemetryReader?: GpuTelemetryReader;

  async drawAgents(
    width: number,
    height: number,
    scaleX: number,
    scaleY: number,
    offsetX: number,
    offsetY: number,
    minRadius: number,
    granulation: number,
    showWhiskers: boolean,
    whiskerLength: number,
  ): Promise<HTMLCanvasElement> {
    await this.tickWork;
    if (!this.device || !this.agentsBuffer)
      throw new Error("GPU engine is not initialized.");
    this.agentRenderer ??= new GpuAgentRenderer(this.device);
    return this.agentRenderer.draw(
      this.agentsBuffer,
      this.activeCount,
      width,
      height,
      scaleX,
      scaleY,
      offsetX,
      offsetY,
      minRadius,
      granulation,
      showWhiskers,
      whiskerLength,
    );
  }

  /** Reads compact GPU moments without copying the full agent state. */
  async captureTelemetry() {
    await this.tickWork;
    if (!this.device || !this.agentsBuffer)
      throw new Error("GPU engine is not initialized.");
    this.telemetryReader ??= new GpuTelemetryReader(this.device);
    return this.telemetryReader.capture(
      this.agentsBuffer,
      this.paramsBuffer!,
      this.activeCount,
      (moments) => {
        const {
          activeDots,
          totalSpeed,
          totalSpeedSquared,
          totalDensity,
          totalDensitySquared,
          peakDensity,
        } = moments;
        const meanSpeed = activeDots ? totalSpeed / activeDots : 0;
        const meanDensity = activeDots ? totalDensity / activeDots : 0;
        const evacuated = this.activeCount - activeDots;
        const evacuatedPeople =
          activeDots === 0
            ? this.rawCount
            : Math.min(this.rawCount, evacuated * this.granulation);
        return {
          simulationTime: this.simulationTime,
          activeDots,
          activeAgents: this.rawCount - evacuatedPeople,
          evacuatedAgents: evacuatedPeople,
          meanSpeed,
          meanDensity,
          peakDensity,
          speedStdDev: activeDots
            ? Math.sqrt(
                Math.max(0, totalSpeedSquared / activeDots - meanSpeed ** 2),
              )
            : 0,
          densityStdDev: activeDots
            ? Math.sqrt(
                Math.max(
                  0,
                  totalDensitySquared / activeDots - meanDensity ** 2,
                ),
              )
            : 0,
        };
      },
    );
  }

  get count(): number {
    return this.activeCount;
  }

  private obs1: [number, number, number, number] = [70, 36, 24, 56];
  private obs2: [number, number, number, number] = [70, 108, 24, 56];
  private exitZone: [number, number, number, number] = [184, 80, 12, 40];
  private sinks: PotentialSink[] = [];
  private weightDistance = 1.0;
  private weightOccupancy = 0.0;
  private multipleTargets = false;

  private parseObstacle(o: any): [number, number, number, number] | null {
    if (!o) return null;
    if (Array.isArray(o) && o.length >= 4) {
      return [Number(o[0]), Number(o[1]), Number(o[2]), Number(o[3])];
    }
    const x = Number(o.x ?? o.X);
    const y = Number(o.y ?? o.Y);
    const w = Number(o.width ?? o.Width ?? o.w ?? o.W);
    const h = Number(o.height ?? o.Height ?? o.h ?? o.H);
    if (
      Number.isFinite(x) &&
      Number.isFinite(y) &&
      Number.isFinite(w) &&
      Number.isFinite(h)
    ) {
      return [x, y, w, h];
    }
    return null;
  }

  private applyEnvironment(values?: CoreCommandValues): void {
    if (this.isMapScenario) return;
    const rawObs = (values as any)?.obstacles;
    if (Array.isArray(rawObs) && rawObs.length > 0) {
      const o0 = this.parseObstacle(rawObs[0]);
      if (o0) this.obs1 = o0;
      if (rawObs.length > 1) {
        const o1 = this.parseObstacle(rawObs[1]);
        if (o1) this.obs2 = o1;
      }
    } else if (this.worldWidth === 60 && this.worldHeight === 40) {
      // Hala 60m preset matching C# InitDefaultEnvironment()
      this.obs1 = [20.0, 0.0, 8.0, 14.0];
      this.obs2 = [20.0, 26.0, 8.0, 14.0];
    } else {
      // Default formula for 200m, 500m, 1000m presets
      this.obs1 = [
        this.worldWidth * 0.35,
        this.worldHeight * 0.18,
        this.worldWidth * 0.12,
        this.worldHeight * 0.28,
      ];
      this.obs2 = [
        this.worldWidth * 0.35,
        this.worldHeight * 0.54,
        this.worldWidth * 0.12,
        this.worldHeight * 0.28,
      ];
    }

    const rawExit = (values as any)?.exitZone;
    const rawTargets = (values as any)?.targets;
    const parsedExit = this.parseObstacle(rawExit);
    if (parsedExit) {
      this.exitZone = parsedExit;
    } else if (Array.isArray(rawTargets) && rawTargets.length > 0) {
      const parsedTgt = this.parseObstacle(rawTargets[0]);
      if (parsedTgt) this.exitZone = parsedTgt;
    } else if (this.worldWidth === 60 && this.worldHeight === 40) {
      this.exitZone = [56.0, 15.0, 4.0, 10.0];
    } else {
      this.exitZone = [
        this.worldWidth * 0.92,
        this.worldHeight * 0.4,
        this.worldWidth * 0.06,
        this.worldHeight * 0.2,
      ];
    }
    if (typeof values?.weightDistance === "number")
      this.weightDistance = values.weightDistance;
    if (typeof values?.weightOccupancy === "number")
      this.weightOccupancy = values.weightOccupancy;
    const active = Array.isArray(rawTargets)
      ? rawTargets.filter(
          (t: any) =>
            (t.isActive ?? t.IsActive ?? true) && this.parseObstacle(t),
        )
      : [];
    const hasCapacity = active.some(
      (t: any) =>
        Number(t.capacity ?? t.Capacity ?? 1000) <= 0 ||
        Number(t.currentOccupancy ?? t.CurrentOccupancy ?? 0) <
          Number(t.capacity ?? t.Capacity ?? 1000),
    );
    const openZoneScale = Math.max(
      1,
      active.reduce(
        (total: number, t: any) =>
          total +
          (Number(t.capacity ?? t.Capacity ?? 1000) <= 0
            ? Math.max(0, Number(t.currentOccupancy ?? t.CurrentOccupancy ?? 0))
            : 0),
        0,
      ),
    );
    this.sinks = active.map((t: any) => {
      const rawCapacity = Number(t.capacity ?? t.Capacity ?? 1000);
      const cap = rawCapacity > 0 ? rawCapacity : openZoneScale;
      const occupancy = Number(t.currentOccupancy ?? t.CurrentOccupancy ?? 0);
      const base =
        ((Math.max(0, this.weightOccupancy) /
          Math.max(0.001, this.weightDistance)) *
          Math.max(1, this.worldWidth) *
          occupancy) /
        cap;
      return {
        zone: this.parseObstacle(t)!,
        potential: Math.fround(
          base +
            (rawCapacity > 0 && occupancy >= cap && hasCapacity ? 100000 : 0),
        ),
      };
    });
    if (!Array.isArray(rawTargets) || rawTargets.length === 0)
      this.sinks = [{ zone: this.exitZone, potential: 0 }];
    this.numExits = this.sinks.length;
    this.multipleTargets = Array.isArray(rawTargets) && rawTargets.length > 1;
  }

  private getAgentRadius(granulation: number): number {
    return granulation > 1
      ? 0.35 * (1.0 + 0.2 * Math.sqrt(granulation - 1))
      : 0.35;
  }

  async boot(_runtimeUrl?: string): Promise<void> {
    if (this.device) return;
    if (!isWebGpuSupported()) {
      throw new Error("WebGPU is not supported in this environment.");
    }

    // On Windows, requesting adapter with powerPreference logs a Chromium warning (crbug.com/369219127).
    const isWindows =
      typeof navigator !== "undefined" &&
      (/Win/i.test(navigator.userAgent || "") ||
        /Win/i.test((navigator as any).userAgentData?.platform || "") ||
        /Win/i.test(navigator.platform || ""));

    let adapter: GPUAdapter | null = null;
    if (!isWindows) {
      adapter = await navigator.gpu.requestAdapter({
        powerPreference: "high-performance",
      });
    }
    if (!adapter) {
      adapter = await navigator.gpu.requestAdapter();
    }
    if (!adapter) throw new Error("No suitable GPU adapter found.");

    this.device = await adapter.requestDevice();

    // Prevent uncaptured errors from bubbling to Worker.onerror
    this.device.onuncapturederror = (event: GPUUncapturedErrorEvent) => {
      console.warn(
        "WebGPU uncaptured device error:",
        event.error?.message ?? event.error,
      );
    };

    this.device.pushErrorScope("validation");

    // 1. Shaders & Pipelines
    const clearModule = this.device.createShaderModule({
      code: clearGridShader,
    });
    const binModule = this.device.createShaderModule({ code: binAgentsShader });
    const clearDensityModule = this.device.createShaderModule({
      code: clearDensityShader,
    });
    const accDensityModule = this.device.createShaderModule({
      code: accumulateDensityShader,
    });
    const decodeDensityModule = this.device.createShaderModule({
      code: decodeDensityShader,
    });
    this.decodeDensityPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: decodeDensityModule, entryPoint: "main" },
    });
    const updatePenaltyModule = this.device.createShaderModule({
      code: updatePenaltyShader,
    });
    const smoothDensityOnlyModule = this.device.createShaderModule({
      code: smoothDensityOnlyShader,
    });
    const initRelaxModule = this.device.createShaderModule({
      code: initRelaxShader,
    });
    const relaxStepModule = this.device.createShaderModule({
      code: relaxStepShader,
    });
    const physicsModule = this.device.createShaderModule({
      code: stepPhysicsShader,
    });

    this.clearPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: clearModule, entryPoint: "main" },
    });
    this.binPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: binModule, entryPoint: "main" },
    });
    this.clearDensityPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: clearDensityModule, entryPoint: "main" },
    });
    this.accumulateDensityPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: accDensityModule, entryPoint: "main" },
    });
    this.updatePenaltyPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: updatePenaltyModule, entryPoint: "main" },
    });
    this.smoothDensityOnlyPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: smoothDensityOnlyModule, entryPoint: "main" },
    });
    this.initRelaxPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: initRelaxModule, entryPoint: "main" },
    });
    this.relaxStepPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: relaxStepModule, entryPoint: "main" },
    });
    this.physicsPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: physicsModule, entryPoint: "main" },
    });

    const kdeClearModule = this.device.createShaderModule({
      code: kdeClearShader,
    });
    const kdeDepositModule = this.device.createShaderModule({
      code: kdeDepositShader,
    });
    const kdeSmoothKernelModule = this.device.createShaderModule({
      code: kdeSmoothKernelShader,
    });

    this.kdeClearPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: kdeClearModule, entryPoint: "main" },
    });
    this.kdeDepositPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: kdeDepositModule, entryPoint: "main" },
    });
    this.kdeSmoothKernelPipeline = this.device.createComputePipeline({
      layout: "auto",
      compute: { module: kdeSmoothKernelModule, entryPoint: "main" },
    });

    const validationError = await this.device.popErrorScope();
    if (validationError) {
      throw new Error(
        `WebGPU pipeline initialization failed: ${validationError.message}`,
      );
    }

    // 2. Spatial Grid Buffers (Adaptive grid based on physical cell size ~4.0m)
    this.ensureSpatialGrid();

    // 2b. Adaptive KDE Grid Buffers
    this.ensureKdeGrid();

    // 3. SimParams Uniform Buffer (128 bytes: aligned to 16 bytes)
    this.paramsBuffer = this.device.createBuffer({
      size: 128,
      usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
    });

    // 4. Density & Dynamic Potential Buffers (100x100 = 10,000 cells)
    const potCells = 100 * 100;
    this.rawDensityBuffer = this.device.createBuffer({
      size: potCells * 4,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    this.smoothedDensityBuffer = this.device.createBuffer({
      size: potCells * 4,
      usage:
        GPUBufferUsage.STORAGE |
        GPUBufferUsage.COPY_DST |
        GPUBufferUsage.COPY_SRC,
    });
    this.densityScratchBuffer = this.device.createBuffer({
      size: potCells * 4,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    this.seedBuffer = this.device.createBuffer({
      size: potCells * 4,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    this.protectedBuffer = this.device.createBuffer({
      size: potCells * 4,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    this.relaxStateBuffer = this.device.createBuffer({
      size: 8,
      usage:
        GPUBufferUsage.STORAGE |
        GPUBufferUsage.COPY_SRC |
        GPUBufferUsage.COPY_DST,
    });
    this.relaxStateStaging = this.device.createBuffer({
      size: 8,
      usage: GPUBufferUsage.MAP_READ | GPUBufferUsage.COPY_DST,
    });
    this.penaltyBuffer = this.device.createBuffer({
      size: potCells * 4,
      usage:
        GPUBufferUsage.STORAGE |
        GPUBufferUsage.COPY_DST |
        GPUBufferUsage.COPY_SRC,
    });
    this.blockedBuffer = this.device.createBuffer({
      size: potCells * 4,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    this.staticPotentialBuffer = this.device.createBuffer({
      size: potCells * 4,
      usage:
        GPUBufferUsage.STORAGE |
        GPUBufferUsage.COPY_SRC |
        GPUBufferUsage.COPY_DST,
    });
    this.potentialBufferA = this.device.createBuffer({
      size: potCells * 4,
      usage:
        GPUBufferUsage.STORAGE |
        GPUBufferUsage.COPY_DST |
        GPUBufferUsage.COPY_SRC,
    });
    this.potentialBufferB = this.device.createBuffer({
      size: potCells * 4,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });

    this.exitsBuffer = this.device.createBuffer({
      size: 64,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    this.mapBlockedBuffer = this.device.createBuffer({
      size: 64,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    this.applyEnvironment();
  }

  private updatePotentialGrid(): void {
    if (this.isMapScenario) {
      return;
    }
    if (
      !this.device ||
      !this.staticPotentialBuffer ||
      !this.potentialBufferA ||
      !this.blockedBuffer ||
      !this.penaltyBuffer ||
      !this.paramsBuffer
    ) {
      return;
    }

    this.potCellSize = Math.max(
      2.0,
      Math.max(this.worldWidth, this.worldHeight) / 100.0,
    );
    this.potCols = Math.max(5, Math.ceil(this.worldWidth / this.potCellSize));
    this.potRows = Math.max(5, Math.ceil(this.worldHeight / this.potCellSize));
    const totalCells = this.potCols * this.potRows;

    const blockedData = rasterizeObstacles(
      this.potCols,
      this.potRows,
      this.potCellSize,
      [this.obs1, this.obs2],
    );
    const seeds = seedPotentialField(
      this.potCols,
      this.potRows,
      this.potCellSize,
      blockedData,
      this.sinks,
    );
    // C# ignores the base cost when refining a single target under congestion.
    const dynamicSeeds =
      !this.multipleTargets && this.sinks.length === 1
        ? seedPotentialField(
            this.potCols,
            this.potRows,
            this.potCellSize,
            blockedData,
            [{ zone: this.sinks[0].zone, potential: 0 }],
          )
        : seeds;
    const grid = solvePotentialField(
      this.potCols,
      this.potRows,
      this.potCellSize,
      blockedData,
      seeds,
    );
    const protectedSeeds = seedPotentialField(
      this.potCols,
      this.potRows,
      this.potCellSize,
      blockedData,
      this.sinks,
      2.5,
    );
    const protectedData = Uint32Array.from(protectedSeeds, (v) =>
      v < impassablePotential ? 1 : 0,
    );
    this.device.queue.writeBuffer(this.staticPotentialBuffer, 0, grid.buffer);
    this.device.queue.writeBuffer(this.potentialBufferA, 0, grid.buffer);
    this.device.queue.writeBuffer(this.blockedBuffer, 0, blockedData.buffer);
    this.device.queue.writeBuffer(this.seedBuffer!, 0, dynamicSeeds.buffer);
    this.device.queue.writeBuffer(
      this.protectedBuffer!,
      0,
      protectedData.buffer,
    );
    this.exitsBuffer?.destroy();
    const exitData = new Float32Array(Math.max(1, this.sinks.length) * 4);
    this.sinks.forEach((sink, i) => exitData.set(sink.zone, i * 4));
    this.exitsBuffer = this.device.createBuffer({
      size: Math.max(64, exitData.byteLength),
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    this.device.queue.writeBuffer(this.exitsBuffer, 0, exitData.buffer);

    // Reset penalties to 0
    const zeroPenalties = new Float32Array(totalCells);
    this.device.queue.writeBuffer(this.penaltyBuffer, 0, zeroPenalties.buffer);
    this.dynamicFieldTimer = 0.2;

    this.recreatePotentialBindGroups();
  }

  private recreatePotentialBindGroups(): void {
    if (!this.device) return;
    // Create bind groups for dynamic potential passes.
    this.densityBindGroup = this.device.createBindGroup({
      layout: this.clearDensityPipeline!.getBindGroupLayout(0),
      entries: [{ binding: 0, resource: { buffer: this.rawDensityBuffer! } }],
    });

    this.decodeDensityBindGroup = this.device.createBindGroup({
      layout: this.decodeDensityPipeline!.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.rawDensityBuffer! } },
        { binding: 1, resource: { buffer: this.densityScratchBuffer! } },
      ],
    });
    this.penaltyBindGroup = this.device.createBindGroup({
      layout: this.updatePenaltyPipeline!.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.smoothedDensityBuffer! } },
        { binding: 1, resource: { buffer: this.protectedBuffer! } },
        { binding: 2, resource: { buffer: this.penaltyBuffer! } },
        { binding: 3, resource: { buffer: this.blockedBuffer! } },
        { binding: 4, resource: { buffer: this.paramsBuffer! } },
        { binding: 5, resource: { buffer: this.relaxStateBuffer! } },
      ],
    });
    const smoothEntries = (reverse: boolean) => [
      {
        binding: 0,
        resource: {
          buffer: reverse
            ? this.smoothedDensityBuffer!
            : this.densityScratchBuffer!,
        },
      },
      {
        binding: 1,
        resource: {
          buffer: reverse
            ? this.densityScratchBuffer!
            : this.smoothedDensityBuffer!,
        },
      },
      { binding: 2, resource: { buffer: this.paramsBuffer! } },
    ];
    this.smoothDensityOnlyBindGroup = this.device.createBindGroup({
      layout: this.smoothDensityOnlyPipeline!.getBindGroupLayout(0),
      entries: smoothEntries(false),
    });
    this.smoothDensityReverseBindGroup = this.device.createBindGroup({
      layout: this.smoothDensityOnlyPipeline!.getBindGroupLayout(0),
      entries: smoothEntries(true),
    });

    this.initRelaxBindGroup = this.device.createBindGroup({
      layout: this.initRelaxPipeline!.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.potentialBufferA! } },
        { binding: 1, resource: { buffer: this.seedBuffer! } },
        { binding: 2, resource: { buffer: this.paramsBuffer! } },
        { binding: 3, resource: { buffer: this.staticPotentialBuffer! } },
        { binding: 4, resource: { buffer: this.relaxStateBuffer! } },
      ],
    });

    this.relaxBindGroupA = this.device.createBindGroup({
      layout: this.relaxStepPipeline!.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.potentialBufferA! } },
        { binding: 1, resource: { buffer: this.potentialBufferB! } },
        { binding: 2, resource: { buffer: this.penaltyBuffer! } },
        { binding: 3, resource: { buffer: this.blockedBuffer! } },
        { binding: 4, resource: { buffer: this.paramsBuffer! } },
        { binding: 5, resource: { buffer: this.relaxStateBuffer! } },
      ],
    });

    this.relaxBindGroupB = this.device.createBindGroup({
      layout: this.relaxStepPipeline!.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.potentialBufferB! } },
        { binding: 1, resource: { buffer: this.potentialBufferA! } },
        { binding: 2, resource: { buffer: this.penaltyBuffer! } },
        { binding: 3, resource: { buffer: this.blockedBuffer! } },
        { binding: 4, resource: { buffer: this.paramsBuffer! } },
        { binding: 5, resource: { buffer: this.relaxStateBuffer! } },
      ],
    });
  }

  private updateSimParams(): void {
    if (!this.device || !this.paramsBuffer) return;
    const buf = new ArrayBuffer(128);
    const f32 = new Float32Array(buf);
    const u32 = new Uint32Array(buf);

    f32[0] = this.worldWidth;
    f32[1] = this.worldHeight;
    f32[2] = 0.016; // dt: 16 ms per tick matching C# Step(0.016, 1.0)
    f32[3] = this.socialWeight;
    u32[4] = this.activeCount;
    u32[5] = this.gridCols;
    u32[6] = this.gridRows;
    u32[7] = this.maxPerCell;

    // Obstacle 1: [x, y, w, h] (floats 8..11, bytes 32..47)
    f32[8] = this.obs1[0];
    f32[9] = this.obs1[1];
    f32[10] = this.obs1[2];
    f32[11] = this.obs1[3];

    // Obstacle 2: [x, y, w, h] (floats 12..15, bytes 48..63)
    f32[12] = this.obs2[0];
    f32[13] = this.obs2[1];
    f32[14] = this.obs2[2];
    f32[15] = this.obs2[3];

    // ExitZone: [x, y, w, h] (floats 16..19, bytes 64..79)
    f32[16] = this.exitZone[0];
    f32[17] = this.exitZone[1];
    f32[18] = this.exitZone[2];
    f32[19] = this.exitZone[3];

    u32[20] = this.granulation;
    u32[21] = this.isMapScenario ? 1 : 0;
    u32[22] = this.mapCols;
    u32[23] = this.mapRows;
    f32[24] = this.mapCellSize;
    u32[25] = this.numExits;
    u32[26] = this.potCols;
    u32[27] = this.potRows;
    f32[28] = this.potCellSize;
    u32[29] = this.isMapScenario && this.mapRecoveryAvailable ? 1 : 0;
    u32[30] = 0;
    u32[31] = 0;

    this.device.queue.writeBuffer(this.paramsBuffer, 0, buf);
  }

  private reallocateAgents(count: number, preparedAgents?: Float32Array): void {
    if (!this.device) return;
    this.activeCount = count;
    const agentByteSize = 32; // 8 floats per agent
    const totalBytes = count * agentByteSize;

    this.agentsBuffer?.destroy();
    this.stagingBuffer?.destroy();

    this.agentsBuffer = this.device.createBuffer({
      size: Math.max(totalBytes, 64),
      usage:
        GPUBufferUsage.STORAGE |
        GPUBufferUsage.COPY_SRC |
        GPUBufferUsage.COPY_DST,
    });

    this.stagingBuffer = this.device.createBuffer({
      size: Math.max(totalBytes, 64),
      usage: GPUBufferUsage.MAP_READ | GPUBufferUsage.COPY_DST,
    });

    this.cpuValues = new Float64Array(count * 5);
    const radius = this.getAgentRadius(this.granulation);

    this.simulationTime = 0;

    let initData: Float32Array;
    if (this.isMapScenario && this.mapScenario) {
      this.activeMapAgents = count;
      this.evacuatedCount = 0;
      this.evacuatedPerExit = new Array(this.numExits).fill(0);
      this.evacuationTimes = [];
      this.lastEvacuationTime = 0;
      this.simulationTime = 0;
      this.agentEvacuated = new Uint8Array(count);
      initData =
        preparedAgents ??
        spawnMapAgents(
          this.mapScenario,
          count,
          this.granulation,
          buildMapPotentialField(this.mapScenario),
          radius,
        );
    } else {
      initData = new Float32Array(count * 8);
      const u32View = new Uint32Array(initData.buffer);

      const spawnMinX = this.worldWidth * 0.03;
      const spawnMaxX = this.worldWidth * 0.18;
      const spawnMinY = this.worldHeight * 0.15;
      const spawnMaxY = this.worldHeight * 0.85;

      let seed = 12345;
      const nextRandom = () => {
        seed = (seed * 1664525 + 1013904223) >>> 0;
        return seed / 4294967296;
      };

      for (let i = 0; i < count; i++) {
        const px = spawnMinX + nextRandom() * (spawnMaxX - spawnMinX);
        const py = spawnMinY + nextRandom() * (spawnMaxY - spawnMinY);
        const vx = 1.2;
        const vy = 0.0;
        const speed = 1.3 + nextRandom() * 0.4;

        const off = i * 8;
        initData[off + 0] = px;
        initData[off + 1] = py;
        initData[off + 2] = vx;
        initData[off + 3] = vy;
        initData[off + 4] = radius;
        initData[off + 5] = speed;
        u32View[off + 6] = 1; // flags = 1 (active)
        u32View[off + 7] = 0;
      }
    }
    this.device.queue.writeBuffer(this.agentsBuffer, 0, initData.buffer);
    this.recoveryBuffer?.destroy();
    const recovery = new Float32Array(count * 4);
    for (let i = 0; i < count; i++) {
      recovery[i * 4] = initData[i * 8];
      recovery[i * 4 + 1] = initData[i * 8 + 1];
    }
    this.recoveryBuffer = this.device.createBuffer({
      size: Math.max(64, recovery.byteLength),
      usage:
        GPUBufferUsage.STORAGE |
        GPUBufferUsage.COPY_DST |
        GPUBufferUsage.COPY_SRC,
    });
    this.device.queue.writeBuffer(this.recoveryBuffer, 0, recovery);
    this.initialDensityPending = true;

    this.recreateSpatialBindGroups();

    this.recreateAgentDensityBindGroup();
    this.recreateKdeDepositBindGroup();
  }

  private recreateAgentDensityBindGroup(): void {
    if (!this.device || !this.agentsBuffer) return;
    this.accumulateDensityBindGroup = this.device.createBindGroup({
      layout: this.accumulateDensityPipeline!.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.agentsBuffer } },
        { binding: 1, resource: { buffer: this.rawDensityBuffer! } },
        { binding: 2, resource: { buffer: this.paramsBuffer! } },
      ],
    });
  }

  private recreateSpatialBindGroups(): void {
    if (
      !this.device ||
      !this.agentsBuffer ||
      !this.cellCountsBuffer ||
      !this.cellAgentsBuffer ||
      !this.paramsBuffer ||
      !this.potentialBufferA ||
      !this.smoothedDensityBuffer ||
      !this.blockedBuffer ||
      !this.exitsBuffer ||
      !this.clearPipeline ||
      !this.binPipeline ||
      !this.physicsPipeline
    ) {
      return;
    }

    this.clearBindGroup = this.device.createBindGroup({
      layout: this.clearPipeline.getBindGroupLayout(0),
      entries: [{ binding: 0, resource: { buffer: this.cellCountsBuffer } }],
    });

    this.binBindGroup = this.device.createBindGroup({
      layout: this.binPipeline.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.agentsBuffer } },
        { binding: 1, resource: { buffer: this.cellCountsBuffer } },
        { binding: 2, resource: { buffer: this.cellAgentsBuffer } },
        { binding: 3, resource: { buffer: this.paramsBuffer } },
      ],
    });

    const blockedRes =
      this.isMapScenario && this.mapBlockedBuffer
        ? this.mapBlockedBuffer
        : this.blockedBuffer;
    const exitsRes = this.exitsBuffer;

    this.bindGroup = this.device.createBindGroup({
      layout: this.physicsPipeline.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.agentsBuffer } },
        { binding: 1, resource: { buffer: this.cellCountsBuffer } },
        { binding: 2, resource: { buffer: this.cellAgentsBuffer } },
        { binding: 3, resource: { buffer: this.paramsBuffer } },
        { binding: 4, resource: { buffer: this.potentialBufferA } },
        { binding: 5, resource: { buffer: this.smoothedDensityBuffer } },
        { binding: 6, resource: { buffer: blockedRes } },
        { binding: 7, resource: { buffer: exitsRes } },
        { binding: 8, resource: { buffer: this.recoveryBuffer! } },
      ],
    });
  }

  private reallocateSpatialGrid(cols: number, rows: number): void {
    if (!this.device) return;
    if (
      this.cellCountsBuffer &&
      this.cellAgentsBuffer &&
      this.gridCols === cols &&
      this.gridRows === rows
    ) {
      return;
    }
    this.gridCols = cols;
    this.gridRows = rows;
    const totalCells = this.gridCols * this.gridRows;

    this.cellCountsBuffer?.destroy();
    this.cellAgentsBuffer?.destroy();

    this.cellCountsBuffer = this.device.createBuffer({
      size: totalCells * 4,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });
    this.cellAgentsBuffer = this.device.createBuffer({
      size: totalCells * this.maxPerCell * 4,
      usage: GPUBufferUsage.STORAGE,
    });
    this.recreateSpatialBindGroups();
  }

  private ensureSpatialGrid(): void {
    const targetCellSize = 4.0;
    let nextCols = Math.max(10, Math.ceil(this.worldWidth / targetCellSize));
    let nextRows = Math.max(10, Math.ceil(this.worldHeight / targetCellSize));
    const maxDimension = 250;
    if (nextCols > maxDimension || nextRows > maxDimension) {
      const maxDim = Math.max(nextCols, nextRows);
      nextCols = Math.max(10, Math.round((nextCols / maxDim) * maxDimension));
      nextRows = Math.max(10, Math.round((nextRows / maxDim) * maxDimension));
    }
    this.reallocateSpatialGrid(nextCols, nextRows);
  }

  private ensureKdeGrid(): void {
    if (!this.device) return;
    // Lift rigid 100x100: target ~1.5m to 2.5m physical cell size with square cells
    const targetCellSize = Math.max(
      1.5,
      Math.min(this.worldWidth, this.worldHeight) / 200.0,
    );
    const cols = Math.min(
      512,
      Math.max(128, Math.round(this.worldWidth / targetCellSize)),
    );
    const rows = Math.min(
      512,
      Math.max(128, Math.round(this.worldHeight / targetCellSize)),
    );

    if (
      this.kdeWidth === cols &&
      this.kdeHeight === rows &&
      this.kdeDensityOutputBuffer &&
      this.kdeStagingBuffer
    ) {
      return;
    }

    this.kdeWidth = cols;
    this.kdeHeight = rows;
    const totalCells = cols * rows;

    this.kdeParamsBuffer?.destroy();
    this.kdeDepositMassBuffer?.destroy();
    this.kdeDepositSpeedBuffer?.destroy();
    this.kdeDensityOutputBuffer?.destroy();
    this.kdeStagingBuffer?.destroy();

    this.kdeParamsBuffer = this.device.createBuffer({
      size: 48,
      usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
    });

    this.kdeDepositMassBuffer = this.device.createBuffer({
      size: totalCells * 4,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });

    this.kdeDepositSpeedBuffer = this.device.createBuffer({
      size: totalCells * 4,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
    });

    this.kdeDensityOutputBuffer = this.device.createBuffer({
      size: totalCells * 4,
      usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_SRC,
    });

    this.kdeStagingBuffer = this.device.createBuffer({
      size: totalCells * 4,
      usage: GPUBufferUsage.MAP_READ | GPUBufferUsage.COPY_DST,
    });

    if (this.kdeClearPipeline) {
      this.kdeClearBindGroup = this.device.createBindGroup({
        layout: this.kdeClearPipeline.getBindGroupLayout(0),
        entries: [
          { binding: 0, resource: { buffer: this.kdeDepositMassBuffer } },
          { binding: 1, resource: { buffer: this.kdeDepositSpeedBuffer } },
        ],
      });
    }

    this.recreateKdeDepositBindGroup();
    this.recreateKdeKernelBindGroup();
  }

  private recreateKdeDepositBindGroup(): void {
    if (
      !this.device ||
      !this.agentsBuffer ||
      !this.kdeDepositMassBuffer ||
      !this.kdeDepositSpeedBuffer ||
      !this.kdeParamsBuffer ||
      !this.kdeDepositPipeline
    ) {
      return;
    }
    this.kdeDepositBindGroup = this.device.createBindGroup({
      layout: this.kdeDepositPipeline.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.agentsBuffer } },
        { binding: 1, resource: { buffer: this.kdeDepositMassBuffer } },
        { binding: 2, resource: { buffer: this.kdeDepositSpeedBuffer } },
        { binding: 3, resource: { buffer: this.kdeParamsBuffer } },
      ],
    });
  }

  private recreateKdeKernelBindGroup(): void {
    if (
      !this.device ||
      !this.kdeDepositMassBuffer ||
      !this.kdeDepositSpeedBuffer ||
      !this.kdeDensityOutputBuffer ||
      !this.kdeParamsBuffer ||
      !this.kdeSmoothKernelPipeline
    ) {
      return;
    }
    this.kdeKernelBindGroup = this.device.createBindGroup({
      layout: this.kdeSmoothKernelPipeline.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: this.kdeDepositMassBuffer } },
        { binding: 1, resource: { buffer: this.kdeDepositSpeedBuffer } },
        { binding: 2, resource: { buffer: this.kdeDensityOutputBuffer } },
        { binding: 3, resource: { buffer: this.kdeParamsBuffer } },
      ],
    });
  }

  private updateKdeParams(): void {
    if (!this.device || !this.kdeParamsBuffer) return;
    const buf = new ArrayBuffer(48);
    const f32 = new Float32Array(buf);
    const u32 = new Uint32Array(buf);
    f32[0] = this.worldWidth;
    f32[1] = this.worldHeight;
    u32[2] = this.kdeWidth;
    u32[3] = this.kdeHeight;
    const isSpeedMode = this.currentRenderMode === "heatmap";
    f32[4] = isSpeedMode ? 18.0 : 25.0; // kernelRadius in meters
    u32[5] = this.granulation;
    u32[6] = this.activeCount;
    u32[7] = isSpeedMode ? 1 : 0;
    this.device.queue.writeBuffer(this.kdeParamsBuffer, 0, buf);
  }

  dispatch(
    session: string,
    kind: BrowserCoreCommand,
    values: CoreCommandValues,
    _profiler: ProfilingWindow,
  ): void {
    this.activeSession = session;
    this.dispatchSequence++;

    if (kind === "init" || kind === "reset") {
      this.activeRunGeneration++;
      this.activeTick = 0;
      this.dynamicFieldTimer = 0.2;
      this.inFlightBatches = 0;
      this.inFlightWorkPromise = null;
      if (typeof values.worldWidth === "number")
        this.worldWidth = values.worldWidth;
      if (typeof values.worldHeight === "number")
        this.worldHeight = values.worldHeight;
      if (typeof values.socialRepulsionWeight === "number")
        this.socialWeight = values.socialRepulsionWeight;
      if (typeof values.granulation === "number")
        this.granulation = values.granulation;

      this.applyEnvironment(values);
      if (typeof values.count === "number") this.rawCount = values.count;
      const simCount = Math.max(1, Math.ceil(this.rawCount / this.granulation));
      this.ensureSpatialGrid();
      this.ensureKdeGrid();
      this.updatePotentialGrid();
      this.reallocateAgents(simCount);
      this.updateSimParams();
      return;
    }

    if (kind === "count") {
      this.activeRunGeneration++;
      this.activeTick = 0;
      this.dynamicFieldTimer = 0.2;
      this.inFlightBatches = 0;
      this.inFlightWorkPromise = null;
      if (typeof values.count === "number") this.rawCount = values.count;
      const simCount = Math.max(1, Math.ceil(this.rawCount / this.granulation));
      this.reallocateAgents(simCount);
      this.updateSimParams();
      return;
    }

    if (kind === "preset") {
      this.activeRunGeneration++;
      this.activeTick = 0;
      this.dynamicFieldTimer = 0.2;
      this.inFlightBatches = 0;
      this.inFlightWorkPromise = null;
      if (typeof values.worldWidth === "number")
        this.worldWidth = values.worldWidth;
      if (typeof values.worldHeight === "number")
        this.worldHeight = values.worldHeight;
      if (typeof values.count === "number") this.rawCount = values.count;
      if (typeof values.granulation === "number")
        this.granulation = values.granulation;
      this.applyEnvironment(values);
      const simCount = Math.max(1, Math.ceil(this.rawCount / this.granulation));
      this.ensureSpatialGrid();
      this.ensureKdeGrid();
      this.updatePotentialGrid();
      this.reallocateAgents(simCount);
      this.updateSimParams();
      return;
    }

    if (kind === "set-environment") {
      this.activeRunGeneration++;
      this.activeTick = 0;
      this.dynamicFieldTimer = 0.2;
      this.inFlightBatches = 0;
      this.inFlightWorkPromise = null;
      if (typeof values.worldWidth === "number")
        this.worldWidth = values.worldWidth;
      if (typeof values.worldHeight === "number")
        this.worldHeight = values.worldHeight;
      this.applyEnvironment(values);
      this.ensureSpatialGrid();
      this.ensureKdeGrid();
      this.updatePotentialGrid();
      this.recreateSpatialBindGroups();
      this.updateSimParams();
      return;
    }

    if (kind === "granulation") {
      if (typeof values.granulation === "number")
        this.granulation = values.granulation;
      const simCount = Math.max(1, Math.ceil(this.rawCount / this.granulation));
      this.ensureSpatialGrid();
      this.reallocateAgents(simCount);
      this.updateSimParams();
      return;
    }

    if (kind === "weight") {
      if (typeof values.socialRepulsionWeight === "number") {
        this.socialWeight = values.socialRepulsionWeight;
        this.updateSimParams();
      }
      return;
    }

    if (kind === "render") {
      if (typeof values.renderMode === "string") {
        this.currentRenderMode = values.renderMode;
      }
      return;
    }
  }

  advanceFixedTicks(ticks: number): Promise<number> {
    const work = this.tickWork.then(() => this.advanceGpuTicks(ticks));
    this.tickWork = work.catch(() => this.activeTick);
    return work;
  }

  private async advanceGpuTicks(
    ticks: number,
    fieldsOnly = false,
  ): Promise<number> {
    if (
      !this.device ||
      !this.clearPipeline ||
      !this.binPipeline ||
      !this.physicsPipeline ||
      !this.bindGroup ||
      !this.binBindGroup ||
      !this.clearBindGroup ||
      this.activeCount === 0
    ) {
      return this.activeTick;
    }

    let commandEncoder = this.device.createCommandEncoder();
    const workgroups = Math.ceil(this.activeCount / 64);
    const gridWorkgroups = Math.ceil((this.gridCols * this.gridRows) / 64);
    const potWorkgroups = Math.ceil((this.potCols * this.potRows) / 64);
    const pot2D_X = Math.ceil(this.potCols / 16);
    const pot2D_Y = Math.ceil(this.potRows / 16);

    for (let step = 0; step < (fieldsOnly ? 1 : ticks); step++) {
      if (!fieldsOnly) {
        this.simulationTime += 0.016;
        this.dynamicFieldTimer += 0.016;
      }

      const refine =
        fieldsOnly ||
        (!this.isMapScenario &&
          (this.initialDensityPending ||
            this.dynamicFieldTimer >= 0.2 ||
            this.activeTick + step === 0));
      if (refine || this.granulation > 1) {
        const run = (
          pipeline: GPUComputePipeline,
          bindGroup: GPUBindGroup,
          x: number,
          y = 1,
        ) => {
          const pass = commandEncoder.beginComputePass();
          pass.setPipeline(pipeline);
          pass.setBindGroup(0, bindGroup);
          pass.dispatchWorkgroups(x, y);
          pass.end();
        };
        run(this.clearDensityPipeline!, this.densityBindGroup!, potWorkgroups);
        run(
          this.accumulateDensityPipeline!,
          this.accumulateDensityBindGroup!,
          workgroups,
        );
        run(
          this.decodeDensityPipeline!,
          this.decodeDensityBindGroup!,
          potWorkgroups,
        );
        for (let pass = 0; pass < (this.granulation >= 16 ? 3 : 1); pass++) {
          run(
            this.smoothDensityOnlyPipeline!,
            pass % 2 === 0
              ? this.smoothDensityOnlyBindGroup!
              : this.smoothDensityReverseBindGroup!,
            pot2D_X,
            pot2D_Y,
          );
        }
        if (refine) {
          this.dynamicFieldTimer = 0;
          commandEncoder.clearBuffer(this.relaxStateBuffer!);
          run(
            this.updatePenaltyPipeline!,
            this.penaltyBindGroup!,
            pot2D_X,
            pot2D_Y,
          );
          // WASM updates the EMA during InitializeAgents and again on the first
          // physics tick. Both observe the initial positions before integration.
          if (this.initialDensityPending) {
            run(
              this.updatePenaltyPipeline!,
              this.penaltyBindGroup!,
              pot2D_X,
              pot2D_Y,
            );
            this.initialDensityPending = false;
          }
          run(
            this.initRelaxPipeline!,
            this.initRelaxBindGroup!,
            pot2D_X,
            pot2D_Y,
          );
          commandEncoder.copyBufferToBuffer(
            this.relaxStateBuffer!,
            0,
            this.relaxStateStaging!,
            0,
            8,
          );
          this.device.queue.submit([commandEncoder.finish()]);
          await this.relaxStateStaging!.mapAsync(GPUMapMode.READ);
          const congested =
            new Uint32Array(this.relaxStateStaging!.getMappedRange())[0] !== 0;
          this.relaxStateStaging!.unmap();
          commandEncoder = this.device.createCommandEncoder();
          if (congested) {
            // Positive edge costs imply convergence in at most V-1 edges.
            // Check after batches of 32 pairs to avoid a readback on every pass.
            const maxPairs =
              Math.ceil((this.potCols * this.potRows - 1) / 2) + 1;
            for (let pair = 0; pair < maxPairs; pair += 32) {
              const batchPairs = Math.min(32, maxPairs - pair);
              for (let j = 0; j < batchPairs; j++) {
                commandEncoder.clearBuffer(this.relaxStateBuffer!, 4, 4);
                run(
                  this.relaxStepPipeline!,
                  this.relaxBindGroupA!,
                  pot2D_X,
                  pot2D_Y,
                );
                run(
                  this.relaxStepPipeline!,
                  this.relaxBindGroupB!,
                  pot2D_X,
                  pot2D_Y,
                );
              }
              commandEncoder.copyBufferToBuffer(
                this.relaxStateBuffer!,
                0,
                this.relaxStateStaging!,
                0,
                8,
              );
              this.device.queue.submit([commandEncoder.finish()]);
              await this.relaxStateStaging!.mapAsync(GPUMapMode.READ);
              const changed =
                new Uint32Array(this.relaxStateStaging!.getMappedRange())[1] !==
                0;
              this.relaxStateStaging!.unmap();
              commandEncoder = this.device.createCommandEncoder();
              if (!changed) break;
            }
          }
        }
      }

      if (fieldsOnly) continue;

      // Pass 1: Clear spatial hash grid
      const pass1 = commandEncoder.beginComputePass();
      pass1.setPipeline(this.clearPipeline);
      pass1.setBindGroup(0, this.clearBindGroup);
      pass1.dispatchWorkgroups(gridWorkgroups);
      pass1.end();

      // Pass 2: Bin agents into grid cells
      const pass2 = commandEncoder.beginComputePass();
      pass2.setPipeline(this.binPipeline);
      pass2.setBindGroup(0, this.binBindGroup);
      pass2.dispatchWorkgroups(workgroups);
      pass2.end();

      // Pass 3: Step physics (forces, obstacles, social repulsion, integration)
      const pass3 = commandEncoder.beginComputePass();
      pass3.setPipeline(this.physicsPipeline);
      pass3.setBindGroup(0, this.bindGroup);
      pass3.dispatchWorkgroups(workgroups);
      pass3.end();
    }

    this.device.queue.submit([commandEncoder.finish()]);
    this.activeTick += ticks;
    this.inFlightBatches++;
    const completion = this.device.queue
      .onSubmittedWorkDone()
      .then(() => {
        if (this.inFlightWorkPromise === completion) {
          this.inFlightBatches = 0;
          this.inFlightWorkPromise = null;
        }
      })
      .catch(() => {
        if (this.inFlightWorkPromise === completion) {
          this.inFlightBatches = 0;
          this.inFlightWorkPromise = null;
        }
      });
    this.inFlightWorkPromise = completion;
    return this.activeTick;
  }

  async syncInFlight(maxAllowed = 1): Promise<void> {
    await this.tickWork;
    if (this.device && maxAllowed === 0) {
      // Configuration/reset also needs to wait for draws that consume agents.
      await this.device.queue.onSubmittedWorkDone();
      return;
    }
    if (
      !this.device ||
      this.inFlightBatches <= maxAllowed ||
      !this.inFlightWorkPromise
    ) {
      return;
    }
    try {
      await this.inFlightWorkPromise;
    } catch {
      // Ignored - device loss or queue error
    }
  }

  async capturePreview(): Promise<OwnedPreview> {
    await this.tickWork;
    if (
      !this.device ||
      !this.agentsBuffer ||
      !this.stagingBuffer ||
      !this.cpuValues
    ) {
      throw new Error("GPU simulation engine is not initialized.");
    }

    if (this.isCapturing) {
      return {
        version: protocolVersion,
        session: this.activeSession ?? "",
        sequence: this.dispatchSequence,
        runGeneration: this.activeRunGeneration,
        tick: this.activeTick,
        count: this.activeCount,
        values: this.cpuValues,
      };
    }

    this.isCapturing = true;
    try {
      const agentByteSize = 32;
      const totalBytes = this.activeCount * agentByteSize;

      const isKdeMode =
        this.currentRenderMode === "density" ||
        this.currentRenderMode === "heatmap";

      const copyEncoder = this.device.createCommandEncoder();

      if (isKdeMode && this.activeCount > 0) {
        this.ensureKdeGrid();
        this.updateKdeParams();

        // 1. Clear KDE deposit buffers
        const clearPass = copyEncoder.beginComputePass();
        clearPass.setPipeline(this.kdeClearPipeline!);
        clearPass.setBindGroup(0, this.kdeClearBindGroup!);
        clearPass.dispatchWorkgroups(
          Math.ceil((this.kdeWidth * this.kdeHeight) / 64),
        );
        clearPass.end();

        // 2. Deposit agent mass/speed
        const depositPass = copyEncoder.beginComputePass();
        depositPass.setPipeline(this.kdeDepositPipeline!);
        depositPass.setBindGroup(0, this.kdeDepositBindGroup!);
        depositPass.dispatchWorkgroups(Math.ceil(this.activeCount / 64));
        depositPass.end();

        // 3. Smooth continuous kernel pass
        const kernelPass = copyEncoder.beginComputePass();
        kernelPass.setPipeline(this.kdeSmoothKernelPipeline!);
        kernelPass.setBindGroup(0, this.kdeKernelBindGroup!);
        kernelPass.dispatchWorkgroups(
          Math.ceil(this.kdeWidth / 16),
          Math.ceil(this.kdeHeight / 16),
        );
        kernelPass.end();

        // Copy KDE output to staging buffer
        const kdeTotalBytes = this.kdeWidth * this.kdeHeight * 4;
        copyEncoder.copyBufferToBuffer(
          this.kdeDensityOutputBuffer!,
          0,
          this.kdeStagingBuffer!,
          0,
          kdeTotalBytes,
        );
      }

      copyEncoder.copyBufferToBuffer(
        this.agentsBuffer,
        0,
        this.stagingBuffer,
        0,
        totalBytes,
      );
      this.device.queue.submit([copyEncoder.finish()]);

      const mapPromises: Promise<void>[] = [
        this.stagingBuffer.mapAsync(GPUMapMode.READ),
      ];
      if (isKdeMode && this.activeCount > 0 && this.kdeStagingBuffer) {
        mapPromises.push(this.kdeStagingBuffer.mapAsync(GPUMapMode.READ));
      }
      await Promise.all(mapPromises);

      const mappedArray = new Float32Array(
        this.stagingBuffer.getMappedRange(0, totalBytes),
      );

      // Pack into 5 contiguous lanes for FrameTransport:
      // [posX (count), posY (count), vx (count), vy (count), radius (count)]
      const count = this.activeCount;
      const target = this.cpuValues;

      for (let i = 0; i < count; i++) {
        const srcOff = i * 8;
        target[i] = mappedArray[srcOff + 0];
        target[count + i] = mappedArray[srcOff + 1];
        target[2 * count + i] = mappedArray[srcOff + 2];
        target[3 * count + i] = mappedArray[srcOff + 3];
        const r = mappedArray[srcOff + 4];
        target[4 * count + i] = r;

        if (
          this.isMapScenario &&
          this.agentEvacuated &&
          !this.agentEvacuated[i] &&
          r <= 0.0
        ) {
          this.agentEvacuated[i] = 1;
          this.evacuatedCount++;
          if (this.activeMapAgents > 0) this.activeMapAgents--;
          let bestExit = 0;
          let minExitDist = Infinity;
          if (this.mapScenario) {
            for (let e = 0; e < this.mapScenario.exits.length; e++) {
              const ex = this.mapScenario.exits[e];
              const dx = target[i] - ex.x;
              const dy = target[count + i] - ex.y;
              const d = Math.sqrt(dx * dx + dy * dy);
              if (d < minExitDist) {
                minExitDist = d;
                bestExit = e;
              }
            }
          }
          this.evacuatedPerExit[bestExit]++;
          this.evacuationTimes.push(this.simulationTime);
          this.lastEvacuationTime = this.simulationTime;
        }
      }

      this.stagingBuffer.unmap();

      let densityResult: DensityGridData | undefined;
      if (isKdeMode && this.activeCount > 0 && this.kdeStagingBuffer) {
        const kdeTotalBytes = this.kdeWidth * this.kdeHeight * 4;
        const mappedKde = new Float32Array(
          this.kdeStagingBuffer.getMappedRange(0, kdeTotalBytes),
        );
        const densityData = new Float32Array(mappedKde);
        this.kdeStagingBuffer.unmap();

        let maxDensity = 0.0;
        for (let i = 0; i < densityData.length; i++) {
          if (densityData[i] > maxDensity) maxDensity = densityData[i];
        }

        densityResult = {
          width: this.kdeWidth,
          height: this.kdeHeight,
          data: densityData,
          maxDensity,
        };
      }

      return {
        version: protocolVersion,
        session: this.activeSession ?? "",
        sequence: this.dispatchSequence,
        runGeneration: this.activeRunGeneration,
        tick: this.activeTick,
        count,
        values: target,
        density: densityResult,
      };
    } finally {
      this.isCapturing = false;
    }
  }

  initializeMap(snapshot: MapGpuSnapshot): void {
    if (!this.device) throw new Error("GPU engine is not initialized.");
    const dots = Math.ceil(snapshot.count / snapshot.granulation);
    if (
      snapshot.agents.byteLength !== dots * 32 ||
      snapshot.fields.byteLength !== snapshot.columns * snapshot.rows * 16 ||
      snapshot.blocked.byteLength !== snapshot.columns * snapshot.rows
    ) {
      throw new Error("Invalid map GPU snapshot.");
    }
    this.rawCount = snapshot.count;
    this.granulation = snapshot.granulation;
    this.socialWeight = snapshot.socialRepulsionWeight;
    this.loadMapScenario(snapshot.scenario, snapshot);
    this.totalPeople = snapshot.count;
    this.activeSession = "plansafe-map";
    this.activeRunGeneration++;
    this.activeTick = 0;
    this.dynamicFieldTimer = 0;
    this.ensureSpatialGrid();
    this.ensureKdeGrid();
    this.reallocateAgents(
      dots,
      new Float32Array(snapshot.agents.slice().buffer),
    );
    this.initialDensityPending = false;
    this.updateSimParams();
  }

  /** Updates terrain and routes while keeping GPU agents, recovery state and progress. */
  async updateMap(
    snapshot: MapGpuSnapshot,
    offsetX = 0,
    offsetY = 0,
  ): Promise<void> {
    if (!this.device || !this.mapScenario)
      throw new Error("A GPU map simulation is required.");
    await this.tickWork;
    await this.syncInFlight(0);
    const previousExits = this.mapScenario.exits;
    const previousCounts = this.evacuatedPerExit;
    if (offsetX !== 0 || offsetY !== 0) {
      await this.translateMapBuffer(this.agentsBuffer!, 8, offsetX, offsetY);
      await this.translateMapBuffer(this.recoveryBuffer!, 4, offsetX, offsetY);
    }
    this.loadMapScenario(snapshot.scenario, snapshot, false);
    this.totalPeople = snapshot.count;
    this.evacuatedPerExit = new Array(this.numExits).fill(0);
    for (let index = 0; index < previousExits.length; index++) {
      const exit = previousExits[index];
      const newIndex = this.mapScenario.exits.findIndex(
        (candidate) =>
          Math.abs(candidate.x - exit.x - offsetX) < 0.01 &&
          Math.abs(candidate.y - exit.y - offsetY) < 0.01 &&
          candidate.radius === exit.radius,
      );
      if (newIndex >= 0)
        this.evacuatedPerExit[newIndex] += previousCounts[index];
    }
    this.ensureSpatialGrid();
    this.ensureKdeGrid();
    this.recreateSpatialBindGroups();
    this.recreateAgentDensityBindGroup();
    this.updateSimParams();
    await this.advanceGpuTicks(0, true);
    await this.syncInFlight(0);
  }

  private async translateMapBuffer(
    buffer: GPUBuffer,
    stride: number,
    offsetX: number,
    offsetY: number,
  ): Promise<void> {
    const staging = this.device!.createBuffer({
      size: buffer.size,
      usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ,
    });
    try {
      const encoder = this.device!.createCommandEncoder();
      encoder.copyBufferToBuffer(buffer, 0, staging, 0, buffer.size);
      this.device!.queue.submit([encoder.finish()]);
      await staging.mapAsync(GPUMapMode.READ);
      const values = new Float32Array(staging.getMappedRange().slice(0));
      staging.unmap();
      for (let index = 0; index < this.activeCount; index++) {
        values[index * stride] += offsetX;
        values[index * stride + 1] += offsetY;
      }
      this.device!.queue.writeBuffer(buffer, 0, values);
    } finally {
      staging.destroy();
    }
  }

  loadMapScenario(
    data: Uint8Array,
    snapshot?: MapGpuSnapshot,
    resetProgress = true,
  ): void {
    const scenario = parseMapScenario(data);
    this.mapScenario = scenario;
    this.isMapScenario = true;
    this.worldWidth = scenario.worldWidth;
    this.worldHeight = scenario.worldHeight;
    this.mapCols = scenario.columns;
    this.mapRows = scenario.rows;
    this.mapCellSize = scenario.cellSize;
    this.potCols = snapshot?.columns ?? scenario.columns;
    this.potRows = snapshot?.rows ?? scenario.rows;
    this.potCellSize = snapshot?.cellSize ?? scenario.cellSize;
    this.numExits = scenario.exits.length;
    this.totalPeople = scenario.totalPeople;

    if (resetProgress) {
      this.evacuatedCount = 0;
      this.evacuatedPerExit = new Array(this.numExits).fill(0);
      this.evacuationTimes = [];
      this.lastEvacuationTime = 0;
      this.simulationTime = 0;
    }

    if (this.device) {
      // 1. Exits buffer: vec4<f32>(x, y, radius, 0.0) per exit
      this.exitsBuffer?.destroy();
      const exitFloats = new Float32Array(Math.max(1, this.numExits) * 4);
      for (let i = 0; i < scenario.exits.length; i++) {
        exitFloats[i * 4 + 0] = scenario.exits[i].x;
        exitFloats[i * 4 + 1] = scenario.exits[i].y;
        exitFloats[i * 4 + 2] = scenario.exits[i].radius;
        exitFloats[i * 4 + 3] = 0.0;
      }
      this.exitsBuffer = this.device.createBuffer({
        size: Math.max(64, exitFloats.byteLength),
        usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
      });
      this.device.queue.writeBuffer(this.exitsBuffer, 0, exitFloats.buffer);

      // 2. Blocked raster buffer: u32 per cell
      this.mapBlockedBuffer?.destroy();
      const blockedU32 = new Uint32Array(scenario.columns * scenario.rows);
      for (let i = 0; i < scenario.blocked.length; i++) {
        blockedU32[i] = scenario.blocked[i];
      }
      this.mapBlockedBuffer = this.device.createBuffer({
        size: Math.max(64, blockedU32.byteLength),
        usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
      });

      // A map's fine wall raster and potential grid can have different resolutions.
      const potCells = this.potCols * this.potRows;
      const allocate = (previous?: GPUBuffer): GPUBuffer => {
        if (previous?.size === potCells * 4) return previous;
        previous?.destroy();
        return this.device!.createBuffer({
          size: potCells * 4,
          usage:
            GPUBufferUsage.STORAGE |
            GPUBufferUsage.COPY_SRC |
            GPUBufferUsage.COPY_DST,
        });
      };
      this.staticPotentialBuffer = allocate(this.staticPotentialBuffer);
      this.potentialBufferA = allocate(this.potentialBufferA);
      this.potentialBufferB = allocate(this.potentialBufferB);
      this.penaltyBuffer = allocate(this.penaltyBuffer);
      this.rawDensityBuffer = allocate(this.rawDensityBuffer);
      this.smoothedDensityBuffer = allocate(this.smoothedDensityBuffer);
      this.densityScratchBuffer = allocate(this.densityScratchBuffer);
      this.seedBuffer = allocate(this.seedBuffer);
      this.protectedBuffer = allocate(this.protectedBuffer);
      this.blockedBuffer = allocate(this.blockedBuffer);

      const fields = snapshot
        ? new Float32Array(snapshot.fields.slice().buffer)
        : undefined;
      const staticField =
        fields?.subarray(0, potCells) ?? buildMapPotentialField(scenario);
      this.mapRecoveryAvailable = false;
      for (let i = 0; i < blockedU32.length; i++) {
        if (scenario.blocked[i] || (scenario.streets && !scenario.streets[i]))
          continue;
        const x = ((i % scenario.columns) + 0.5) * scenario.cellSize;
        const y = (Math.floor(i / scenario.columns) + 0.5) * scenario.cellSize;
        const col = Math.min(
          this.potCols - 1,
          Math.floor(x / this.potCellSize),
        );
        const row = Math.min(
          this.potRows - 1,
          Math.floor(y / this.potCellSize),
        );
        if (staticField[row * this.potCols + col] >= impassablePotential * 0.5)
          continue;
        blockedU32[i] |= 2;
        this.mapRecoveryAvailable = true;
      }
      // Bit 4 is a conservative exit proximity mask. Most agents are far from
      // every exit tile, so they can skip the per-exit arrival checks entirely.
      // Keep a cell of padding for GPU float rounding at rectangle boundaries.
      const rasterCellSize = Math.fround(scenario.cellSize);
      const rasterIndex = (
        coordinate: number,
        cells: number,
        padding: number,
      ) =>
        Math.max(
          0,
          Math.min(
            cells - 1,
            Math.floor(coordinate / rasterCellSize) + padding,
          ),
        );
      for (const exit of scenario.exits) {
        const reach = Math.fround(
          Math.fround(exit.radius) + Math.fround(this.potCellSize),
        );
        const firstColumn = rasterIndex(
          Math.fround(exit.x) - reach,
          scenario.columns,
          -1,
        );
        const lastColumn = rasterIndex(
          Math.fround(exit.x) + reach,
          scenario.columns,
          1,
        );
        const firstRow = rasterIndex(
          Math.fround(exit.y) - reach,
          scenario.rows,
          -1,
        );
        const lastRow = rasterIndex(
          Math.fround(exit.y) + reach,
          scenario.rows,
          1,
        );
        for (let row = firstRow; row <= lastRow; row++) {
          for (let column = firstColumn; column <= lastColumn; column++) {
            blockedU32[row * scenario.columns + column] |= 4;
          }
        }
      }
      this.device.queue.writeBuffer(this.mapBlockedBuffer, 0, blockedU32);
      const mask = Uint32Array.from(snapshot?.blocked ?? scenario.blocked);
      const sinks: PotentialSink[] = scenario.exits.map((exit) => ({
        zone: [
          exit.x - exit.radius,
          exit.y - exit.radius,
          exit.radius * 2,
          exit.radius * 2,
        ],
        potential: 0,
      }));
      const seeds = seedPotentialField(
        this.potCols,
        this.potRows,
        this.potCellSize,
        mask,
        sinks,
      );
      const protectedSeeds = seedPotentialField(
        this.potCols,
        this.potRows,
        this.potCellSize,
        mask,
        sinks,
        2.5,
      );
      const protectedMask = Uint32Array.from(protectedSeeds, (value) =>
        value < impassablePotential ? 1 : 0,
      );
      const zero = new Float32Array(potCells);
      this.device.queue.writeBuffer(this.staticPotentialBuffer, 0, staticField);
      this.device.queue.writeBuffer(
        this.potentialBufferA,
        0,
        fields?.subarray(potCells, potCells * 2) ?? staticField,
      );
      this.device.queue.writeBuffer(
        this.penaltyBuffer,
        0,
        fields?.subarray(potCells * 2, potCells * 3) ?? zero,
      );
      this.device.queue.writeBuffer(
        this.smoothedDensityBuffer,
        0,
        fields?.subarray(potCells * 3, potCells * 4) ?? zero,
      );
      this.device.queue.writeBuffer(this.blockedBuffer, 0, mask);
      this.device.queue.writeBuffer(this.seedBuffer, 0, seeds);
      this.device.queue.writeBuffer(this.protectedBuffer, 0, protectedMask);
      this.recreatePotentialBindGroups();
    }
  }

  evacuationStatus(): EvacuationStatus {
    const dots = this.activeCount;
    const peoplePerDot = Math.max(1, this.granulation);
    const evacuatedPeople = Math.min(
      this.totalPeople,
      this.evacuatedCount * peoplePerDot,
    );
    const perExit = this.evacuatedPerExit.map((count) => count * peoplePerDot);
    const finished = this.activeMapAgents === 0;
    const stalled =
      !finished &&
      (this.evacuatedCount === 0
        ? this.simulationTime > 3600
        : this.simulationTime - this.lastEvacuationTime > 600);

    const timeForFraction = (fraction: number): number | undefined => {
      const required = Math.max(1, Math.ceil(dots * fraction));
      return this.evacuatedCount >= required
        ? this.evacuationTimes[required - 1]
        : undefined;
    };

    return {
      isMap: this.isMapScenario,
      simulationSeconds: this.simulationTime,
      totalPeople: this.totalPeople,
      evacuatedPeople,
      remainingAgents: this.activeMapAgents,
      evacuatedPerExit: perExit,
      halfEvacuatedSeconds: timeForFraction(0.5),
      ninetyPercentEvacuatedSeconds: timeForFraction(0.9),
      lastEvacuationSeconds:
        this.evacuatedCount > 0
          ? this.evacuationTimes[this.evacuatedCount - 1]
          : undefined,
      finished,
      stalled,
    };
  }

  dispose(): void {
    this.agentRenderer?.dispose();
    this.telemetryReader?.dispose();
    this.densityScratchBuffer?.destroy();
    this.seedBuffer?.destroy();
    this.protectedBuffer?.destroy();
    this.relaxStateBuffer?.destroy();
    this.relaxStateStaging?.destroy();
    this.agentsBuffer?.destroy();
    this.stagingBuffer?.destroy();
    this.cellCountsBuffer?.destroy();
    this.recoveryBuffer?.destroy();
    this.cellAgentsBuffer?.destroy();
    this.paramsBuffer?.destroy();
    this.rawDensityBuffer?.destroy();
    this.smoothedDensityBuffer?.destroy();
    this.penaltyBuffer?.destroy();
    this.blockedBuffer?.destroy();
    this.staticPotentialBuffer?.destroy();
    this.potentialBufferA?.destroy();
    this.potentialBufferB?.destroy();
    this.exitsBuffer?.destroy();
    this.mapBlockedBuffer?.destroy();
    this.kdeParamsBuffer?.destroy();
    this.kdeDepositMassBuffer?.destroy();
    this.kdeDepositSpeedBuffer?.destroy();
    this.kdeDensityOutputBuffer?.destroy();
    this.kdeStagingBuffer?.destroy();
    this.inFlightBatches = 0;
    this.inFlightWorkPromise = null;
    this.device?.destroy();
    this.device = undefined;
  }
}
