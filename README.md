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

GPU runtime tests verify compact telemetry, direct agent rendering, reset, and
camera transforms with the same Chrome/Playwright environment variables:

```sh
npm run test:gpu
```

The demo runs only the selected physics engine. Both engines integrate in 16 ms
ticks; playback speed controls how many ticks run, and actual speed measures
simulation seconds per wall-clock second. Unlimited mode measures throughput.
The GPU agent view renders from the physics storage buffer and samples reduced
statistics at 10 Hz. Heatmap views retain the existing Canvas renderer and agent
readback. Potential-field convergence remains exact rather than using a fixed
number of relaxation passes.
