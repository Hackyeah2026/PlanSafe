import { expect, test, type Page } from "@playwright/test";

test.beforeEach(async ({ context }) => {
  await context.addInitScript(() =>
    localStorage.setItem("plansafe.map-tour.v1", "seen"),
  );
});

const shelter = {
  id: "shelter-test",
  name: "Punkt zbiórki przy Parku Krakowskim",
  x: 19.932,
  y: 50.066,
  latitude: 50.066,
  longitude: 19.932,
  width: 20,
  height: 20,
  capacity: 100,
  currentOccupancy: 35,
  isActive: true,
};
const zone = [
  { latitude: 50.058, longitude: 19.926 },
  { latitude: 50.058, longitude: 19.946 },
  { latitude: 50.072, longitude: 19.946 },
  { latitude: 50.072, longitude: 19.926 },
];

async function prepare(
  page: Page,
  options: {
    gps?:
      | "inside"
      | "outside"
      | "denied"
      | "timeout"
      | "unavailable"
      | "unsupported";
    expired?: boolean;
    noShelter?: boolean;
    configFailure?: boolean;
    nullConfig?: boolean;
    longName?: boolean;
    noZones?: boolean;
    capacity?: number;
  } = {},
) {
  const requests: { path: string; body: Record<string, unknown> | null }[] = [];
  const target = {
    ...shelter,
    capacity: options.capacity ?? shelter.capacity,
    name: options.longName
      ? "Punkt zbiórki przy Szkole Podstawowej imienia Marii Skłodowskiej-Curie - wejście od ulicy Długiej"
      : shelter.name,
  };
  await page.addInitScript((gps) => {
    Object.defineProperty(navigator, "geolocation", {
      value:
        gps === "unsupported"
          ? undefined
          : {
              getCurrentPosition(
                success: PositionCallback,
                failure: PositionErrorCallback,
              ) {
                if (
                  gps === "denied" ||
                  gps === "timeout" ||
                  gps === "unavailable"
                ) {
                  failure({
                    code: gps === "denied" ? 1 : gps === "timeout" ? 3 : 2,
                    message: "Location failed",
                  } as GeolocationPositionError);
                } else {
                  success({
                    coords: {
                      latitude: gps === "outside" ? 51 : 50.063,
                      longitude: 19.936,
                      accuracy: 10,
                    },
                  } as GeolocationPosition);
                }
              },
            },
    });
  }, options.gps ?? "inside");
  await page.route("https://tile.openstreetmap.org/**", (route) =>
    route.fulfill({ status: 204 }),
  );
  await page.route("**/api/evacuate/**", async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    requests.push({
      path,
      body: request.postDataJSON() as Record<string, unknown> | null,
    });
    if (path.endsWith("/config")) {
      await route.fulfill({
        status: options.configFailure ? 503 : 200,
        json: options.nullConfig
          ? null
          : {
              sessionId: "evacuation-browser-test",
              targets: [target],
              obstacles: [],
              roadblocks: [],
              evacuationZones: options.noZones ? [] : [zone],
              safeZones: [],
              expiresAtUtc: options.expired
                ? "2020-01-01T00:00:00Z"
                : "2099-01-01T00:00:00Z",
            },
      });
    } else if (path.endsWith("/assign")) {
      await route.fulfill({
        json: {
          target: options.noShelter ? null : target,
          distance: 420,
          bearingDegrees: 315,
          isOutsideZone: false,
        },
      });
    } else if (path.endsWith("/targets")) {
      await route.fulfill({ json: [target] });
    } else {
      await route.fulfill({ json: { success: true } });
    }
  });
  return requests;
}

async function expectNoOverflow(page: Page) {
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
}

async function selectMapLocation(page: Page) {
  await page
    .getByRole("button", { name: "Select on map", exact: true })
    .click();
  await expect(page.locator(".evac-panel .command-toggle")).toHaveAttribute(
    "aria-expanded",
    "false",
  );
  const zone = page
    .locator('.leaflet-overlay-pane path[stroke="#f97316"]')
    .first();
  await zone.click();
  await expect(page.getByTestId("target-name")).toBeVisible();
}

