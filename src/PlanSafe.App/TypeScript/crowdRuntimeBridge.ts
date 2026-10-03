import {
  coreCommand,
  type CoreCommand,
  type CoreCommandValues,
} from "./crowdCoreCommand.js";
import {
  maxAgents,
  protocolVersion,
  resolveWorkerRuntimeUrl,
  type DensityGridData,
  type EvacuationStatus,
  type ProfilingWindow,
} from "./crowdProtocol.js";

export interface RuntimeMemoryView {
  copyTo(destination: Float64Array): void;
  dispose(): void;
}
type RuntimeDispatch = (request: string) => string;
export type BrowserCoreCommand = Exclude<CoreCommand, "advance">;
export interface OwnedPreview {
  readonly version: number;
  readonly session: string;
  readonly sequence: number;
  readonly runGeneration: number;
  readonly tick: number;
  readonly count: number;
  readonly values: Float64Array;
  readonly density?: DensityGridData;
}

interface RuntimePreviewResponse {
  readonly version: number;
  readonly session: string;
  readonly sequence: number;
  readonly runGeneration: number;
  readonly tick: number;
  readonly count: number;
  readonly storageGeneration: number;
}

export interface RuntimeModule {
  readonly dotnet: {
    create(): Promise<{
      getAssemblyExports(assemblyName: string): Promise<{
        PlanSafe?: {
          Worker?: {
            WorkerExports?: {
              DispatchRender?: RuntimeDispatch;
              DispatchFixedTicks?: (ticks: number) => number;
              CapturePreview?: () => string;
              GetRenderFrame?: () => RuntimeMemoryView;
              LoadMapScenario?: (scenario: Uint8Array) => void;
              GetEvacuationStatus?: () => string;
              DispatchJson?: RuntimeDispatch;
            };
          };
        };
      }>;
      getConfig(): { mainAssemblyName: string };
    }>;
  };
}
interface RuntimeRenderResponse {
  readonly count: number;
  readonly generation: number;
  readonly runGeneration: number;
  readonly sequence: number;
  readonly session: string;
  readonly tick: number;
  readonly version: number;
}

function isValidCount(value: unknown): value is number {
  return (
    typeof value === "number" &&
    Number.isSafeInteger(value) &&
    value >= 1 &&
    value <= maxAgents
  );
}

function isRunBoundary(kind: BrowserCoreCommand): boolean {
  return (
    kind === "reset" ||
    kind === "count" ||
    kind === "preset" ||
    kind === "granulation"
  );
}

function validateSteps(value: unknown): number {
  const steps = Number(value);
  if (!Number.isSafeInteger(steps) || steps < 1 || steps > 5)
    throw new Error("Advance step count is out of range.");
  return steps;
}

function reserveTickAddition(currentTick: number, steps: number): number {
  if (currentTick > Number.MAX_SAFE_INTEGER - steps)
    throw new Error("Runtime progress is exhausted.");
  return currentTick + steps;
}

export interface ISimulationEngine {
  readonly count: number;
  boot(runtimeUrl?: string): Promise<void>;
  dispatch(
    session: string,
    kind: BrowserCoreCommand,
    values: CoreCommandValues,
    profiler: ProfilingWindow,
  ): void;
  advanceFixedTicks(ticks: number): number;
  capturePreview(): OwnedPreview | Promise<OwnedPreview>;
  syncInFlight?(maxAllowed?: number): Promise<void>;
  dispose(): void;
  loadMapScenario?(scenario: Uint8Array): void;
  evacuationStatus?(): EvacuationStatus;
}

export class RuntimeBridge implements ISimulationEngine {
  private dispatchRender: RuntimeDispatch | undefined;
  private dispatchFixedTicks: ((ticks: number) => number) | undefined;
  private capturePreviewExport: (() => string) | undefined;
  private dispatchJsonFn: RuntimeDispatch | undefined;
  private getRenderFrame: (() => RuntimeMemoryView) | undefined;
  private loadMapScenarioExport: ((scenario: Uint8Array) => void) | undefined;
  private evacuationStatusExport: (() => string) | undefined;
  private renderView: RuntimeMemoryView | undefined;
  private renderGeneration = 0;
  private dispatchSequence = 0;
  private renderCount = 0;
  private activeSession: string | undefined;
  private activeRunGeneration = 0;
  private activeTick = 0;

