import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test, { type TestContext } from "node:test";
import vm from "node:vm";

// Leaflet computes the real geometry. Only DOM creation and the map viewport
// are replaced, so these tests need neither tiles nor a browser/GPU installation.
class ElementFixture extends EventTarget {
  children: ElementFixture[] = [];
  parentElement: ElementFixture | null = null;
  className = "";
  innerHTML = "";
  style: Record<string, string> = {};
  private text = "";
  readonly tagName: string;

  constructor(tagName: string) {
    super();
    this.tagName = tagName;
  }

  get textContent(): string {
    return this.text + this.children.map((child) => child.textContent).join("");
  }

  set textContent(value: string) {
    this.text = value;
    this.children = [];
  }

  append(...children: ElementFixture[]): void {
    for (const child of children) child.parentElement = this;
    this.children.push(...children);
  }

  getBoundingClientRect() {
    return {
      top: 0,
      left: 0,
      right: 800,
      bottom: 500,
      width: 800,
      height: 500,
    };
  }

  closest(selector: string): ElementFixture | null {
    const matches = selector.startsWith(".")
      ? this.className.split(" ").includes(selector.slice(1))
      : this.tagName === selector;
    return matches ? this : (this.parentElement?.closest(selector) ?? null);
  }

  querySelector(selector: string): ElementFixture | null {
    for (const child of this.children) {
      if (
        selector.startsWith(".")
          ? child.className.split(" ").includes(selector.slice(1))
          : child.tagName === selector
      )
        return child;
      const found = child.querySelector(selector);
      if (found) return found;
    }
    return null;
  }

  click(): void {
    this.dispatchEvent(new Event("click"));
  }
}

async function setup(t: TestContext, citizen = true) {
  const previous = new Map(
    ["window", "document", "localStorage", "L", "requestAnimationFrame"].map(
      (key) => [key, Object.getOwnPropertyDescriptor(globalThis, key)],
    ),
  );
  const container = new ElementFixture("div");
  const fits: number[][] = [];
  const layers: { getLayers?: () => unknown[] }[] = [];
  const map = {
    layers,
    fits,
    getContainer: () => container,
    addLayer(layer: (typeof layers)[number]) {
      layers.push(layer);
      return this;
    },
    on() {
      return this;
    },
    remove() {},
    invalidateSize() {},
    zoomControl: { setPosition() {} },
    fitBounds(bounds: {
      getSouth(): number;
      getWest(): number;
      getNorth(): number;
      getEast(): number;
    }) {
      fits.push([
        bounds.getSouth(),
        bounds.getWest(),
        bounds.getNorth(),
        bounds.getEast(),
      ]);
    },
  };
  const storage = new Map<string, string>();
  const callbacks: unknown[][] = [];
  const dotNetRef = {
    invokeMethodAsync(...args: unknown[]) {
      callbacks.push(args);
      return Promise.resolve();
    },
  };
  const window = {
    addEventListener() {},
    removeEventListener() {},
    matchMedia: () => ({ matches: false }),
    screen: { deviceXDPI: 1, logicalXDPI: 1 },
  };
  const document = {
    documentElement: { style: {} },
    getElementById: () => container,
    createElement: (tag: string) => new ElementFixture(tag),
    createTextNode: (text: string) => {
      const node = new ElementFixture("#text");
      node.textContent = text;
      return node;
    },
  };
  const scope = vm.createContext({
    window,
    document,
    navigator: { userAgent: "node", platform: "Linux" },
  });
  vm.runInContext(
    readFileSync(
      new URL(
        "../src/PlanSafe.App/wwwroot/lib/leaflet/leaflet.js",
        import.meta.url,
      ),
      "utf8",
    ),
    scope,
  );
  const leaflet = scope.window.L;
  leaflet.map = () => map;
  Object.assign(globalThis, {
    window,
    document,
    L: leaflet,
    requestAnimationFrame: (callback: () => void) => callback(),
    localStorage: {
      getItem: (key: string) => storage.get(key) ?? null,
      setItem: (key: string, value: string) => storage.set(key, value),
    },
  });
  const citizenModule =
    await import("../src/PlanSafe.App/wwwroot/js/citizenMapInterop.js");
  const editorModule =
    await import("../src/PlanSafe.App/wwwroot/js/mapInterop.js");
  t.after(() => {
    citizenModule.disposeCitizenMap("test-map");
    editorModule.disposeMap("test-map");
    for (const [key, descriptor] of previous) {
      if (descriptor) Object.defineProperty(globalThis, key, descriptor);
      else Reflect.deleteProperty(globalThis, key);
    }
  });
  if (citizen)
    assert.equal(
      citizenModule.initCitizenMap("test-map", 50, 19, 15, dotNetRef),
      true,
    );
  else assert.equal(editorModule.initMap("test-map", {}, dotNetRef), true);
  return { map, storage, callbacks, citizenModule, editorModule };
}

type Point = [number, number];
interface MapData {
  citizen?: Point;
  shelters?: {
    id?: string;
    name?: string;
    latitude: number;
    longitude: number;
  }[];
  selectedId?: string;
  route?: Point[];
  safeZones?: Point[][];
}

function update(
  module: Awaited<ReturnType<typeof setup>>["citizenModule"],
  data: MapData = {},
) {
  module.updateCitizenMap(
    "test-map",
    data.citizen?.[0] ?? null,
    data.citizen?.[1] ?? null,
    0,
    JSON.stringify(data.shelters ?? []),
    data.selectedId ?? null,
    "[]",
    JSON.stringify(data.route ?? []),
    "[]",
    JSON.stringify(data.safeZones ?? []),
  );
}

