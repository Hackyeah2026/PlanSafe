import { protocolVersion } from "./crowdProtocol.js";

/** Core payload values remain independently validated by the Core dispatcher. */
export interface CoreCommandValues {
  readonly count?: unknown;
  readonly seed?: unknown;
  readonly steps?: unknown;
  readonly socialRepulsionWeight?: unknown;
  readonly granulation?: unknown;
  readonly worldWidth?: unknown;
  readonly worldHeight?: unknown;
  readonly timeScale?: unknown;
  readonly preset?: unknown;
  readonly renderMode?: unknown;
  readonly obstacles?: unknown;
  readonly targets?: unknown;
  readonly weightDistance?: unknown;
  readonly weightOccupancy?: unknown;
  readonly snapshot?: unknown;
}

export type CoreCommand =
  | "advance"
  | "init"
  | "reset"
  | "count"
  | "weight"
  | "render"
  | "granulation"
  | "preset"
  | "timeScale"
  | "set-environment"
  | "snapshot";

/** The Core envelope. Random initialization belongs to Core. */
export function coreCommand(
  session: string,
  sequence: number,
  kind: CoreCommand,
  values: CoreCommandValues,
) {
  if (values.seed !== undefined && values.seed !== null) {
    throw new Error("Core v2 does not accept deterministic seeds.");
  }
  const cmd: Record<string, unknown> = {
    version: protocolVersion,
    session,
    sequence,
    kind,
    count: values.count ?? 0,
    seed: null,
    steps: values.steps ?? 0,
    socialRepulsionWeight: values.socialRepulsionWeight ?? 4.5,
    granulation: values.granulation ?? 1,
  };
  if (values.worldWidth !== undefined && values.worldWidth !== null) {
    cmd.worldWidth = values.worldWidth;
  }
  if (values.worldHeight !== undefined && values.worldHeight !== null) {
    cmd.worldHeight = values.worldHeight;
  }
  if (values.timeScale !== undefined && values.timeScale !== null) {
    cmd.timeScale = values.timeScale;
  }
  if (values.preset !== undefined && values.preset !== null) {
    cmd.preset = values.preset;
  }
  if (values.obstacles !== undefined && values.obstacles !== null) {
    cmd.obstacles = values.obstacles;
  }
  if (values.targets !== undefined && values.targets !== null) {
    cmd.targets = values.targets;
  }
  if (values.weightDistance !== undefined && values.weightDistance !== null) {
    cmd.weightDistance = values.weightDistance;
  }
  if (values.weightOccupancy !== undefined && values.weightOccupancy !== null) {
    cmd.weightOccupancy = values.weightOccupancy;
  }
  if (values.snapshot !== undefined && values.snapshot !== null) {
    cmd.snapshot = values.snapshot;
  }
  return cmd;
}