for (const viewport of [
  { width: 1440, height: 900 },
  { width: 1024, height: 768 },
  { width: 400, height: 841 },
  { width: 360, height: 640 },
  { width: 320, height: 568 },
  { width: 667, height: 375 },
]) {
  test(`shelter and shared panel remain usable at ${viewport.width}x${viewport.height}`, async ({
    page,
  }) => {
    await page.setViewportSize(viewport);
    await prepare(page);
    const errors: string[] = [];
    page.on("pageerror", (e) => errors.push(e.message));
    await page.goto("/evacuate");
    await expect(page.getByTestId("target-name")).toHaveText(shelter.name);
    const navigation = page.getByRole("link", {
      name: "Navigate in Google Maps",
    });
    const appearance = page.locator(".evac-appearance summary");
    await expect(appearance).toHaveAccessibleName("Appearance");
    await expect(appearance.locator("svg")).toBeVisible();
    await expect(appearance).toHaveText("");
    const legendToggle = page.locator(".map-legend summary");
    await legendToggle.click();
    await expect(page.locator(".legend-content")).toBeInViewport({ ratio: 1 });
    const legendBounds = await page.locator(".legend-content").boundingBox();
    expect(legendBounds!.x).toBeGreaterThanOrEqual(0);
    expect(legendBounds!.x + legendBounds!.width).toBeLessThanOrEqual(
      viewport.width,
    );
    await legendToggle.click();
    await expect(navigation).toBeInViewport({ ratio: 1 });
    await expect(navigation).toHaveAttribute("href", /origin=50.063/);
    const marker = page.locator(".custom-shelter-marker").first();
    await expect
      .poll(() =>
        marker.evaluate((element) => {
          const r = element.getBoundingClientRect();
          return element.contains(
            document.elementFromPoint(r.x + 17, r.y + 17),
          );
        }),
      )
      .toBe(true);
    await expect(page.getByTestId("target-occupancy")).toBeHidden();
    if (viewport.width < 768) {
      const toggle = page.locator(".evac-panel .command-toggle");
      await expect(toggle).toHaveAttribute("aria-expanded", "true");
      await toggle.focus();
      await page.keyboard.press("Enter");
      await expect(navigation).toBeHidden();
      await expect(toggle).toHaveAttribute("aria-expanded", "false");
      await page.keyboard.press("Enter");
      await expect(navigation).toBeVisible();
    }
    await expectNoOverflow(page);
    await page.screenshot({
      path: `artifacts/browser/evacuation-panel-${viewport.width}.png`,
    });
    expect(errors).toEqual([]);
  });
}

for (const gps of [
  "denied",
  "timeout",
  "unavailable",
  "unsupported",
] as const) {
  test(`GPS ${gps} offers manual selection without a provisional route`, async ({
    page,
  }) => {
    await page.setViewportSize({ width: 400, height: 841 });
    const requests = await prepare(page, { gps });
    await page.goto("/evacuate");
    await expect(page.getByTestId("location-message")).toContainText(
      gps === "denied"
        ? "blocked"
        : gps === "timeout"
          ? "too long"
          : gps === "unavailable"
            ? "could not find"
            : "not supported",
    );
    await expect(
      page.locator(".navigation-link, .citizen-position"),
    ).toHaveCount(0);
    expect(
      requests.some(
        (r) => r.path.endsWith("/assign") || r.path.endsWith("/checkin"),
      ),
    ).toBe(false);
    await page.screenshot({
      path: `artifacts/browser/evacuation-gps-${gps}.png`,
    });
    await selectMapLocation(page);
    await expect(
      page.getByText("Location selected on map", { exact: true }),
    ).toBeVisible();
    await expect(page.locator(".evac-panel .command-toggle")).toHaveAttribute(
      "aria-expanded",
      "true",
    );
    await expect(page.locator(".navigation-link")).toBeVisible();
  });
}

test("GPS retry confirms the actual location", async ({ page }) => {
  await prepare(page, { gps: "timeout" });
  await page.goto("/evacuate");
  await expect(page.getByTestId("location-message")).toContainText("too long");
  await page.evaluate(() => {
    navigator.geolocation.getCurrentPosition = (success) =>
      success({
        coords: { latitude: 50.063, longitude: 19.936, accuracy: 10 },
      } as GeolocationPosition);
  });
  await page.getByRole("button", { name: "Retry GPS", exact: true }).click();
  await expect(page.getByTestId("target-name")).toBeVisible();
  await expect(
    page.getByText("Located using GPS", { exact: true }),
  ).toBeVisible();
});

