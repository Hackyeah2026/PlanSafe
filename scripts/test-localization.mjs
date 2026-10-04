import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
import test from "node:test";
import { TextKeys } from "../src/PlanSafe.App/wwwroot/js/textKeys.js";

const catalogue = readFileSync(
  new URL("../src/PlanSafe.App/wwwroot/js/translations.js", import.meta.url),
  "utf8",
);
const controller = readFileSync(
  new URL("../src/PlanSafe.App/wwwroot/js/localization.js", import.meta.url),
  "utf8",
);

function boot(languages, saved = null, blocked = false) {
  const storage = new Map(
    saved === null ? [] : [["plansafe.language.v1", saved]],
  );
  const context = {
    navigator: { languages, language: languages[0] },
    localStorage: {
      getItem: (key) => {
        if (blocked) throw new Error("blocked");
        return storage.get(key) ?? null;
      },
      setItem: (key, value) => {
        if (blocked) throw new Error("blocked");
        storage.set(key, value);
      },
    },
    document: {
      documentElement: { lang: "", style: { setProperty() {} } },
      querySelectorAll: () => [],
      addEventListener() {},
    },
    Event,
    WeakRef,
    dispatchEvent() {},
  };
  context.window = context;
  runInNewContext(catalogue + controller, context);
  return {
    api: context.planSafeLocalization,
    catalogues: context.planSafeTranslations,
    storage,
    document: context.document,
  };
}

test("uses the first supported browser language, with English fallback and no implicit preference", () => {
  for (const [languages, expected] of [
    [["pl-PL", "en-US"], "pl"],
    [["en-US", "pl-PL"], "en"],
    [["de-DE", "PL"], "pl"],
    [["de-DE"], "en"],
    [[], "en"],
  ]) {
    const { api, storage, document } = boot(languages);
    assert.equal(api.detect(), expected);
    assert.equal(document.documentElement.lang, expected);
    assert.equal(storage.size, 0);
  }
});

test("saved supported choices win; invalid and blocked storage use the browser", () => {
  assert.equal(boot(["pl-PL"], "en").api.detect(), "en");
  assert.equal(boot(["en-US"], "pl").api.detect(), "pl");
  assert.equal(boot(["pl-PL"], "de").api.detect(), "pl");
  assert.equal(boot(["pl-PL"], null, true).api.detect(), "pl");
});

test("switches detached text safely, preserves arguments, and persists only explicit choices", () => {
  const { api, catalogues, storage } = boot(["en-US"]);
  const node = { dataset: {}, textContent: "", setAttribute() {} };
  api.bind(node, TextKeys.Shelter.OccupancySummary, 2, 10, 20);
  assert.equal(node.textContent, "Occupancy: 2 / 10 (20%)");
  api.apply("pl", catalogues.pl, true);
  assert.equal(node.textContent, "Obłożenie: 2 / 10 (20%)");
  assert.equal(storage.get("plansafe.language.v1"), "pl");
  api.bind(node, "User text <script>");
  assert.equal(node.textContent, "User text <script>");
  api.apply("en", catalogues.en, false);
  assert.equal(storage.get("plansafe.language.v1"), "pl");
});

test("blocked storage does not prevent an explicit language change", () => {
  const { api, catalogues, document } = boot(["en-US"], null, true);
  api.apply("pl", catalogues.pl, true);
  assert.equal(document.documentElement.lang, "pl");
  assert.equal(api.text(TextKeys.Appearance.Language), "Język");
});
