export const protocolVersion = 2;
export const maxAgents = 100_000;
export const maxWeight = 10;
export const maxDimension = 16_384;
export const maxPixelRatio = 4;
export const maxPhysicalCanvasDimension = 16_384;
export const maxCanvasPixels = 67_108_864;

export type RenderMode = "agents" | "heatmap" | "density";
export type PacingMode = "paced" | "unlimited";
export type RenderFps = 15 | 30 | 60 | "display";

export interface Dimensions {
  readonly height: number;
  readonly pixelRatio: number;
  readonly width: number;
}

/** Core preset that simulates a previously loaded real map area. */
export type SimulationPreset = "map";

export interface SimulationSettings {
  readonly count: number;
  readonly preset?: SimulationPreset;
  readonly granulation?: number;
  readonly renderMode: RenderMode;
  readonly socialRepulsionWeight: number;
  readonly worldWidth?: number;
  readonly worldHeight?: number;
  readonly timeScale?: number;
  readonly pacingMode?: PacingMode;
}

export interface DensityGridData {
  readonly width: number;
  readonly height: number;
  readonly data: Float32Array;
  readonly maxDensity?: number;
}

export interface RenderFrame {
  readonly posX: readonly number[] | Float64Array;
  readonly posY: readonly number[] | Float64Array;
  readonly radius: readonly number[] | Float64Array;
  readonly vx: readonly number[] | Float64Array;
  readonly vy: readonly number[] | Float64Array;
  readonly density?: DensityGridData;
}

/** Identity checked before dispatch; payload validation stays in each worker branch. */
export interface WorkerIdentity {
  readonly sequence: number;
  readonly session: string;
  readonly version: typeof protocolVersion;
}

/** No role tag is added to the wire: each endpoint selects its payload union. */
type LifecyclePayload =
  | { readonly kind: "start" }
  | { readonly kind: "pause" }
  | { readonly kind: "dispose" }
  | { readonly kind: "reset-profile"; readonly measurementGeneration: number };
type RenderPayload = {
  readonly kind: "render";
  readonly renderMode: RenderMode;
} & Partial<SimulationSettings>;
type CountPayload = {
  readonly kind: "count";
  readonly count: number;
} & Partial<SimulationSettings>;
type ResetPayload = {
  readonly kind: "reset";
  readonly seed?: number | null;
} & Partial<SimulationSettings>;
type ResizePayload = { readonly kind: "resize" } & Dimensions;

export type SimulatorWorkerPayload =
  | LifecyclePayload
  | RenderPayload
  | CountPayload
  | ResetPayload
  | ({
      readonly kind: "weight";
      readonly socialRepulsionWeight: number;
    } & Partial<SimulationSettings>)
  | ({
      readonly kind: "preset";
      readonly worldWidth: number;
      readonly worldHeight: number;
      readonly count?: number;
      readonly granulation?: number;
    } & Partial<SimulationSettings>)
  | {
      readonly kind: "time-scale";
      readonly timeScale: number;
      readonly pacingMode: PacingMode;
    }
  | { readonly kind: "verify"; readonly runtimeUrl: string }
  | { readonly kind: "load-scenario"; readonly scenario: ArrayBuffer }
  | { readonly kind: "detach-preview"; readonly previewAttempt: number }
  | {
      readonly kind: "attach-preview";
      readonly framePort: MessagePort;
      readonly previewAttempt: number;
    }
  | ({
      readonly kind: "init";
      readonly seed?: number | null;
      readonly runtimeUrl?: string;
      readonly canvas?: never;
    } & SimulationSettings)
  | {
      readonly kind: "set-environment";
      readonly obstacles?: unknown[];
      readonly targets?: unknown[];
      readonly weightDistance?: number;
      readonly weightOccupancy?: number;
    }
  | {
      readonly kind: "snapshot";
      readonly snapshot?: unknown;
    };

