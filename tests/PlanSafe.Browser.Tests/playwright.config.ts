import { defineConfig } from "@playwright/test";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../../", import.meta.url));
const output = `${root}artifacts/browser`;

export default defineConfig({
  testDir: ".",
  testMatch: "*.spec.ts",
  forbidOnly: !!process.env.CI,
  // Leave CPU headroom for software WebGPU and the app/API servers in CI.
  workers: process.env.CI ? 2 : 1,
  retries: 0,
  timeout: 90_000,
  expect: { timeout: 10_000 },
  outputDir: `${output}/results`,
  reporter: [
    ["list"],
    ["html", { outputFolder: `${output}/report`, open: "never" }],
    ["junit", { outputFile: `${output}/junit.xml` }],
  ],
  use: {
    baseURL: "http://127.0.0.1:5171",
    browserName: "chromium",
    channel: "chromium",
    viewport: { width: 1440, height: 1000 },
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    launchOptions: {
      executablePath: process.env.PLANSAFE_BROWSER_PATH,
      // CI uses software WebGPU; the opt-in performance run uses hardware.
      args: [
        "--enable-unsafe-webgpu",
        "--enable-features=Vulkan",
        ...(process.env.PLANSAFE_MAP_PERFORMANCE === "1"
          ? []
          : [
              "--use-angle=swiftshader",
              "--enable-unsafe-swiftshader",
              "--use-vulkan=swiftshader",
            ]),
      ],
    },
  },
  webServer: [
    {
      command:
        "dotnet run --project src/PlanSafe.App --no-build --no-restore --no-launch-profile --urls http://127.0.0.1:5171",
      cwd: root,
      url: "http://127.0.0.1:5171",
      reuseExistingServer: false,
      timeout: 120_000,
    },
    {
      command:
        "dotnet run --project src/PlanSafe.Api --no-build --no-restore --no-launch-profile --urls http://127.0.0.1:49492",
      cwd: root,
      url: "http://127.0.0.1:49492/api/health",
      reuseExistingServer: false,
      timeout: 120_000,
    },
  ],
});