  get count(): number {
    return this.renderCount;
  }

  get hasView(): boolean {
    return this.renderView !== undefined;
  }

  async boot(
    runtimeUrl: string,
    load: (url: string) => Promise<RuntimeModule> = (url) => import(url),
  ): Promise<void> {
    if (this.dispatchRender) return;
    const scope = self as unknown as {
      importScripts?: (...urls: string[]) => void;
    };
    const saved = scope.importScripts;
    try {
      scope.importScripts = undefined;
      const runtime = await load(
        resolveWorkerRuntimeUrl(runtimeUrl, import.meta.url),
      );
      const dotnet = await runtime.dotnet.create();
      const exports = (
        await dotnet.getAssemblyExports(dotnet.getConfig().mainAssemblyName)
      ).PlanSafe?.Worker?.WorkerExports;
      const dispatchRender = exports?.DispatchRender;
      const dispatchFixedTicks = exports?.DispatchFixedTicks;
      const capturePreview = exports?.CapturePreview;
      const getRenderFrame = exports?.GetRenderFrame;
      if (
        !dispatchRender ||
        !dispatchFixedTicks ||
        !capturePreview ||
        !getRenderFrame
      )
        throw new Error(
          "Worker runtime did not expose required runtime exports.",
        );
      this.dispatchRender = dispatchRender;
      this.dispatchFixedTicks = dispatchFixedTicks;
      this.capturePreviewExport = capturePreview;
      this.getRenderFrame = getRenderFrame;
      this.loadMapScenarioExport = exports?.LoadMapScenario;
      this.evacuationStatusExport = exports?.GetEvacuationStatus;
      this.dispatchJsonFn = exports?.DispatchJson;
    } finally {
      scope.importScripts = saved;
    }
  }

  dispose(): void {
    const view = this.renderView;
    this.renderView = undefined;
    view?.dispose();
  }

  loadMapScenario(scenario: Uint8Array): void {
    if (!this.loadMapScenarioExport)
      throw new Error("Worker runtime does not support map scenarios.");
    if (this.activeSession)
      throw new Error("A map scenario must be loaded before initialization.");
    this.loadMapScenarioExport(scenario);
  }

  evacuationStatus(): EvacuationStatus {
    if (!this.evacuationStatusExport)
      throw new Error("Worker runtime does not report evacuation status.");
    const status = JSON.parse(
      this.evacuationStatusExport(),
    ) as EvacuationStatus;
    if (
      !status ||
      typeof status !== "object" ||
      typeof status.simulationSeconds !== "number" ||
      !Array.isArray(status.evacuatedPerExit)
    )
      throw new Error("Invalid runtime evacuation status.");
    return status;
  }

  copyTo(destination: Float64Array): void {
    if (!this.renderView)
      throw new Error("Worker runtime did not provide a render MemoryView.");
    this.renderView.copyTo(destination);
  }

  advanceFixedTicks(ticks: number): number {
    if (!this.dispatchFixedTicks)
      throw new Error("Worker runtime did not expose fixed-tick advancement.");
    const steps = validateSteps(ticks);
    if (!this.activeSession || this.dispatchSequence < 1)
      throw new Error("Worker runtime is not initialized.");
    if (this.dispatchSequence >= Number.MAX_SAFE_INTEGER)
      throw new Error("Runtime progress is exhausted.");

    const expectedTick = reserveTickAddition(this.activeTick, steps);
    const result = this.dispatchFixedTicks(steps);
    if (!Number.isSafeInteger(result) || result !== expectedTick)
      throw new Error(
        "Worker runtime returned unexpected fixed-tick progress.",
      );
    this.dispatchSequence++;
    this.activeTick = result;
    return result;
  }

