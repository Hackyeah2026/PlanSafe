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