test("outside GPS cannot navigate from the zone center", async ({ page }) => {
  await page.setViewportSize({ width: 400, height: 841 });
  const requests = await prepare(page, { gps: "outside" });
  await page.goto("/evacuate");
  await expect(page.getByTestId("location-message")).toContainText("outside");
  await expect(page.locator(".navigation-link")).toHaveCount(0);
  expect(requests.some((r) => r.path.endsWith("/assign"))).toBe(false);
  await selectMapLocation(page);
  await expect(page.locator(".navigation-link")).toBeVisible();
});

test("changing a confirmed location offers GPS or map and can be cancelled", async ({
  page,
}) => {
  await page.setViewportSize({ width: 400, height: 841 });
  await prepare(page);
  await page.goto("/evacuate");
  await expect(page.getByTestId("target-name")).toBeVisible();
  await page
    .getByRole("button", { name: "Change location", exact: true })
    .click();
  await expect(
    page.getByRole("button", { name: "Use GPS", exact: true }),
  ).toBeVisible();
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(page.getByTestId("target-name")).toBeVisible();
  await page
    .getByRole("button", { name: "Change location", exact: true })
    .click();
  await selectMapLocation(page);
  await expect(
    page.getByText("Location selected on map", { exact: true }),
  ).toBeVisible();
});

test("manual location wins over an outstanding GPS response", async ({
  page,
}) => {
  await page.setViewportSize({ width: 400, height: 841 });
  await prepare(page);
  await page.addInitScript(() => {
    navigator.geolocation.getCurrentPosition = (success) => {
      (window as any).finishGps = () =>
        success({
          coords: { latitude: 50.063, longitude: 19.936, accuracy: 10 },
        } as GeolocationPosition);
    };
  });
  await page.goto("/evacuate");
  await expect(page.getByTestId("location-message")).toContainText(
    "Finding your location",
  );
  await selectMapLocation(page);
  const route = await page.locator(".navigation-link").getAttribute("href");
  await page.evaluate(() => (window as any).finishGps());
  await expect(
    page.getByText("Location selected on map", { exact: true }),
  ).toBeVisible();
  await expect(page.locator(".navigation-link")).toHaveAttribute(
    "href",
    route!,
  );
});

test("only the Fit map button recenters after initial load", async ({
  page,
}) => {
  await page.setViewportSize({ width: 400, height: 841 });
  await prepare(page, { gps: "denied" });
  await page.goto("/evacuate");
  await expect(page.getByTestId("location-message")).toContainText("blocked");
  const zone = page
    .locator('.leaflet-overlay-pane path[stroke="#f97316"]')
    .first();
  await expect(zone).toBeVisible();
  await page.evaluate(() => {
    const state = window as any;
    state.fitCalls = 0;
    const original = state.L.Map.prototype.fitBounds;
    state.L.Map.prototype.fitBounds = function (...args: unknown[]) {
      state.fitCalls++;
      return original.apply(this, args);
    };
  });
  const initialBounds = await zone.boundingBox();
  await selectMapLocation(page);
  const selectedBounds = await zone.boundingBox();
  expect(selectedBounds!.x).toBeCloseTo(initialBounds!.x, 0);
  expect(selectedBounds!.y).toBeCloseTo(initialBounds!.y, 0);
  await page.locator(".evac-panel .command-toggle").click();
  await page.locator(".evac-panel .command-toggle").click();
  await page
    .getByRole("button", { name: "Change location", exact: true })
    .click();
  await page.evaluate(() => {
    navigator.geolocation.getCurrentPosition = (success) =>
      success({
        coords: { latitude: 50.063, longitude: 19.936, accuracy: 10 },
      } as GeolocationPosition);
  });
  await page.getByRole("button", { name: "Use GPS", exact: true }).click();
  await expect(
    page.getByText("Located using GPS", { exact: true }),
  ).toBeVisible();
  await page.setViewportSize({ width: 430, height: 900 });
  expect(await page.evaluate(() => (window as any).fitCalls)).toBe(0);
  await page.getByRole("button", { name: "Fit map", exact: true }).click();
  expect(await page.evaluate(() => (window as any).fitCalls)).toBe(1);
});