  capturePreview(): OwnedPreview {
    if (!this.capturePreviewExport)
      throw new Error("Worker runtime did not expose preview capture.");
    if (!this.activeSession || this.dispatchSequence < 1)
      throw new Error("Worker runtime is not initialized.");

    let metadata: RuntimePreviewResponse;
    try {
      metadata = JSON.parse(
        this.capturePreviewExport(),
      ) as RuntimePreviewResponse;
    } catch (error) {
      throw new Error(
        `Worker runtime preview capture failed: ${error instanceof Error ? error.message : String(error)}`,
      );
    }
    if (
      !metadata ||
      typeof metadata !== "object" ||
      metadata.version !== protocolVersion ||
      metadata.session !== this.activeSession ||
      metadata.sequence !== this.dispatchSequence ||
      metadata.runGeneration !== this.activeRunGeneration ||
      metadata.tick !== this.activeTick ||
      !isValidCount(metadata.count) ||
      !Number.isSafeInteger(metadata.storageGeneration) ||
      metadata.storageGeneration < 1
    )
      throw new Error("Invalid runtime preview metadata.");

    this.renderCount = metadata.count;
    this.acquireView(metadata.storageGeneration);
    const values = new Float64Array(metadata.count * 5);
    this.renderView!.copyTo(values);
    return {
      version: metadata.version,
      session: metadata.session,
      sequence: metadata.sequence,
      runGeneration: metadata.runGeneration,
      tick: metadata.tick,
      count: metadata.count,
      values,
    };
  }

  dispatch(
    session: string,
    kind: BrowserCoreCommand,
    values: CoreCommandValues,
    profiler: ProfilingWindow,
  ): void {
    if (this.dispatchSequence >= Number.MAX_SAFE_INTEGER)
      throw new Error("Runtime progress is exhausted.");
    const request = coreCommand(
      session,
      this.dispatchSequence + 1,
      kind,
      values,
    );
    let response: RuntimeRenderResponse;
    const started = performance.now();
    try {
      response = JSON.parse(
        this.dispatchRender?.(JSON.stringify(request)) ?? "",
      ) as RuntimeRenderResponse;
    } catch (error) {
      throw new Error(
        `Worker runtime dispatch failed: ${error instanceof Error ? error.message : String(error)}`,
      );
    }
    profiler.add({
      simulationDispatchMilliseconds: performance.now() - started,
    });
    const validationStarted = performance.now();
    const expectedRunGeneration =
      kind === "init"
        ? 1
        : this.activeRunGeneration + (isRunBoundary(kind) ? 1 : 0);
    const expectedTick =
      kind === "init" || isRunBoundary(kind) ? 0 : this.activeTick;
    if (
      !response ||
      typeof response !== "object" ||
      response.version !== protocolVersion ||
      response.session !== session ||
      response.sequence !== request.sequence ||
      !isValidCount(response.count) ||
      !Number.isSafeInteger(response.generation) ||
      response.generation < 1 ||
      response.runGeneration !== expectedRunGeneration ||
      response.tick !== expectedTick ||
      (this.renderCount !== 0 &&
        response.count !== this.renderCount &&
        kind !== "count" &&
        kind !== "preset" &&
        kind !== "granulation")
    )
      throw new Error("Invalid typed runtime response envelope.");
    this.dispatchSequence = request.sequence;
    this.activeSession = session;
    this.activeRunGeneration = response.runGeneration;
    this.activeTick = response.tick;
    if (
      kind === "init" ||
      kind === "count" ||
      kind === "preset" ||
      kind === "granulation"
    )
      this.renderCount = response.count;
    this.acquireView(response.generation);
    if (!this.renderView)
      throw new Error("Worker runtime did not provide a render MemoryView.");
    profiler.add({
      frameValidationMilliseconds: performance.now() - validationStarted,
    });
  }

  private acquireView(generation: number): void {
    if (!this.renderView || this.renderGeneration !== generation) {
      this.dispose();
      this.renderView = this.getRenderFrame?.();
      this.renderGeneration = generation;
    }
    if (!this.renderView)
      throw new Error("Worker runtime did not provide a render MemoryView.");
  }

  dispatchJson(
    session: string,
    kind: CoreCommand,
    values: CoreCommandValues,
  ): string {
    const request = coreCommand(
      session,
      this.dispatchSequence + 1,
      kind,
      values,
    );
    const result = this.dispatchJsonFn?.(JSON.stringify(request));
    if (!result) {
      throw new Error(
        "Worker runtime did not return response for DispatchJson.",
      );
    }
    this.dispatchSequence = request.sequence as number;
    return result;
  }
}
