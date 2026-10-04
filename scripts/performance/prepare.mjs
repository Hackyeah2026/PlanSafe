import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

export const root = fileURLToPath(new URL("../../", import.meta.url));
const output = new URL("../../.run/simulation-perf/", import.meta.url);

function replaceOnce(source, marker, replacement) {
  if (source.split(marker).length !== 2) {
    throw new Error(`Performance instrumentation marker changed: ${marker}`);
  }
  return source.replace(marker, replacement);
}

export function prepare() {
  mkdirSync(output, { recursive: true });
  let source = readFileSync(
    new URL(
      "../../src/PlanSafe.App/Simulation/CrowdSimulationEngine.cs",
      import.meta.url,
    ),
    "utf8",
  )
    .replace(/^\uFEFF/, "")
    .replaceAll("\r\n", "\n");
  source = replaceOnce(
    source,
    "namespace PlanSafe.App.Simulation;",
    "namespace PerfProbe;",
  );
  source =
    "using System.Diagnostics;\nusing PlanSafe.App.Simulation;\nusing PlanSafe.Contracts.Simulation;\nusing TargetSelector = PlanSafe.App.Simulation.TargetSelector;\n" +
    source;
  const variant = replaceOnce(
    source.replace("namespace PerfProbe;", "namespace PerfVariant;"),
    "Array.Copy(target, source, TotalCells);",
    "(source, target) = (target, source);",
  );
  writeFileSync(new URL("VariantEngine.cs", output), variant);

  const start = source.indexOf("    public void UpdatePhysics(double dt)");
  const end = source.indexOf(
    "    /// <summary>Switches to a real map area.",
    start,
  );
  if (start < 0 || end < 0)
    throw new Error("Cannot locate UpdatePhysics for instrumentation");
  let part = source.slice(start, end);
  const replacements = [
    [
      "        int activeCount = ActiveAgentCount;",
      "        long wholeStart = Stopwatch.GetTimestamp();\n        int activeCount = ActiveAgentCount;",
    ],
    [
      "        SpatialHashGridIndex.Clear();",
      "        Profile.Add(0, wholeStart);\n        wholeStart = Stopwatch.GetTimestamp();\n        SpatialHashGridIndex.Clear();",
    ],
    [
      "        double scaleSqrtG = Math.Sqrt(Granulation);",
      "        Profile.Add(1, wholeStart);\n        double scaleSqrtG = Math.Sqrt(Granulation);",
    ],
    [
      "            // 1. Bazowy kierunek z pola potencjału",
      "            bool sample = (agentIndex & 15) == 0;\n            long phaseStart = sample ? Stopwatch.GetTimestamp() : 0;\n            // 1. Bazowy kierunek z pola potencjału",
    ],
    [
      "            // 4. Interakcje z sąsiadami",
      "            if (sample) { Profile.Add(2, phaseStart, 16); phaseStart = Stopwatch.GetTimestamp(); }\n            // 4. Interakcje z sąsiadami",
    ],
    [
      "            double reliefForceX = 0.0;",
      "            if (sample) { Profile.Add(3, phaseStart, 16); phaseStart = Stopwatch.GetTimestamp(); }\n            double reliefForceX = 0.0;",
    ],
    [
      "            for (int k = 0; k < pass2Count; k++)",
      "            if (sample) { Profile.Add(4, phaseStart, 16); phaseStart = Stopwatch.GetTimestamp(); }\n            for (int k = 0; k < pass2Count; k++)",
    ],
    [
      "            double lateralFanForceX = 0;",
      "            if (sample) { Profile.Add(5, phaseStart, 16); phaseStart = Stopwatch.GetTimestamp(); }\n            double lateralFanForceX = 0;",
    ],
    [
      "            // 7. Circle-box collision",
      "            if (sample) { Profile.Add(6, phaseStart, 16); phaseStart = Stopwatch.GetTimestamp(); }\n            // 7. Circle-box collision",
    ],
    [
      "        }\n\n        ProcessEvacuations();",
      "            if (sample) Profile.Add(7, phaseStart, 16);\n        }\n\n        wholeStart = Stopwatch.GetTimestamp();\n        ProcessEvacuations();\n        Profile.Add(8, wholeStart);",
    ],
  ];
  for (const [marker, replacement] of replacements)
    part = replaceOnce(part, marker, replacement);
  source = source.slice(0, start) + part + source.slice(end);
  writeFileSync(new URL("InstrumentedEngine.cs", output), source);
}
