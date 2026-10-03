{ pkgs, ... }:
{
  languages.dotnet = {
    enable = true;
    package = pkgs.dotnet-sdk_10.withWorkloads [ "wasm-tools" ];
  };

  packages = with pkgs; [
    nodejs_24
    clang
    binutils
    zlib
    openssl
  ];

  env = {
    DOTNET_CLI_TELEMETRY_OPTOUT = "1";
    DOTNET_NOLOGO = "1";
  };

  processes.app.exec = "dotnet watch --project src/PlanSafe.App run --urls http://127.0.0.1:5000";
  processes.api.exec = "dotnet watch --project src/PlanSafe.Api run --urls http://127.0.0.1:5001";

  scripts.format.exec = "npm run format";
  scripts.format-check.exec = "npm run format:check";
}
