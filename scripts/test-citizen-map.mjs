import test from "node:test";
import assert from "node:assert/strict";
import {
  initCitizenMap,
  updateCitizenMap,
  disposeCitizenMap,
  fitCitizenBounds,
} from "../src/PlanSafe.App/wwwroot/js/citizenMapInterop.js";

test("initial view and Fit map share bounds without refitting on every update", () => {
  const fits = [];
  const fitOptions = [];
  const container = {
    querySelector: () => null,
    closest: () => null,
    getBoundingClientRect: () => ({
      top: 0,
      left: 0,
      right: 800,
      bottom: 500,
      width: 800,
      height: 500,
    }),
  };
  const map = {
    getContainer: () => container,
    zoomControl: { setPosition() {} },
    on() {},
    invalidateSize() {},
    remove() {},
    fitBounds(bounds, options) {
      fits.push(bounds);
      fitOptions.push(options);
    },
  };
  const layer = () => ({
    items: [],
    addTo() {
      return this;
    },
    clearLayers() {
      this.items = [];
    },
    getLayers() {
      return this.items;
    },
  });
  globalThis.document = { getElementById: () => container };
  globalThis.window = { matchMedia: () => ({ matches: false }) };
  globalThis.L = {
    map: () => map,
    tileLayer: () => ({ addTo() {} }),
    layerGroup: layer,
    polygon: (points) => ({
      addTo(group) {
        group.items.push(this);
        return this;
      },
      getBounds: () => points,
    }),
    latLngBounds: () => ({
      items: [],
      extend(points) {
        this.items.push(points);
      },
      isValid() {
        return this.items.length > 0;
      },
    }),
  };
  const zones = [
    [
      [50, 19],
      [50, 20],
      [51, 20],
    ],
    [
      [52, 21],
      [52, 22],
      [53, 22],
    ],
  ];
  const update = (needsLocation) =>
    updateCitizenMap(
      "test",
      null,
      null,
      null,
      "[]",
      null,
      "[]",
      "[]",
      JSON.stringify(zones),
      "[]",
      needsLocation,
    );
  try {
    initCitizenMap("test");
    update(true);
    assert.deepEqual(fits[0].items, zones);
    assert.equal(fitOptions[0].maxZoom, 16);
    assert.equal(fitOptions[0].animate, false);
    update(true);
    assert.equal(fits.length, 1);
    fitCitizenBounds("test");
    assert.deepEqual(fits[1].items, fits[0].items);
    assert.deepEqual(fitOptions[1], fitOptions[0]);
    update(false);
    update(true);
    assert.deepEqual(fits.at(-1).items, zones);
    assert.equal(fits.length, 2);
  } finally {
    disposeCitizenMap("test");
    delete globalThis.L;
    delete globalThis.window;
    delete globalThis.document;
  }
});
