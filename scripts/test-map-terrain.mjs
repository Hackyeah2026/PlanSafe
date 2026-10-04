import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";

test(
  "Map terrain is served intact, blocks buildings and rejects failed loading",
  {
    skip:
      !process.env.PLANSAFE_PLAYWRIGHT_MODULE || !process.env.PLANSAFE_APP_URL,
  },
  async () => {
    const { chromium } = await import(process.env.PLANSAFE_PLAYWRIGHT_MODULE);
    const browser = await chromium.launch({
      headless: true,
      executablePath: process.env.PLANSAFE_BROWSER_PATH,
      args: [
        "--enable-unsafe-webgpu",
        "--disable-dawn-features=disallow_unsafe_apis",
      ],
    });
    try {
      for (const mode of ["terrain", "empty-fallback", "malformed-fallback"]) {
        const context = await browser.newContext();
        try {
          await context.addInitScript(() => {
            localStorage.setItem("plansafe_simulation_engine", "webgpu");
            localStorage.setItem(
              "plansafe_sessions_v1",
              JSON.stringify([
                {
                  id: "terrain-test",
                  branchName: "Terrain test",
                  mapCenter: [50.0643, 19.9306],
                  zoomLevel: 17,
                  items: [
                    {
                      id: "spawn",
                      type: "circle_zone",
                      name: "Crowd",
                      center: [50.0643, 19.9296],
                      radius: 30,
                      color: "#f97316",
                    },
                    {
                      id: "exit",
                      type: "safe_point",
                      name: "Exit",
                      position: [50.0643, 19.9316],
                      color: "#10b981",
                    },
                  ],
                  simulationConfig: {
                    agentCount: 37,
                    useGusCensus: false,
                    granulation: 1,
                    renderMode: "agents",
                    renderFps: "30",
                    timeScale: 1,
                  },
                  tags: {},
                },
              ]),
            );
            localStorage.setItem("plansafe_active_session_id", "terrain-test");
          });
          const page = await context.newPage();
          if (mode !== "terrain") {
            await page.route("**/data/krakow_osm.bin", (route) =>
              route.fulfill({ status: 200, body: "" }),
            );
            await page.route("**/data/buildings_krakow.json", (route) =>
              route.fulfill({
                contentType: "application/json",
                body: mode === "empty-fallback" ? "[]" : "{invalid json",
              }),
            );
          }
          await page.goto(new URL("/map", process.env.PLANSAFE_APP_URL).href);
          await page.waitForFunction(
            () =>
              window.PlanSafeMap?.getMap(
                document.querySelector(".map-viewport")?.id,
              ),
            null,
            { timeout: 60000 },
          );
          if (mode === "terrain") {
            const response = await page.request.get(
              new URL("/data/krakow_osm.bin", process.env.PLANSAFE_APP_URL)
                .href,
            );
            assert.equal(response.status(), 200);
            const actual = await response.body();
            const expected = readFileSync(
              new URL("../data/osm/krakow_osm.bin", import.meta.url),
            );
            assert.equal(actual.subarray(0, 7).toString(), "KOSMV3\0");
            assert.equal(actual.length, expected.length);
            const hash = (bytes) =>
              createHash("sha256").update(bytes).digest("hex");
            assert.equal(hash(actual), hash(expected));
            await page.evaluate(async () => {
              const { GpuSimulationEngine } =
                await import("/js/crowdSimulatorGpu.js");
              const initialize = GpuSimulationEngine.prototype.initializeMap;
              GpuSimulationEngine.prototype.initializeMap = function (
                snapshot,
              ) {
                initialize.call(this, snapshot);
                window.terrainBlockedCells = this.mapScenario.blocked.reduce(
                  (sum, value) => sum + value,
                  0,
                );
              };
            });
          }
          const start = page.getByRole("button", {
            name: "Start simulation",
            exact: true,
          });
          await start.click();
          if (mode === "terrain") {
            await page.waitForFunction(
              () => window.terrainBlockedCells > 0,
              null,
              { timeout: 60000 },
            );
            await page.waitForFunction(
              () =>
                document
                  .querySelector(".map-simulation-canvas")
                  ?.classList.contains("active"),
              null,
              { timeout: 60000 },
            );
            assert.equal(
              await page
                .getByTestId("map-simulation-engine")
                .getAttribute("data-engine"),
              "webgpu",
            );
          } else {
            await page.waitForFunction(
              () =>
                document
                  .querySelector(".sim-warning-toast")
                  ?.textContent.includes("Map terrain could not be loaded"),
              null,
              { timeout: 60000 },
            );
            assert.equal(
              await page.locator(".map-simulation-canvas.active").count(),
              0,
            );
            assert.ok(await start.isEnabled());
          }
        } finally {
          await context.close();
        }
      }
    } finally {
      await browser.close();
    }
  },
);
