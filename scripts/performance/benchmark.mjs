import { spawnSync } from "node:child_process";
import { prepare, root } from "./prepare.mjs";

const mode = process.argv[2] ?? "cpu";
if (!["cpu", "gpu"].includes(mode))
  throw new Error(
    "Usage: benchmark.mjs cpu|gpu [--variant=contact-cull|fine-grid]",
  );

if (mode === "cpu") {
  if (process.argv.length > 3)
    throw new Error("CPU benchmark does not accept additional arguments");
  prepare();
  const result = spawnSync(
    "dotnet",
    [
      "run",
      "--project",
      "scripts/performance/SimulationBenchmark.csproj",
      "--configuration",
      "Release",
      "-p:RunAOTCompilation=false",
      "-p:PublishAot=false",
    ],
    { cwd: root, stdio: "inherit" },
  );
  if (result.error) throw result.error;
  process.exitCode = result.status ?? 1;
} else {
  await import("./gpu-benchmark.mjs");
}
