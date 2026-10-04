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

Potential-field parity tests compare every cell against the C# engine:

```sh
npm run test:potential
```

To also run the production WGSL shaders and compare density, penalties, fields,
and flow directions in headless Chrome, set `PLANSAFE_PLAYWRIGHT_MODULE` to a
Playwright module URL and `PLANSAFE_BROWSER_PATH` to the Chrome executable before
running the same command. GPU comparisons allow float32 rounding differences;
the static TypeScript field comparison is exact.

GPU runtime tests verify compact telemetry, direct agent rendering, reset,
camera transforms, and map state/field parity with the same Chrome/Playwright
environment variables:

```sh
npm run test:gpu
```

Set `PLANSAFE_APP_URL` to a running app (for example `http://127.0.0.1:5000`) to
also check the map's engine indicator, playback, reset, mobile layout, and WASM
fallback through real Blazor interop.

The demo and map prefer WebGPU when available and respect a saved manual WASM
selection. The map displays its active engine in the simulation controls and
falls back to WASM if GPU initialization fails. Map preparation transfers the
same starting agents, terrain, and potential fields to the GPU once; subsequent
physics and compact telemetry run on the GPU.

Map routing is calculated once from terrain, obstacles, and exits and stays fixed
through playback and reset in both engines. Local density, crowd forces, and
collision avoidance still respond to moving agents. Preparation creates the
requested agents once and yields between terrain, field, and spawning batches
so the browser can render the preparation indicator and remain responsive.

Both views run only the selected physics engine. Both engines integrate in 16 ms
ticks; playback speed controls how many ticks run, and actual speed measures
simulation seconds per wall-clock second. Unlimited mode measures throughput.
The GPU agent view renders from the physics storage buffer and samples reduced
statistics at 10 Hz. Heatmap views retain the existing Canvas renderer and agent
readback. Demo congestion routing still solves potential fields to convergence.

For an opt-in performance regression on a hardware WebGPU adapter, set
`PLANSAFE_MAP_PERFORMANCE=1` along with the browser-test environment variables
above and run `node --test scripts/test-map-view.mjs`. This adds a 10,000-agent,
roughly 1 km city map at 20x playback, checks UI responsiveness and throughput,
and prints measured playback speed and render FPS. Run it without competing GPU
work; software adapters and low-end GPUs may not meet its throughput thresholds.

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
as in the browser tests above. Run one benchmark at a time. Generated snapshots,
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