const markup = '<img src=x onerror="window.auditMarker=1">';
const hostileId = "shelter');window.auditMarker=2;//";

test("imported map names and metrics remain text; remove uses the exact imported ID", async (t) => {
  const { map, storage, editorModule } = await setup(t, false);
  editorModule.loadSessionItems(
    "test-map",
    JSON.stringify([
      {
        id: hostileId,
        type: "safe_point",
        position: [50, 19],
        name: markup,
        metricInfo: "<script>window.auditMarker=3</script>",
      },
      { id: "keep", type: "safe_point", position: [51, 20], name: "Keep" },
    ]),
  );
  const items = map.layers[1];
  const marker = items.getLayers!()[0] as {
    getPopup(): { getContent(): ElementFixture };
  };
  const popup = marker.getPopup().getContent();
  assert.ok(popup instanceof ElementFixture);
  assert.equal(popup.querySelector(".map-popup-title")?.textContent, markup);
  assert.equal(
    popup.querySelector(".map-popup-meta")?.textContent,
    "<script>window.auditMarker=3</script>",
  );
  assert.equal(popup.querySelector("img"), null);
  assert.equal(popup.querySelector("script"), null);
  popup.querySelector("button")!.click();
  assert.equal(items.getLayers!().length, 1);
  const stored = JSON.parse(storage.get("plansafe_map_items")!);
  assert.deepEqual(
    stored.map((item: { id: string }) => item.id),
    ["keep"],
  );
});

test("published shelter names remain text in markers and popups; selection preserves the ID", async (t) => {
  const { map, callbacks, citizenModule } = await setup(t);
  update(citizenModule, {
    shelters: [{ id: hostileId, name: markup, latitude: 50, longitude: 19 }],
    selectedId: hostileId,
  });
  const marker = map.layers[5].getLayers!()[0] as {
    options: { icon: { options: { html: ElementFixture } } };
    getPopup(): { getContent(): ElementFixture };
    fire(event: string): void;
  };
  const popup = marker.getPopup().getContent();
  const label = marker.options.icon.options.html;
  assert.ok(popup instanceof ElementFixture);
  assert.ok(label instanceof ElementFixture);
  assert.equal(popup.querySelector("strong")?.textContent, markup);
  assert.ok(label.textContent.includes(`${markup} (0%)`));
  assert.equal(popup.querySelector("img"), null);
  assert.equal(label.querySelector("img"), null);
  marker.fire("click");
  assert.deepEqual(callbacks, [["OnShelterSelected", hostileId]]);
});

test("initial bounds include populated child layers, including safe-zone-only and route-only maps", async (t) => {
  const cases: { name: string; data: MapData; bounds: number[] }[] = [
    { name: "citizen", data: { citizen: [50, 19] }, bounds: [50, 19, 50, 19] },
    {
      name: "shelter",
      data: { shelters: [{ latitude: 51, longitude: 20 }] },
      bounds: [51, 20, 51, 20],
    },
    {
      name: "safe zone",
      data: {
        safeZones: [
          [
            [50, 19],
            [51, 19],
            [51, 20],
          ],
        ],
      },
      bounds: [50, 19, 51, 20],
    },
    {
      name: "route",
      data: {
        route: [
          [50, 19],
          [52, 21],
        ],
      },
      bounds: [50, 19, 52, 21],
    },
    {
      name: "combined",
      data: {
        citizen: [49, 18],
        shelters: [{ latitude: 53, longitude: 22 }],
        safeZones: [
          [
            [50, 19],
            [51, 19],
            [51, 20],
          ],
        ],
        route: [
          [50, 19],
          [52, 21],
        ],
      },
      bounds: [49, 18, 53, 22],
    },
  ];
  for (const { name, data, bounds } of cases) {
    await t.test(name, async (t) => {
      const { map, citizenModule } = await setup(t);
      update(citizenModule);
      assert.equal(citizenModule.fitCitizenBounds("test-map"), false);
      assert.equal(map.fits.length, 0);
      update(citizenModule, data);
      assert.deepEqual(map.fits, [bounds]);
      update(citizenModule, data);
      assert.equal(
        map.fits.length,
        1,
        "polling must preserve the user's viewport",
      );
      assert.equal(citizenModule.fitCitizenBounds("test-map"), true);
      assert.deepEqual(map.fits, [bounds, bounds]);
    });
  }
});

test("failed initial fits are retried on the next update", async (t) => {
  const { map, citizenModule } = await setup(t);
  const fitBounds = map.fitBounds;
  const warnings: unknown[][] = [];
  t.mock.method(console, "warn", (...args: unknown[]) => warnings.push(args));
  map.fitBounds = () => {
    throw new Error("Viewport is temporarily unavailable");
  };
  update(citizenModule, { citizen: [50, 19] });
  assert.equal(warnings.length, 1);
  assert.equal(map.fits.length, 0);
  map.fitBounds = fitBounds;
  update(citizenModule, { citizen: [50, 19] });
  assert.deepEqual(map.fits, [[50, 19, 50, 19]]);
  update(citizenModule, { citizen: [51, 20] });
  assert.equal(map.fits.length, 1);
  assert.equal(citizenModule.fitCitizenBounds("missing-map"), false);
});
