import { expect, test } from "@playwright/test";

for (const engine of ["wasm", "webgpu"] as const) {
  test(`map edits rebuild navigation and preserve playback: ${engine}`, async ({
    page,
  }) => {
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(error.message));
    await page.route("https://tile.openstreetmap.org/**", (route) =>
      route.fulfill({ status: 204 }),
    );
    await page.addInitScript((engine) => {
      localStorage.setItem("plansafe.map-tour.v1", "seen");
      localStorage.setItem("plansafe.language.v1", "en");
      localStorage.setItem("plansafe_simulation_engine", engine);
      localStorage.setItem(
        "plansafe_sessions_v1",
        JSON.stringify([
          {
            id: "live-update",
            branchName: "Live update",
            mapCenter: [50.07, 19.9],
            zoomLevel: 17,
            items: [
              {
                id: "spawn",
                type: "circle_zone",
                center: [50.07, 19.899],
                radius: 20,
              },
              { id: "exit", type: "safe_point", position: [50.07, 19.901] },
            ],
            simulationConfig: {
              agentCount: 37,
              useGusCensus: false,
              granulation: 3,
              timeScale: 1,
            },
            tags: {},
          },
        ]),
      );
      localStorage.setItem("plansafe_active_session_id", "live-update");
    }, engine);
    await page.goto("/map");
    await page
      .getByRole("button", { name: "Start simulation", exact: true })
      .click();
    await expect(page.getByTestId("map-simulation-engine")).toHaveAttribute(
      "data-engine",
      engine,
    );
    await expect(page.locator(".map-module-layout")).toHaveAttribute(
      "aria-busy",
      "false",
    );
    const time = async () =>
      parseFloat((await page.locator(".sim-time-text").textContent())!);
    await expect.poll(time).toBeGreaterThan(0.5);

    await page.evaluate(() => {
      const workspace = document.querySelector(".map-module-layout")!;
      (window as unknown as { editBusyStates: boolean[] }).editBusyStates = [];
      new MutationObserver(() => {
        (
          window as unknown as { editBusyStates: boolean[] }
        ).editBusyStates.push(workspace.getAttribute("aria-busy") === "true");
      }).observe(workspace, {
        attributes: true,
        attributeFilter: ["aria-busy"],
      });
    });
    const before = await time();
    await page.evaluate(async () => {
      const module = "/js/mapInterop.js";
      const { loadSessionItems } = (await import(
        module
      )) as typeof import("../../src/PlanSafe.App/wwwroot/js/mapInterop.js");
      const items = JSON.parse(localStorage.getItem("plansafe_sessions_v1")!)[0]
        .items;
      loadSessionItems(document.querySelector(".map-viewport")!.id, [
        ...items,
        {
          id: "new-safe",
          type: "safe_circle",
          center: [50.072, 19.897],
          radius: 15,
        },
        {
          id: "barrier",
          type: "blockade",
          startPoint: [50.0697, 19.9],
          endPoint: [50.0703, 19.9],
        },
      ]);
    });
    await expect
      .poll(() =>
        page.evaluate(() =>
          (
            window as unknown as { editBusyStates: boolean[] }
          ).editBusyStates.includes(true),
        ),
      )
      .toBe(true);
    await expect(page.locator(".map-module-layout")).toHaveAttribute(
      "aria-busy",
      "false",
    );
    await expect(
      page.getByRole("button", { name: "Pause", exact: true }),
    ).toBeEnabled();
    await expect.poll(time).toBeGreaterThan(before);
    await expect(page.locator(".sim-evac-counter")).toContainText("/ 37");
    await expect(page.locator(".sim-warning-toast")).toHaveCount(0);

    await page.getByRole("button", { name: "Pause", exact: true }).click();
    await expect(
      page.getByRole("button", { name: "Start", exact: true }),
    ).toBeEnabled();
    const pausedTime = await time();
    await page.evaluate(async () => {
      (window as unknown as { editBusyStates: boolean[] }).editBusyStates = [];
      const module = "/js/mapInterop.js";
      const { deleteMapItem } = (await import(
        module
      )) as typeof import("../../src/PlanSafe.App/wwwroot/js/mapInterop.js");
      deleteMapItem(document.querySelector(".map-viewport")!.id, "barrier");
    });
    await expect
      .poll(() =>
        page.evaluate(
          () =>
            (
              window as unknown as { editBusyStates: boolean[] }
            ).editBusyStates.filter(Boolean).length,
        ),
      )
      .toBeGreaterThan(0);
    await expect(page.locator(".map-module-layout")).toHaveAttribute(
      "aria-busy",
      "false",
    );
    await expect(
      page.getByRole("button", { name: "Start", exact: true }),
    ).toBeEnabled();
    await page.waitForTimeout(300);
    expect(await time()).toBe(pausedTime);
    await expect(page.locator(".sim-warning-toast")).toHaveCount(0);
    expect(errors).toEqual([]);
  });
}
