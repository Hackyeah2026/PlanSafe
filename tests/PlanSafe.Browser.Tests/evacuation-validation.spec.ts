import { expect, test } from "@playwright/test";

test.beforeEach(async ({ context }) => {
  await context.addInitScript(() =>
    localStorage.setItem("plansafe.map-tour.v1", "seen"),
  );
});

for (const language of ["en", "pl"] as const) {
  for (const hasSafeLocation of [false, true]) {
    test(`evacuation requires a zone: ${language}, ${hasSafeLocation ? "safe location only" : "empty map"}`, async ({
      page,
      context,
    }) => {
      await page.route("https://tile.openstreetmap.org/**", (route) =>
        route.fulfill({ status: 204 }),
      );
      let publishRequests = 0;
      await page.route("**/api/evacuate/publish-plan", (route) => {
        publishRequests++;
        return route.fulfill({ status: 503 });
      });
      await context.addInitScript(
        ({ language, hasSafeLocation }) => {
          localStorage.setItem("plansafe.language.v1", language);
          localStorage.setItem(
            "plansafe_sessions_v1",
            JSON.stringify([
              {
                id: "evacuation-validation",
                branchName: "Evacuation validation",
                mapCenter: [50.07, 19.9],
                zoomLevel: 16,
                items: hasSafeLocation
                  ? [
                      {
                        id: "safe",
                        type: "safe_point",
                        position: [50.07, 19.9],
                      },
                    ]
                  : [],
                simulationConfig: { agentCount: 100, useGusCensus: false },
                tags: {},
              },
            ]),
          );
          localStorage.setItem(
            "plansafe_active_session_id",
            "evacuation-validation",
          );
        },
        { language, hasSafeLocation },
      );

      await page.goto("/map");
      await page
        .getByRole("button", {
          name: language === "pl" ? "Rozpocznij ewakuację" : "Start evacuation",
          exact: true,
        })
        .click();
      await expect(page.locator(".sim-warning-toast")).toContainText(
        language === "pl"
          ? "Przed uruchomieniem ewakuacji narysuj co najmniej jedną strefę ewakuacji."
          : "Draw at least one evacuation zone before starting an evacuation.",
      );
      await expect(page.getByRole("dialog")).toHaveCount(0);
      expect(publishRequests).toBe(0);
    });
  }
}