export type RendererWorkerPayload =
  | LifecyclePayload
  | RenderPayload
  | ResizePayload
  | { readonly kind: "render-fps"; readonly renderFps: RenderFps }
  | { readonly kind: "visibility"; readonly visible: boolean }
  | { readonly kind: "verify" }
  | {
      readonly kind: "zoom";
      readonly factor: number;
      readonly mouseX?: number;
      readonly mouseY?: number;
    }
  | { readonly kind: "pan"; readonly dx: number; readonly dy: number }
  | { readonly kind: "reset-view" }
  | {
      readonly kind: "view";
      readonly scale: number;
      readonly offsetX: number;
      readonly offsetY: number;
    }
  | {
      readonly kind: "whiskers";
      readonly show: boolean;
      readonly length?: number;
    }
  | {
      readonly kind: "set-environment";
      readonly obstacles?: unknown[];
      readonly targets?: unknown[];
      readonly weightDistance?: number;
      readonly weightOccupancy?: number;
    }
  | {
      readonly kind: "get-world-pos";
      readonly clientX?: number;
      readonly clientY?: number;
    }
  | ({
      readonly kind: "init";
      readonly canvas: OffscreenCanvas;
      readonly framePort: MessagePort;
      readonly epoch: number;
      readonly previewAttempt: number;
      readonly renderFps: RenderFps;
      readonly count: number;
      readonly renderMode: RenderMode;
      readonly overlay?: boolean;
    } & Dimensions &
      Partial<SimulationSettings>);

export type WorkerPayload = SimulatorWorkerPayload | RendererWorkerPayload;
export type WorkerRequest = WorkerIdentity & WorkerPayload;

export interface InboundWorkerEnvelope extends WorkerIdentity {
  readonly kind: string;
  readonly canvas?: unknown;
  readonly framePort?: unknown;
  readonly epoch?: unknown;
  readonly measurementGeneration?: unknown;
  readonly runtimeUrl?: unknown;
  readonly seed?: unknown;
  readonly steps?: unknown;
  readonly count?: unknown;
  readonly renderMode?: unknown;
  readonly socialRepulsionWeight?: unknown;
  readonly granulation?: unknown;
  readonly width?: unknown;
  readonly height?: unknown;
  readonly pixelRatio?: unknown;
  readonly worldWidth?: unknown;
  readonly worldHeight?: unknown;
  readonly timeScale?: unknown;
  readonly pacingMode?: unknown;
  readonly renderFps?: unknown;
  readonly previewAttempt?: unknown;
  readonly factor?: unknown;
  readonly mouseX?: unknown;
  readonly mouseY?: unknown;
  readonly dx?: unknown;
  readonly dy?: unknown;
  readonly show?: unknown;
  readonly length?: unknown;
  readonly visible?: unknown;
  readonly preset?: unknown;
  readonly scenario?: unknown;
  readonly overlay?: unknown;
  readonly scale?: unknown;
  readonly offsetX?: unknown;
  readonly offsetY?: unknown;
}

export interface PreviewRequestMessage {
  readonly version: typeof protocolVersion;
  readonly kind: "preview-request";
  readonly session: string;
  readonly requestId: number;
}

export interface PreviewInvalidatedMessage {
  readonly version: typeof protocolVersion;
  readonly kind: "preview-invalidated";
  readonly session: string;
}

export interface SemanticDrawMessage {
  readonly version: typeof protocolVersion;
  readonly kind: "preview";
  readonly session: string;
  readonly requestId: number;
  readonly frameId: number;
  readonly runGeneration: number;
  readonly tick: number;
  readonly sequence: number;
  readonly epoch: number;
  readonly count: number;
  readonly granulation: number;
  readonly renderMode: RenderMode;
  readonly worldWidth: number;
  readonly worldHeight: number;
  readonly timeScale: 1;
  readonly density?: DensityGridData;
}

export interface EvacuationStatus {
  readonly isMap: boolean;
  readonly simulationSeconds: number;
  readonly totalPeople: number;
  readonly evacuatedPeople: number;
  readonly remainingAgents: number;
  readonly evacuatedPerExit: readonly number[];
  readonly halfEvacuatedSeconds?: number;
  readonly ninetyPercentEvacuatedSeconds?: number;
  readonly lastEvacuationSeconds?: number;
  readonly finished: boolean;
  readonly stalled: boolean;
}

export interface EvacuationStatusMessage {
  readonly version: typeof protocolVersion;
  readonly kind: "evacuation-status";
  readonly session: string;
  readonly status: EvacuationStatus;
}

export function isEvacuationStatusMessage(
  value: unknown,
): value is EvacuationStatusMessage {
  if (typeof value !== "object" || value === null) return false;
  const message = value as Partial<EvacuationStatusMessage>;
  const status = message.status as Partial<EvacuationStatus> | undefined;
  return (
    message.version === protocolVersion &&
    message.kind === "evacuation-status" &&
    typeof message.session === "string" &&
    typeof status === "object" &&
    status !== null &&
    typeof status.simulationSeconds === "number" &&
    typeof status.evacuatedPeople === "number" &&
    Array.isArray(status.evacuatedPerExit)
  );
}

