# PlanSafe

## Run with devenv

Install [Nix](https://nixos.org/download/) and [devenv](https://devenv.sh/getting-started/).

From the repository root:

```sh
devenv shell
npm ci
devenv up
```

## Run without devenv

Install Node.js 24 with npm and the exact .NET SDK `10.0.201` pinned in `global.json`.

From the repository root:

```sh
dotnet workload install wasm-tools
npm ci
```

Start the app:

```sh
dotnet watch --project src/PlanSafe.App run --urls http://127.0.0.1:5000
```

In a separate terminal, from the repository root, start the API:

```sh
dotnet watch --project src/PlanSafe.Api run --urls http://127.0.0.1:5001
```

For either setup, open <http://127.0.0.1:5000>. The API runs at <http://127.0.0.1:5001>.

## Build and format

From the repository root (inside `devenv shell` if using devenv):

```sh
dotnet build PlanSafe.slnx
npm run format
npm run format:check
```

`npm ci` installs a commit hook that checks staged formatting, TypeScript, and
the solution build with warnings treated as errors. Enable it in an existing
checkout with `npm run hooks:install`.

Run the checks:

```sh
bash infra/ci/check.sh
bash infra/ci/check-api.sh
npx playwright install --with-deps chromium
bash infra/ci/check-browser.sh
```

## Simulation performance investigation

Run the native .NET physics benchmark, which also exports snapshots for the GPU
benchmark, then measure the production WebGPU shaders:

```sh
npm run benchmark:cpu
npm run benchmark:gpu
npm run benchmark:gpu -- --variant=fine-grid
npm run benchmark:gpu -- --variant=contact-cull
```

GPU runs need Playwright (installed separately, or selected with
`PLANSAFE_PLAYWRIGHT_MODULE`) and Chrome selected with `PLANSAFE_BROWSER_PATH`,
using the same environment variables as the standalone browser tests. Run one benchmark at a time. Generated snapshots,
instrumented engine copies, and fresh results go in ignored `.run/simulation-perf/`.
Instrumentation validates its source markers and fails when the engine changes.
The variants modify only generated benchmark engine copies or served JavaScript.

The [recorded results](scripts/performance/results) from 4 October 2026 used a
1,500 × 1,000 m map with a 600 × 400 potential grid, seed 42, and 16 ms ticks.
Native runs warmed up for 30 ticks and measured 80; GPU runs warmed up for 32
ticks and measured three batches of 32, awaiting queue completion. GPU phase
times use timestamp queries on an NVIDIA Lovelace hardware adapter. Native .NET
timings do not represent browser WASM performance. CPU agent phase times sample
every sixteenth agent and extrapolate; instrumentation overhead and sampling
bias mean those phase estimates are approximate. GPU rendering times measure
the interop call; direct agent rendering submits work asynchronously. The
separate first telemetry/preview/render samples can include cold setup costs.
These short synthetic runs guide investigation, rather than establish a
performance guarantee. They used `dc56f05` with existing local map/camera edits.

The strongest optimization candidates are:

| Area                           | Evidence and next step                                                                                                                                                                                                                                                                                                                                                                                                   |
| ------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Neighbor lookup                | Dense 10,000-agent physics took about 32 ms/tick on native CPU and 0.97 ms/tick on GPU, while sparse cases were much faster. A finer GPU grid reduced the dense case to about 0.38 ms/tick (2.6×), but increased fixed-capacity index storage from about 21 MB to 98 MB. Use compact cell storage, preserve the full interaction radius at every granulation, and validate parity and overflow behavior before shipping. |
| Heatmap rendering              | Warm density and speed heatmaps took about 8–10 ms/frame in interop, with full agent readback and Canvas aggregation. Keep aggregation and color rendering on GPU to reduce transfer and CPU work.                                                                                                                                                                                                                       |
| CPU density smoothing          | At granulation 16, updating and smoothing the full density grid consumed roughly 80% of the instrumented tick. Investigate occupied tiles with a smoothing halo and fused decode/smoothing. A buffer-swap experiment preserved checked final positions/density but showed no reliable speed gain.                                                                                                                        |
| Wall contacts                  | Per-agent wall scans and wall-run discovery repeat each tick. Precompute wall segments and a spatial index, preserving corner behavior.                                                                                                                                                                                                                                                                                  |
| Telemetry and evacuated agents | Readback awaits can delay scheduling the next GPU batch; use bounded batches and buffered asynchronous telemetry. GPU dispatch continues to cover original agent slots after evacuation; compact active agents when enough slots become inactive.                                                                                                                                                                        |

Map potential fields are already computed once. Native physics allocated zero
bytes per measured tick. The contact-distance culling experiment showed no
reliable improvement. Production simulation logic is unchanged by these
benchmarks, and the finer-grid prototype has not passed behavioral parity tests.
