import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";
import vm from "node:vm";

const source = fs.readFileSync(
  new URL("../src/PlanSafe.App/wwwroot/js/appearance.js", import.meta.url),
  "utf8",
);

function boot(raw = null, dark = false, blocked = false) {
  const window = new EventTarget();
  const media = new EventTarget();
  media.matches = dark;
  const document = { documentElement: { dataset: {} } };
  let stored = raw;
  window.matchMedia = () => media;
  window.localStorage = {
    getItem() {
      if (blocked) throw new Error("Storage blocked");
      return stored;
    },
  };
  vm.runInNewContext(source, { window, document });
  return {
    get theme() {
      return document.documentElement.dataset.bsTheme;
    },
    os(dark) {
      media.matches = dark;
      media.dispatchEvent(new Event("change"));
    },
    storage(value) {
      stored = value;
      const event = new Event("storage");
      event.key = "plansafe.appearance.v1";
      window.dispatchEvent(event);
    },
  };
}

const preference = (theme) => JSON.stringify({ version: 1, theme });

test("system preference applies during boot and follows OS changes", () => {
  const env = boot(null, true);
  assert.equal(env.theme, "dark");
  env.os(false);
  assert.equal(env.theme, "light");
});

test("stored themes override OS; malformed or blocked storage falls back safely", () => {
  const env = boot(preference("light"), true);
  env.os(true);
  assert.equal(env.theme, "light");
  for (const raw of [
    "broken",
    "null",
    "[]",
    preference("invalid"),
    '{"version":2,"theme":"light"}',
  ]) {
    assert.equal(boot(raw, true).theme, "dark");
  }
  assert.equal(boot(preference("light"), true, true).theme, "dark");
});

test("cross-tab updates apply and deletion restores system preference", () => {
  const env = boot();
  env.storage(preference("dark"));
  assert.equal(env.theme, "dark");
  env.storage(null);
  assert.equal(env.theme, "light");
});