export interface PreviewReadyMessage {
  readonly version: typeof protocolVersion;
  readonly kind: "preview-ready";
  readonly session: string;
  readonly previewAttempt: number;
}

export interface PreviewFailure {
  readonly version: typeof protocolVersion;
  readonly kind: "preview-failure";
  readonly session: string;
  readonly previewAttempt: number;
  readonly error: string;
}

export interface WorkerMetrics {
  readonly backingHeight: number;
  readonly backingWidth: number;
  readonly droppedFrames: number;
  readonly frameValidationMilliseconds: number;
  readonly frameTransferPreparationMilliseconds: number;
  readonly frameTransferPublications: number;
  readonly kind: "metrics";
  readonly measurementGeneration: number;
  readonly pixelRatio: number;
  readonly renderAgentsMilliseconds: number;
  readonly renderFramesPerSecond: number;
  readonly renderHeatmapMilliseconds: number;
  readonly rendererDrawMilliseconds: number;
  readonly rendererFrameIngressMilliseconds: number;
  readonly rendererFramesReceived: number;
  readonly rendererMaterializationMilliseconds: number;
  readonly rendererFramesMaterialized: number;
  readonly session: string;
  readonly simulationDispatchMilliseconds: number;
  readonly simulationStepsPerSecond: number;
  readonly version: typeof protocolVersion;
}

export function calculatePerSecond(
  count: number,
  elapsedMilliseconds: number,
): number {
  return (count * 1000) / elapsedMilliseconds;
}

export interface WorkerResponse {
  readonly error?: string;
  readonly ok: boolean;
  readonly measurementGeneration?: number;
  readonly sequence: number;
  readonly session: string;
  readonly version: typeof protocolVersion;
  readonly engine?: "webgpu" | "wasm";
  readonly gpuError?: string;
}

export function normalizeMeasurementGeneration(value: unknown): number {
  if (!isPositiveSafeInteger(value)) {
    throw new Error("Measurement generation must be a positive safe integer.");
  }
  return value;
}

export function normalizeCount(value: unknown): number {
  if (
    typeof value !== "number" ||
    !Number.isInteger(value) ||
    value < 1 ||
    value > maxAgents
  ) {
    throw new Error(
      `Agent count must be an integer between 1 and ${maxAgents}.`,
    );
  }
  return value;
}

export function normalizeWeight(value: unknown): number {
  if (
    typeof value !== "number" ||
    !Number.isFinite(value) ||
    value < 0 ||
    value > maxWeight
  ) {
    throw new Error(
      `Social repulsion weight must be between 0 and ${maxWeight}.`,
    );
  }
  return value;
}

export function normalizeTimeScale(value: unknown): number {
  if (
    typeof value !== "number" ||
    !Number.isFinite(value) ||
    value < 1 ||
    value > 50
  )
    throw new RangeError("Time scale must be finite and between 1 and 50.");
  return value;
}

export function normalizePacingMode(value: unknown): PacingMode {
  if (value !== "paced" && value !== "unlimited")
    throw new RangeError("Pacing mode must be paced or unlimited.");
  return value;
}

export function normalizeRenderFps(value: unknown): RenderFps {
  if (value !== 15 && value !== 30 && value !== 60 && value !== "display")
    throw new RangeError("Render FPS must be 15, 30, 60, or display.");
  return value;
}

export function normalizeRenderMode(value: unknown): RenderMode {
  if (value !== "agents" && value !== "heatmap" && value !== "density") {
    throw new Error("Unsupported render mode.");
  }
  return value;
}

export function normalizeGranulation(value: unknown): number {
  if (value === undefined || value === null) {
    return 1;
  }
  if (
    typeof value !== "number" ||
    !Number.isInteger(value) ||
    value < 1 ||
    value > 25
  ) {
    throw new Error("Granulation must be an integer between 1 and 25.");
  }
  return value;
}

export function normalizeDimensions(value: {
  readonly width?: unknown;
  readonly height?: unknown;
  readonly pixelRatio?: unknown;
}): Dimensions {
  const { width, height, pixelRatio } = value;
  if (
    typeof width !== "number" ||
    typeof height !== "number" ||
    typeof pixelRatio !== "number" ||
    !Number.isInteger(width) ||
    !Number.isInteger(height) ||
    !Number.isFinite(pixelRatio) ||
    width < 1 ||
    width > maxDimension ||
    height < 1 ||
    height > maxDimension ||
    pixelRatio < 1 ||
    pixelRatio > maxPixelRatio
  ) {
    throw new Error("Canvas dimensions or device pixel ratio are invalid.");
  }

  physicalCanvasDimensions({ width, height, pixelRatio });
  return { width, height, pixelRatio };
}

