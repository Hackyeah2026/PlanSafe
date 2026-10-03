import test from "node:test";
import assert from "node:assert/strict";

test(
  "Map engine indicator, playback and fallback work through Blazor interop",
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
    const errors = [];
    try {
      for (const mode of [
        "default",
        "wasm",
        "unsupported",
        "adapter-failure",
        "mobile",
      ]) {
        const context = await browser.newContext({
          viewport:
            mode === "mobile"
              ? { width: 390, height: 844 }
              : { width: 1440, height: 1000 },
        });
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
          if (mode === "wasm")
            localStorage.setItem("plansafe_simulation_engine", "wasm");
          if (mode === "unsupported")
            Object.defineProperty(navigator, "gpu", { value: undefined });
          if (mode === "adapter-failure")
            Object.defineProperty(navigator, "gpu", {
              value: { requestAdapter: async () => null },
            });
        }, mode);
        const page = await context.newPage();
        page.on("pageerror", (error) =>
          errors.push(`${mode}: ${error.message}`),
        );
        page.on("console", (message) => {
          if (
            (message.type() === "error" && !message.text().includes("ERR_")) ||
            message.text().includes("uncaptured device error")
          )
            errors.push(`${mode}: ${message.text()}`);
        });
        await page.goto(new URL("/map", process.env.PLANSAFE_APP_URL).href);
        await page.waitForFunction(
          () =>
            window.PlanSafeMap?.getMap(
              document.querySelector(".map-viewport")?.id,
            ),
          null,
          { timeout: 60000 },
        );
        const start = page.getByRole("button", {
          name: "Start simulation",
          exact: true,
        });
        if (mode === "mobile" || !(await start.isVisible()))
          await page
            .getByRole("button", { name: "Open Control Panel", exact: true })
            .click();
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
        const expectedEngine =
          mode === "default" || mode === "mobile" ? "webgpu" : "wasm";
        assert.equal(await badge.getAttribute("data-engine"), expectedEngine);
        assert.ok(await badge.isVisible());
        // Close the mobile drawer before using the floating simulation controls.
        if (mode === "mobile")
          await page
            .getByRole("button", { name: "Close panel", exact: true })
            .click();
        await page.waitForFunction(
          () =>
            parseFloat(document.querySelector(".sim-time-text")?.textContent) >
            0.2,
        );
        await page.getByRole("button", { name: "Pauza", exact: true }).click();
        await page.waitForTimeout(150);
        const time = await page.locator(".sim-time-text").textContent();
        await page.waitForTimeout(200);
        assert.equal(await page.locator(".sim-time-text").textContent(), time);
        await page.getByRole("button", { name: "Krok", exact: true }).click();
        await page.getByRole("button", { name: "Reset", exact: true }).click();
        await page.waitForFunction(
          () =>
            document.querySelector(".sim-time-text")?.textContent === "0.0s",
        );
        assert.match(
          await page.locator(".sim-evac-counter").textContent(),
          /\/ 37/,
        );
        await page
          .getByRole("button", { name: "Gęstość", exact: true })
          .click();
        await page
          .getByRole("button", { name: "Prędkość", exact: true })
          .click();
        await page.getByRole("button", { name: "Agenci", exact: true }).click();
        await page.evaluate(() => {
          const map = window.PlanSafeMap.getMap(
            document.querySelector(".map-viewport").id,
          );
          map.setZoom(18);
          map.panBy([50, 30], { animate: false });
        });
        await page
          .getByRole("button", { name: "Zamknij symulację", exact: true })
          .click();
        await badge.waitFor({ state: "detached" });
        if (mode === "mobile")
          await page
            .getByRole("button", { name: "Open Control Panel", exact: true })
            .click();
        await start.click();
        await ready();
        assert.equal(await badge.getAttribute("data-engine"), expectedEngine);
        await context.close();
      }
      assert.deepEqual(errors, []);
    } finally {
      await browser.close();
    }
  },
);
