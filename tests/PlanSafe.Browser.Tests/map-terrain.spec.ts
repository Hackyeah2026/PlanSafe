import { test, expect } from "@playwright/test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import type { GpuTestEngine } from "./gpu-types.js";

declare global {
  interface Window {
    terrainBlockedCells?: number;
  }
}

for (const mode of [
  "terrain",
  "empty-fallback",
  "malformed-fallback",
] as const) {
  test(`Map terrain loading: ${mode}`, async ({ page, context }) => {
    await page.route("https://tile.openstreetmap.org/**", (route) =>
      route.fulfill({ status: 204 }),
    );
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
    await page.goto("/map");
    await page.waitForFunction(
      () =>
        window.PlanSafeMap?.getMap(
          document.querySelector(".map-viewport")?.id ?? "",
        ),
      null,
      { timeout: 60000 },
    );
    if (mode === "terrain") {
      const response = await page.request.get("/data/krakow_osm.bin");
      assert.equal(response.status(), 200);
      const actual = await response.body();
      const expected = readFileSync(
        new URL("../../data/osm/krakow_osm.bin", import.meta.url),
      );
      assert.equal(actual.subarray(0, 7).toString(), "KOSMV3\0");
      assert.equal(actual.length, expected.length);
      const hash = (bytes: Uint8Array) =>
        createHash("sha256").update(bytes).digest("hex");
      assert.equal(hash(actual), hash(expected));
      await page.evaluate(async () => {
        const module = "/js/crowdSimulatorGpu.js";
        const { GpuSimulationEngine } = (await import(
          module
        )) as typeof import("../../src/PlanSafe.App/TypeScript/crowdSimulatorGpu.js");
        const initialize = GpuSimulationEngine.prototype.initializeMap;
        GpuSimulationEngine.prototype.initializeMap = function (snapshot) {
          initialize.call(this, snapshot);
          window.terrainBlockedCells = (
            this as unknown as GpuTestEngine
          ).mapScenario.blocked.reduce((sum, value) => sum + value, 0);
        };
      });
    }
    const start = page.getByRole("button", {
      name: "Start simulation",
      exact: true,
    });
    if (!(await start.isVisible()))
      await page
        .getByRole("button", { name: "Expand zone information", exact: true })
        .click();
    await start.click();
    if (mode === "terrain") {
      await page.waitForFunction(
        () => (window.terrainBlockedCells ?? 0) > 0,
        null,
        { timeout: 60000 },
      );
      await expect(page.locator(".map-simulation-canvas")).toHaveClass(
        /active/,
        { timeout: 60000 },
      );
      await expect(page.getByTestId("map-simulation-engine")).toHaveAttribute(
        "data-engine",
        "webgpu",
      );
    } else {
      await expect(page.locator(".sim-warning-toast")).toContainText(
        "Map terrain could not be loaded",
        { timeout: 60000 },
      );
      await expect(page.locator(".map-simulation-canvas.active")).toHaveCount(
        0,
      );
      await expect(start).toBeEnabled();
    }
  });
}