export function physicalCanvasDimensions(dimensions: Dimensions): {
  readonly height: number;
  readonly width: number;
} {
  const width = Math.round(dimensions.width * dimensions.pixelRatio);
  const height = Math.round(dimensions.height * dimensions.pixelRatio);
  if (
    !Number.isSafeInteger(width) ||
    !Number.isSafeInteger(height) ||
    width < 1 ||
    height < 1 ||
    width > maxPhysicalCanvasDimension ||
    height > maxPhysicalCanvasDimension ||
    width > Math.floor(maxCanvasPixels / height)
  ) {
    throw new Error("Canvas backing dimensions exceed supported limits.");
  }

  return { width, height };
}

export function isWorkerResponse(value: unknown): value is WorkerResponse {
  if (typeof value !== "object" || value === null) {
    return false;
  }

  const response = value as Partial<WorkerResponse>;
  return (
    response.version === protocolVersion &&
    typeof response.session === "string" &&
    response.session.length > 0 &&
    typeof response.sequence === "number" &&
    Number.isSafeInteger(response.sequence) &&
    response.sequence > 0 &&
    typeof response.ok === "boolean" &&
    (response.measurementGeneration === undefined ||
      isPositiveSafeInteger(response.measurementGeneration)) &&
    (response.error === undefined || typeof response.error === "string")
  );
}

export function isWorkerMetrics(value: unknown): value is WorkerMetrics {
  if (typeof value !== "object" || value === null) {
    return false;
  }

  const metrics = value as Partial<WorkerMetrics>;
  return (
    metrics.version === protocolVersion &&
    metrics.kind === "metrics" &&
    isNonNegativeSafeInteger(metrics.measurementGeneration) &&
    typeof metrics.session === "string" &&
    metrics.session.length > 0 &&
    metrics.session.length <= 64 &&
    isNonNegativeFinite(metrics.renderFramesPerSecond) &&
    isNonNegativeFinite(metrics.simulationStepsPerSecond) &&
    isNonNegativeFinite(metrics.simulationDispatchMilliseconds) &&
    isNonNegativeFinite(metrics.frameValidationMilliseconds) &&
    isNonNegativeFinite(metrics.frameTransferPreparationMilliseconds) &&
    isNonNegativeSafeInteger(metrics.frameTransferPublications) &&
    isNonNegativeFinite(metrics.renderAgentsMilliseconds) &&
    isNonNegativeFinite(metrics.renderHeatmapMilliseconds) &&
    isNonNegativeFinite(metrics.rendererDrawMilliseconds) &&
    isNonNegativeFinite(metrics.rendererFrameIngressMilliseconds) &&
    isNonNegativeSafeInteger(metrics.rendererFramesReceived) &&
    isNonNegativeFinite(metrics.rendererMaterializationMilliseconds) &&
    isNonNegativeSafeInteger(metrics.rendererFramesMaterialized) &&
    isNonNegativeSafeInteger(metrics.droppedFrames) &&
    isPositiveSafeInteger(metrics.backingWidth) &&
    isPositiveSafeInteger(metrics.backingHeight) &&
    typeof metrics.pixelRatio === "number" &&
    Number.isFinite(metrics.pixelRatio) &&
    metrics.pixelRatio >= 1
  );
}

function isNonNegativeFinite(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value) && value >= 0;
}

function isNonNegativeSafeInteger(value: unknown): value is number {
  return Number.isSafeInteger(value) && typeof value === "number" && value >= 0;
}

function isPositiveSafeInteger(value: unknown): value is number {
  return Number.isSafeInteger(value) && typeof value === "number" && value >= 1;
}

export interface ProfileSample {
  readonly frameTransferPreparationMilliseconds?: number;
  readonly frameTransferPublications?: number;
  readonly frameValidationMilliseconds?: number;
  readonly renderAgentsMilliseconds?: number;
  readonly renderHeatmapMilliseconds?: number;
  readonly rendererDrawMilliseconds?: number;
  readonly simulationDispatchMilliseconds?: number;
}

