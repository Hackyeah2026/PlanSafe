import { expect, test } from "@playwright/test";

test("safe zones have no occupancy limit or manual capacity editor", async ({
  page,
  context,
}) => {
  await page.route("https://tile.openstreetmap.org/**", (route) =>
    route.fulfill({ status: 204 }),
  );
  const published: {
    targets: { capacity: number; currentOccupancy: number }[];
  }[] = [];
  await page.route("**/api/evacuate/publish-plan", async (route) => {
    published.push(route.request().postDataJSON());
    await route.fulfill({ status: 503, body: "Intercepted by capacity test" });
  });
  await context.addInitScript(() => {
    localStorage.setItem(
      "plansafe_sessions_v1",
      JSON.stringify([
        {
          id: "capacity-defaults",
          branchName: "Capacity defaults",
          mapCenter: [50.07, 19.9],
          zoomLevel: 16,
          items: [0, 1, 2].map((i) => ({
            id: `safe-${i}`,
            type: "safe_point",
            name: `Safe zone ${i}`,
            position: [50.07 + i * 0.001, 19.9],
            capacity: 1,
            color: "#10b981",
          })),
          simulationConfig: { agentCount: 1001, useGusCensus: false },
          tags: {},
        },
      ]),
    );
    localStorage.setItem("plansafe_active_session_id", "capacity-defaults");
  });
  await page.goto("/map");
  await expect(page.locator(".custom-safe-point-marker")).toHaveCount(3);
  await page.locator(".custom-safe-point-marker").first().click();
  await expect(page.locator(".leaflet-popup-content")).toContainText(
    "Safe zone 0",
  );
  await expect(page.locator(".leaflet-popup-content input")).toHaveCount(0);
  await page.locator(".leaflet-popup-close-button").click();
  const publish = async (capacity: number) => {
    await page
      .getByRole("button", { name: "Start evacuation", exact: true })
      .click();
    const count = published.length;
    await page
      .getByRole("button", {
        name: "Publish plan",
        exact: true,
      })
      .click();
    await expect.poll(() => published.length).toBe(count + 1);
    expect(published[count].targets).toHaveLength(3);
    expect(published[count].targets.map((target) => target.capacity)).toEqual([
      capacity,
      capacity,
      capacity,
    ]);
    expect(
      published[count].targets.map((target) => target.currentOccupancy),
    ).toEqual([0, 0, 0]);
    await page
      .getByRole("dialog")
      .getByRole("button", { name: "Close", exact: true })
      .first()
      .click();
  };
  await publish(0);
  await page
    .getByRole("button", { name: "Simulation settings", exact: true })
    .click();
  await page
    .getByRole("slider", {
      name: /Number of agents|Liczba agentów/,
      exact: true,
    })
    .evaluate((input: HTMLInputElement) => {
      input.value = "1500";
      input.dispatchEvent(new Event("change", { bubbles: true }));
    });
  await page.getByRole("button", { name: /Save|Zapisz/, exact: true }).click();
  await publish(0);
});
