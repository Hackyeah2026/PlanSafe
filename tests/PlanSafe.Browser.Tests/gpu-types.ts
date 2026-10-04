import type {
  GpuSimulationEngine,
  MapGpuSnapshot,
  ParsedMapScenario,
} from "../../src/PlanSafe.App/TypeScript/crowdSimulatorGpu.js";

// Readback tests need the buffers behind the public API. Keep this inspection
// contract in tests so production callers cannot depend on these private fields.
export type GpuTestEngine = Pick<
  GpuSimulationEngine,
  keyof GpuSimulationEngine
> & {
  device: GPUDevice;
  agentsBuffer: GPUBuffer;
  recoveryBuffer: GPUBuffer;
  paramsBuffer: GPUBuffer;
  exitsBuffer: GPUBuffer;
  staticPotentialBuffer: GPUBuffer;
  potentialBufferA: GPUBuffer;
  penaltyBuffer: GPUBuffer;
  smoothedDensityBuffer: GPUBuffer;
  mapScenario: ParsedMapScenario;
  numExits: number;
  potCols: number;
  mapCols: number;
  mapCellSize: number;
  relaxStepPipeline: GPUComputePipeline;
};

type SnapshotBuffer = "scenario" | "agents" | "fields" | "blocked";
export type EncodedSnapshot = Omit<MapGpuSnapshot, SnapshotBuffer> &
  Record<SnapshotBuffer, string>;
export interface MapReference {
  name: string;
  snapshot: EncodedSnapshot;
  afterOneTick: EncodedSnapshot;
  afterRefinement: EncodedSnapshot;
}

export interface FieldReference {
  name: string;
  width: number;
  height: number;
  cellSize: number;
  granulation: number;
  weightDistance: number;
  weightOccupancy: number;
  cols: number;
  rows: number;
  obstacles: number[][];
  targets: {
    x: number;
    y: number;
    width: number;
    height: number;
    capacity: number;
    currentOccupancy: number;
    isActive: boolean;
  }[];
  xs: number[];
  ys: number[];
  samples: number[][];
  flows: number[][];
  staticField: number[];
  dynamicField: number[];
  density: number[];
  penalty: number[];
}
