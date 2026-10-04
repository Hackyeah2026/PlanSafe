import { expect } from "@playwright/test";
import assert from "node:assert/strict";
import { fileURLToPath } from "node:url";
import { test } from "./fixtures.js";

test("Citizen bounds and popup labels work with real Leaflet and browser DOM", async ({
  gpuPage: page,
}) => {
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  await page.route("https://tile.openstreetmap.org/**", (route) =>
    route.fulfill({ status: 204 }),
  );
  await page.setContent(
    '<div id="citizen" style="width:800px;height:500px"></div>',
  );
  await page.addScriptTag({
    path: fileURLToPath(
      new URL(
        "../../src/PlanSafe.App/wwwroot/lib/leaflet/leaflet.js",
        import.meta.url,
      ),
    ),
  });
  await page.addStyleTag({
    path: fileURLToPath(
      new URL(
        "../../src/PlanSafe.App/wwwroot/lib/leaflet/leaflet.css",
        import.meta.url,
      ),
    ),
  });
  const name = '<img src=x onerror="window.popupInjected=true">';
  const result = await page.evaluate(async (name) => {
    const module = "/citizenMapInterop.js";
    const citizen = (await import(
      module
    )) as typeof import("../../src/PlanSafe.App/wwwroot/js/citizenMapInterop.js");
    const initialized = citizen.initCitizenMap("citizen", 50.06, 19.93);
    citizen.updateCitizenMap(
      "citizen",
      50.05,
      19.92,
      0,
      JSON.stringify([
        {
          id: "shelter",
          name,
          latitude: 50.07,
          longitude: 19.94,
          capacity: 100,
          currentOccupancy: 10,
        },
      ]),
      "shelter",
      "[]",
      JSON.stringify([
        [50.05, 19.92],
        [50.07, 19.94],
      ]),
      "[]",
      "[]",
    );
    return { initialized, fitted: citizen.fitCitizenBounds("citizen") };
  }, name);
  assert.deepEqual(result, { initialized: true, fitted: true });
  const marker = page.locator(".custom-shelter-marker");
  await expect(marker).toContainText(name);
  await expect(marker.locator("img")).toHaveCount(0);
  await marker.click();
  await expect(page.locator(".leaflet-popup-content strong")).toHaveText(name);
  await expect(page.locator(".leaflet-popup-content img")).toHaveCount(0);
  assert.equal(
    await page.evaluate(() => Reflect.get(window, "popupInjected")),
    undefined,
  );
  assert.deepEqual(errors, []);
});
