import { expect, test, type Page } from "@playwright/test";

async function openMenu(page: Page) {
  const menu = page.locator(".global-menu");
  if (!(await menu.evaluate((element) => element.hasAttribute("open")))) {
    await menu.locator("summary").click();
  }
  return menu;
}

for (const [locale, language, caption] of [
  ["pl-PL", "pl", "Informacje o strefie"],
  ["en-US", "en", "Zone information"],
  ["de-DE", "en", "Zone information"],
]) {
  test(`browser locale ${locale} selects ${language}`, async ({ browser }) => {
    const context = await browser.newContext({ locale });
    const page = await context.newPage();
    await page.route("https://tile.openstreetmap.org/**", (route) =>
      route.fulfill({ status: 204 }),
    );
    await page.goto(`${test.info().project.use.baseURL}/map`);
    await expect(
      page.getByRole("heading", { name: caption, exact: true }),
    ).toBeVisible();
    await expect(page.locator("html")).toHaveAttribute("lang", language);
    const menu = await openMenu(page);
    await expect(
      menu.locator("div[role=group]").filter({
        has: page.getByRole("button", {
          name: language === "pl" ? "Jasny" : "Light",
          exact: true,
        }),
      }),
    ).toBeVisible();
    const languages = menu.getByTestId("language-settings");
    await expect(
      languages.getByRole("button", {
        name: language === "pl" ? "Polski" : "English",
      }),
    ).toHaveAttribute("aria-pressed", "true");
    expect(
      await page.evaluate(() => localStorage.getItem("plansafe.language.v1")),
    ).toBeNull();
    await context.close();
  });
}

test("switching updates Razor and map popups without restarting the page, and persists", async ({
  page,
}) => {
  await page.addInitScript(() => {
    if (!localStorage.getItem("plansafe.language.v1"))
      localStorage.setItem("plansafe.language.v1", "en");
  });
  await page.goto("/map");
  await expect(
    page.getByRole("heading", { name: "Zone information", exact: true }),
  ).toBeVisible();
  // A JS-owned popup may be detached when the language changes, then reopened later.
  await page.evaluate(async () => {
    const path = "/js/mapPopups.js";
    const { createMapItemPopup } = await import(path);
    const popup = createMapItemPopup(
      {
        type: "blockade",
        name: "User name <test>",
        metricInfo: "Length: 12 m",
      },
      () => {},
    );
    popup.id = "localization-test-popup";
    document.body.append(popup);
    (
      window as unknown as { localizationPageMarker: object }
    ).localizationPageMarker = {};
  });
  const marker = await page.evaluateHandle(
    () =>
      (window as unknown as { localizationPageMarker: object })
        .localizationPageMarker,
  );
  const menu = await openMenu(page);
  await menu.getByRole("button", { name: "Polski", exact: true }).click();
  await expect(
    page.getByRole("heading", { name: "Informacje o strefie", exact: true }),
  ).toBeVisible();
  await expect(
    menu.getByRole("button", { name: "Jasny", exact: true }),
  ).toBeVisible();
  await expect(page.locator("#localization-test-popup")).toContainText(
    "Długość: 12 m",
  );
  await expect(
    page
      .locator("#localization-test-popup")
      .getByRole("button", { name: "Usuń", exact: true }),
  ).toBeVisible();
  await expect(page.locator("#localization-test-popup")).toContainText(
    "User name <test>",
  );
  expect(
    await marker.evaluate(
      (value) =>
        value ===
        (window as unknown as { localizationPageMarker: object })
          .localizationPageMarker,
    ),
  ).toBe(true);
  expect(
    await page.evaluate(() => localStorage.getItem("plansafe.language.v1")),
  ).toBe("pl");
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("lang", "pl");
  await page.goto("/evacuate");
  await expect(
    page.getByText("Asystent ewakuacji", { exact: true }),
  ).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("lang", "pl");
});

test("switching a running simulation preserves its state", async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.setItem("plansafe_simulation_engine", "wasm");
    localStorage.setItem("plansafe.language.v1", "en");
  });
  await page.goto("/simulation-demo");
  const run = page.getByRole("button", { name: "Run", exact: true });
  await expect(run).toBeEnabled({ timeout: 60000 });
  await expect(page.locator('label[for="time-scale"]')).toContainText("Speed");
  await expect(
    page.getByRole("button", { name: "Publish plan", exact: true }),
  ).toBeVisible();
  await expect(page.locator('#render-mode option[value="agents"]')).toHaveText(
    "Agents",
  );
  await page.getByTestId("agent-count").fill("50");
  await run.click();
  await expect(page.getByTestId("status")).toContainText("Running");
  const menu = await openMenu(page);
  await menu.getByRole("button", { name: "Polski", exact: true }).click();
  await expect(page.getByTestId("status")).toContainText("Uruchomiona");
  await expect(page.locator('label[for="time-scale"]')).toContainText("Tempo");
  await expect(page.locator('label[for="granulation"]')).toContainText(
    "Agenci na punkt:",
  );
  await expect(
    page.getByRole("checkbox", { name: "Bez limitu", exact: true }),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Opublikuj plan", exact: true }),
  ).toBeVisible();
  await expect(page.getByTestId("agent-count")).toHaveValue("50");
  await expect(
    page.getByRole("button", { name: "Zatrzymaj", exact: true }),
  ).toBeVisible();
  await menu.getByRole("button", { name: "English", exact: true }).click();
  await expect(page.getByTestId("status")).toContainText("Running");
});
