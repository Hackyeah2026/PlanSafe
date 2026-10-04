import { expect, test } from "@playwright/test";
import assert from "node:assert/strict";

test.beforeEach(async ({ context }) => {
  await context.addInitScript(() =>
    localStorage.setItem("plansafe.map-tour.v1", "seen"),
  );
});

declare global {
  interface Window {
    mapPerformance: { maxHeartbeatMs: number; draws: number };
    mapHeartbeat: number;
    mapStartup: {
      opaqueFills: number;
      revealedWithoutFrame: boolean;
      frames: number;
    };
    PlanSafeMap?: {
      getMap(id: string):
        | {
            setZoom(zoom: number): void;
            panBy(offset: number[], options: { animate: boolean }): void;
          }
        | undefined;
    };
  }
}

for (const mode of [
  "default",
  "wasm",
  "unsupported",
  "adapter-failure",
  "mobile",
  ...(process.env.PLANSAFE_MAP_PERFORMANCE === "1"
    ? (["performance"] as const)
    : []),
] as const) {
  test(`Map playback and engine fallback: ${mode}`, async ({
    page,
    context,
  }) => {
    const errors: string[] = [];
    await page.route("https://tile.openstreetmap.org/**", (route) =>
      route.fulfill({ status: 204 }),
    );
    if (mode === "mobile")
      await page.setViewportSize({ width: 390, height: 844 });
    await context.addInitScript((mode) => {
      localStorage.setItem(
        "plansafe_sessions_v1",
        JSON.stringify([
          {
            id: "gpu-map-test",
            branchName: "GPU map test",
            mapCenter: [50.07, 19.9],
            zoomLevel: 17,
            items: [
              {
                id: "spawn",
                type: "circle_zone",
                name: "Test crowd",
                center: [50.07, 19.899],
                radius: 20,
                color: "#f97316",
              },
              {
                id: "exit",
                type: "safe_point",
                name: "Test exit",
                position: [50.07, 19.901],
                color: "#10b981",
              },
            ],
            simulationConfig: {
              agentCount: 37,
              useGusCensus: false,
              granulation: 3,
              renderMode: "agents",
              renderFps: "30",
              socialRepulsionWeight: 4.5,
              timeScale: 1,
              whiskerLength: 2.5,
            },
            tags: {},
          },
        ]),
      );
      localStorage.setItem("plansafe_active_session_id", "gpu-map-test");
      if (mode === "performance") {
        const geo = (x: number, y: number) => [
          50.0614 - y / 111320,
          19.9366 + x / (111320 * Math.cos((50.0614 * Math.PI) / 180)),
        ];
        const [session]: {
          mapCenter: number[];
          zoomLevel: number;
          items: object[];
          simulationConfig: {
            agentCount: number;
            granulation: number;
            timeScale: number;
          };
        }[] = JSON.parse(localStorage.getItem("plansafe_sessions_v1")!);
        session.mapCenter = geo(0, 0);
        session.zoomLevel = 15;
        session.items = [
          {
            id: "spawn",
            type: "polygon_zone",
            name: "Crowd",
            color: "#f97316",
            coordinates: [
              geo(-450, -400),
              geo(-250, -400),
              geo(-250, 400),
              geo(-450, 400),
            ],
          },
          ...[-300, 300].map((y, i) => ({
            id: `exit-${i}`,
            type: "safe_point",
            name: `Exit ${i}`,
            position: geo(450, y),
            color: "#10b981",
          })),
        ];
        Object.assign(session.simulationConfig, {
          agentCount: 10000,
          granulation: 1,
          timeScale: 20,
        });
        localStorage.setItem("plansafe_sessions_v1", JSON.stringify([session]));
      }
      if (mode === "wasm")
        localStorage.setItem("plansafe_simulation_engine", "wasm");
      if (mode === "unsupported")
        Object.defineProperty(navigator, "gpu", { value: undefined });
      if (mode === "adapter-failure")
        Object.defineProperty(navigator, "gpu", {
          value: { requestAdapter: async () => null },
        });
    }, mode);
    page.on("pageerror", (error) => errors.push(`${mode}: ${error.message}`));
    page.on("console", (message) => {
      if (
        (message.type() === "error" && !message.text().includes("ERR_")) ||
        message.text().includes("uncaptured device error")
      )
        errors.push(`${mode}: ${message.text()}`);
    });
    await page.goto("/map");
    await page.waitForFunction(
      () =>
        window.PlanSafeMap?.getMap(
          document.querySelector(".map-viewport")?.id ?? "",
        ),
      null,
      { timeout: 60000 },
    );
    const start = page.getByRole("button", {
      name: "Start simulation",
      exact: true,
    });
    if (mode === "performance") {
      await page.evaluate(async () => {
        const module = "/js/crowdSimulatorGpu.js";
        const { GpuSimulationEngine } = (await import(
          module
        )) as typeof import("../../src/PlanSafe.App/TypeScript/crowdSimulatorGpu.js");
        window.mapPerformance = { maxHeartbeatMs: 0, draws: 0 };
        let previous = performance.now();
        window.mapHeartbeat = window.setInterval(() => {
          const now = performance.now();
          window.mapPerformance.maxHeartbeatMs = Math.max(
            window.mapPerformance.maxHeartbeatMs,
            now - previous,
          );
          previous = now;
        }, 50);
        const draw = GpuSimulationEngine.prototype.drawAgents;
        GpuSimulationEngine.prototype.drawAgents = function (
          this: InstanceType<typeof GpuSimulationEngine>,
          ...args: Parameters<typeof draw>
        ) {
          window.mapPerformance.draws++;
          return draw.apply(this, args);
        };
      });
    }
    if (!(await start.isVisible()))
      await page
        .getByRole("button", { name: "Expand zone information", exact: true })
        .click();
    await page.evaluate(() => {
      const canvas = document.querySelector<HTMLCanvasElement>(
        ".map-simulation-canvas",
      )!;
      window.mapStartup = {
        opaqueFills: 0,
        revealedWithoutFrame: false,
        frames: 0,
      };
      const context = CanvasRenderingContext2D.prototype as unknown as Record<
        "fillRect" | "fill" | "drawImage",
        (this: CanvasRenderingContext2D, ...args: unknown[]) => unknown
      >;
      for (const name of ["fillRect", "fill", "drawImage"] as const) {
        const original = context[name];
        context[name] = function (...args) {
          if (this.canvas === canvas) {
            if (name === "fillRect") window.mapStartup.opaqueFills++;
            else window.mapStartup.frames++;
          }
          return original.apply(this, args);
        };
      }
      new MutationObserver(() => {
        if (
          canvas.classList.contains("active") &&
          window.mapStartup.frames === 0
        )
          window.mapStartup.revealedWithoutFrame = true;
      }).observe(canvas, { attributes: true, attributeFilter: ["class"] });
    });
    const assertPreparedOverlay = async () => {
      const state = await page.evaluate(() => {
        const canvas = document.querySelector<HTMLCanvasElement>(
          ".map-simulation-canvas",
        )!;
        return {
          visibility: getComputedStyle(canvas).visibility,
          width: canvas.clientWidth,
          height: canvas.clientHeight,
        };
      });
      assert.equal(state.visibility, "hidden");
      assert.ok(state.width > 0 && state.height > 0);
    };
    const assertStartup = async () => {
      const state = await page.evaluate(() => window.mapStartup);
      assert.equal(state.opaqueFills, 0);
      assert.equal(state.revealedWithoutFrame, false);
      assert.ok(state.frames > 0);
    };
    // Stretch GPU initialization so an early overlay reveal cannot escape
    // the observer merely because this machine initializes it quickly.
    await page.evaluate(async () => {
      const module = "/js/crowdSimulatorGpu.js";
      const { GpuSimulationEngine } = (await import(
        module
      )) as typeof import("../../src/PlanSafe.App/TypeScript/crowdSimulatorGpu.js");
      const boot = GpuSimulationEngine.prototype.boot;
      GpuSimulationEngine.prototype.boot = async function (...args) {
        await new Promise((resolve) => setTimeout(resolve, 200));
        return boot.apply(this, args);
      };
    });
    await assertPreparedOverlay();
    await start.click();
    const badge = page.getByTestId("map-simulation-engine");
    const ready = () =>
      page.waitForFunction(
        () =>
          /\((CPU|GPU)\)/.test(
            document.querySelector('[data-testid="map-simulation-engine"]')
              ?.textContent ?? "",
          ),
        null,
        { timeout: 60000 },
      );
    await ready();
    await assertStartup();
    const expectedEngine =
      mode === "default" || mode === "mobile" || mode === "performance"
        ? "webgpu"
        : "wasm";
    assert.equal(await badge.getAttribute("data-engine"), expectedEngine);
    assert.ok(await badge.isVisible());
    if (mode === "performance") {
      const before = await page.evaluate(() => ({
        now: performance.now(),
        draws: window.mapPerformance.draws,
        time: parseFloat(
          document.querySelector(".sim-time-text")!.textContent!,
        ),
      }));
      await page.waitForTimeout(5000);
      const result = await page.evaluate((before) => {
        window.clearInterval(window.mapHeartbeat);
        const seconds = (performance.now() - before.now) / 1000;
        return {
          maxHeartbeatMs: window.mapPerformance.maxHeartbeatMs,
          actualSpeed:
            (parseFloat(
              document.querySelector(".sim-time-text")!.textContent!,
            ) -
              before.time) /
            seconds,
          fps: (window.mapPerformance.draws - before.draws) / seconds,
        };
      }, before);
      console.log("10,000-agent map, 20x:", result);
      assert.ok(
        result.maxHeartbeatMs < 1000,
        `UI blocked for ${result.maxHeartbeatMs} ms`,
      );
      assert.ok(
        result.actualSpeed > 15,
        `Playback reached only ${result.actualSpeed}x`,
      );
      assert.ok(result.fps > 15, `Rendering reached only ${result.fps} FPS`);
      return;
    }
    const collapse = page.getByRole("button", {
      name: "Collapse zone information",
      exact: true,
    });
    if (mode === "mobile" && (await collapse.isVisible()))
      await collapse.click();
    await page.waitForFunction(
      () =>
        parseFloat(
          document.querySelector(".sim-time-text")?.textContent ?? "0",
        ) > 0.2,
    );
    await page.getByRole("button", { name: "Pause", exact: true }).click();
    await expect(
      page.getByRole("button", { name: "Start", exact: true }),
    ).toBeVisible();
    const time = await page.locator(".sim-time-text").textContent();
    await page.waitForTimeout(200);
    assert.equal(await page.locator(".sim-time-text").textContent(), time);
    await page.getByRole("button", { name: "Step", exact: true }).click();
    await page.getByRole("button", { name: "Reset", exact: true }).click();
    await page.waitForFunction(
      () => document.querySelector(".sim-time-text")?.textContent === "0.0s",
    );
    assert.match(
      (await page.locator(".sim-evac-counter").textContent()) ?? "",
      /\/ 37/,
    );
    if (mode === "default" || mode === "wasm" || mode === "mobile") {
      const speed = page.getByTestId("map-time-scale");
      await speed.selectOption("max");
      await expect(speed).toHaveValue("max");
      await speed.selectOption("0.25");
      await expect(speed).toHaveValue("0.25");
      await page.getByRole("button", { name: "Start", exact: true }).click();
      await page.waitForTimeout(1600);
      await page.getByRole("button", { name: "Pause", exact: true }).click();
      const slowedTime = parseFloat(
        (await page.locator(".sim-time-text").textContent())!,
      );
      assert.ok(
        slowedTime >= 0.2 && slowedTime < 1,
        `Quarter-speed playback advanced ${slowedTime} simulated seconds`,
      );
      await speed.selectOption("1");
      await expect(speed).toHaveValue("1");
    }
    await page.getByRole("button", { name: "Density", exact: true }).click();
    await page.getByRole("button", { name: "Speed", exact: true }).click();
    await page.getByRole("button", { name: "Agents", exact: true }).click();
    await page.evaluate(() => {
      const map = window.PlanSafeMap!.getMap(
        document.querySelector(".map-viewport")!.id,
      );
      if (!map) throw new Error("Map is not initialized");
      map.setZoom(18);
      map.panBy([50, 30], { animate: false });
    });
    await page
      .getByRole("button", { name: "Close simulation", exact: true })
      .click();
    await badge.waitFor({ state: "detached" });
    if (!(await start.isVisible()))
      await page
        .getByRole("button", { name: "Expand zone information", exact: true })
        .click();
    await assertPreparedOverlay();
    await page.evaluate(() => {
      window.mapStartup = {
        opaqueFills: 0,
        revealedWithoutFrame: false,
        frames: 0,
      };
    });
    await start.click();
    await ready();
    await assertStartup();
    assert.equal(await badge.getAttribute("data-engine"), expectedEngine);

    assert.deepEqual(errors, []);
  });
}