export class ProfilingWindow {
  private frameTransferPreparationMilliseconds = 0;
  private frameTransferPublications = 0;
  private frameValidationMilliseconds = 0;
  private renderAgentsMilliseconds = 0;
  private renderHeatmapMilliseconds = 0;
  private rendererDrawMilliseconds = 0;
  private simulationDispatchMilliseconds = 0;

  public add(sample: ProfileSample): void {
    this.frameTransferPreparationMilliseconds +=
      sample.frameTransferPreparationMilliseconds ?? 0;
    this.frameTransferPublications += sample.frameTransferPublications ?? 0;
    this.frameValidationMilliseconds += sample.frameValidationMilliseconds ?? 0;
    this.renderAgentsMilliseconds += sample.renderAgentsMilliseconds ?? 0;
    this.renderHeatmapMilliseconds += sample.renderHeatmapMilliseconds ?? 0;
    this.rendererDrawMilliseconds += sample.rendererDrawMilliseconds ?? 0;
    this.simulationDispatchMilliseconds +=
      sample.simulationDispatchMilliseconds ?? 0;
  }

  public take(): Required<ProfileSample> {
    const result = {
      frameTransferPreparationMilliseconds:
        this.frameTransferPreparationMilliseconds,
      frameTransferPublications: this.frameTransferPublications,
      frameValidationMilliseconds: this.frameValidationMilliseconds,
      renderAgentsMilliseconds: this.renderAgentsMilliseconds,
      renderHeatmapMilliseconds: this.renderHeatmapMilliseconds,
      rendererDrawMilliseconds: this.rendererDrawMilliseconds,
      simulationDispatchMilliseconds: this.simulationDispatchMilliseconds,
    };
    this.frameTransferPreparationMilliseconds = 0;
    this.frameTransferPublications = 0;
    this.frameValidationMilliseconds = 0;
    this.renderAgentsMilliseconds = 0;
    this.renderHeatmapMilliseconds = 0;
    this.rendererDrawMilliseconds = 0;
    this.simulationDispatchMilliseconds = 0;
    return result;
  }
}

export function isRenderFrame(value: unknown): value is RenderFrame {
  if (typeof value !== "object" || value === null) {
    return false;
  }

  const frame = value as Partial<RenderFrame>;
  const { posX, posY, radius, vx, vy } = frame;
  const lanes = [posX, posY, vx, vy, radius];
  if (
    !lanes.every(
      (lane) => Array.isArray(lane) || lane instanceof Float64Array,
    ) ||
    posX === undefined ||
    posY === undefined ||
    vx === undefined ||
    vy === undefined ||
    radius === undefined ||
    posX.length !== posY.length ||
    posX.length !== vx.length ||
    posX.length !== vy.length ||
    posX.length !== radius.length
  ) {
    return false;
  }

  return lanes.every((values) =>
    values.every((entry) => Number.isFinite(entry)),
  );
}

export interface WorkerFailure {
  readonly error: string;
  readonly kind: "failure";
  readonly session?: string;
  readonly stack?: string;
  readonly version: typeof protocolVersion;
}

export function isWorkerFailure(value: unknown): value is WorkerFailure {
  if (typeof value !== "object" || value === null) {
    return false;
  }

  const failure = value as Partial<WorkerFailure>;
  return (
    failure.version === protocolVersion &&
    failure.kind === "failure" &&
    typeof failure.error === "string" &&
    failure.error.length > 0 &&
    failure.error.length <= 512 &&
    (failure.session === undefined || typeof failure.session === "string") &&
    (failure.stack === undefined ||
      (typeof failure.stack === "string" && failure.stack.length <= 2048))
  );
}

const workerRuntimePath = /^worker\/[A-Fa-f0-9]{64}\/_framework\/dotnet\.js$/;

export function resolveWorkerRuntimeUrl(
  value: unknown,
  moduleUrl: string | URL,
): string {
  if (typeof value !== "string") {
    throw new Error("Worker runtime URL is invalid.");
  }
  const runtimePath = workerRuntimePath.exec(value);
  if (!runtimePath || runtimePath[0] !== value) {
    throw new Error("Worker runtime URL is invalid.");
  }

  try {
    const module = new URL(moduleUrl);
    const application = new URL("../", module);
    const runtime = new URL(value, application);
    if (
      runtime.origin !== module.origin ||
      runtime.pathname !== `${application.pathname}${value}`
    ) {
      throw new Error("Worker runtime URL is invalid.");
    }

    return runtime.href;
  } catch {
    throw new Error("Worker runtime URL is invalid.");
  }
}