for (const state of ["failure", "mismatch", "null", "expired"] as const) {
  test(`invalid plan stays blocked: ${state}`, async ({ page }) => {
    const requests = await prepare(page, {
      configFailure: state === "failure",
      nullConfig: state === "null",
      expired: state === "expired",
    });
    await page.goto(
      state === "mismatch" ? "/evacuate?session=obsolete" : "/evacuate",
    );
    await expect(page.getByTestId("plan-load-error")).toBeVisible();
    await expect(
      page.locator(".evac-map[inert] .leaflet-container"),
    ).toBeVisible();
    await expect(
      page.locator(".navigation-link, .leaflet-control-zoom"),
    ).toHaveCount(0);
    expect(requests.every((r) => r.path.endsWith("/config"))).toBe(true);
  });
}

test("unavailable shelter provides retry", async ({ page }) => {
  await prepare(page, { noShelter: true });
  await page.goto("/evacuate");
  await expect(
    page.getByRole("heading", { name: "No available shelter" }),
  ).toBeVisible();
  await expect(page.getByRole("button", { name: "Try again" })).toBeVisible();
});

test("language switching preserves location and assignment", async ({
  page,
}) => {
  await prepare(page);
  await page.goto("/evacuate");
  await expect(page.getByTestId("target-name")).toBeVisible();
  const map = await page.locator("#citizen-leaflet-map").elementHandle();
  await page.locator(".evac-appearance summary").click();
  await page.getByRole("button", { name: "Polski", exact: true }).click();
  await expect(
    page.getByRole("button", { name: "Zmień lokalizację", exact: true }),
  ).toBeVisible();
  await expect(page.getByTestId("target-name")).toHaveText(shelter.name);
  expect(
    await map!.evaluate(
      (e) => e === document.getElementById("citizen-leaflet-map"),
    ),
  ).toBe(true);
});

test("the main page shares the panel but starts collapsed on mobile", async ({
  page,
}) => {
  await page.setViewportSize({ width: 400, height: 841 });
  await prepare(page);
  await page.goto("/map");
  const toggle = page.locator(".side-panel-wrapper .command-toggle");
  await expect(toggle).toHaveAttribute("aria-expanded", "false");
  await toggle.click();
  await expect(
    page.getByRole("button", { name: "Start evacuation", exact: true }),
  ).toBeVisible();
  const zoom = await page.locator(".leaflet-control-zoom").boundingBox();
  expect(zoom!.y).toBe(10);
  await toggle.click();
  await expect(
    page.getByRole("button", { name: "Start evacuation", exact: true }),
  ).toBeHidden();
});

test("a shelter marker can be selected as the actual position in manual mode", async ({
  page,
}) => {
  await page.setViewportSize({ width: 400, height: 841 });
  const requests = await prepare(page, { gps: "denied" });
  await page.goto("/evacuate");
  await page
    .getByRole("button", { name: "Select on map", exact: true })
    .click();
  await page.locator(".custom-shelter-marker").first().click();
  await expect(page.getByTestId("target-name")).toBeVisible();
  const assigned = requests.find((r) => r.path.endsWith("/assign"));
  expect(assigned?.body?.latitude).toBe(shelter.latitude);
  expect(assigned?.body?.longitude).toBe(shelter.longitude);
});

test("cancelled GPS correction cannot replace the confirmed location later", async ({
  page,
}) => {
  await prepare(page);
  await page.goto("/evacuate");
  await expect(page.getByTestId("target-name")).toBeVisible();
  const route = await page.locator(".navigation-link").getAttribute("href");
  await page.evaluate(() => {
    navigator.geolocation.getCurrentPosition = (success) => {
      (window as any).finishGps = () =>
        success({
          coords: { latitude: 50.064, longitude: 19.936, accuracy: 10 },
        } as GeolocationPosition);
    };
  });
  await page
    .getByRole("button", { name: "Change location", exact: true })
    .click();
  await page.getByRole("button", { name: "Use GPS", exact: true }).click();
  await page.getByRole("button", { name: "Cancel", exact: true }).click();
  await page.evaluate(() => (window as any).finishGps());
  await expect(page.locator(".navigation-link")).toHaveAttribute(
    "href",
    route!,
  );
});

test("unlimited safe zones show people without a capacity limit", async ({
  page,
}) => {
  await prepare(page, { capacity: 0 });
  await page.goto("/evacuate");
  await expect(page.getByTestId("target-name")).toHaveText(shelter.name);
  await page.locator(".shelter-details summary").click();
  await expect(page.getByTestId("target-occupancy")).toHaveText("35 people");
  await expect(
    page.locator(".custom-shelter-marker").first(),
  ).not.toContainText("%");
});
