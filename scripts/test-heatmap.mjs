import test from "node:test";
import assert from "node:assert/strict";
import { initMapSimulator } from "../src/PlanSafe.App/wwwroot/js/crowdSimulatorInterop.js";

function setup(t) {
  function makeCanvas(width = 800, height = 600) {
    const canvas = {
      width,
      height,
      clientWidth: width,
      clientHeight: height,
      style: {},
      addEventListener() {},
      removeEventListener() {},
    };
    const context = new Proxy(
      {
        createImageData: (w, h) => ({
          width: w,
          height: h,
          data: new Uint8ClampedArray(w * h * 4),
        }),
        putImageData: (image) => {
          canvas.image = image;
        },
        drawImage: (source, ...bounds) => {
          canvas.draw = { source, bounds };
        },
      },
      { get: (object, key) => object[key] ?? (() => {}) },
    );
    canvas.getContext = () => context;
    return canvas;
  }
  const previousWindow = globalThis.window;
  const previousDocument = globalThis.document;
  globalThis.window = { addEventListener() {}, removeEventListener() {} };
  globalThis.document = { createElement: () => makeCanvas(300, 150) };
  const canvas = makeCanvas();
  const simulator = initMapSimulator(canvas, "heatmap-test");
  simulator.setWorldConfig({ worldWidth: 10000, worldHeight: 4000 });
  simulator.setTransform({ scale: 1, offsetX: 0, offsetY: 0 });
  t.after(() => {
    simulator.dispose();
    globalThis.window = previousWindow;
    globalThis.document = previousDocument;
  });
  return {
    simulator,
    render(agents, mode, granulation = 1) {
      simulator.renderBinary(
        new Float32Array(agents.flat()),
        agents.length,
        mode,
        false,
        2.5,
        granulation,
        false,
      );
      const trail = canvas.draw.source;
      const {
        source,
        bounds: [x, y, w, h],
      } = trail.draw;
      const image = source.image;
      const pixels = [];
      for (let row = 0; row < image.height; row++) {
        for (let column = 0; column < image.width; column++) {
          const index = (row * image.width + column) * 4;
          if (image.data[index + 3] > 8)
            pixels.push({
              x: x + ((column + 0.5) * w) / image.width,
              y: y + ((row + 0.5) * h) / image.height,
              color: Array.from(image.data.slice(index, index + 3)),
            });
        }
      }
      return { pixels, image, filter: trail.getContext().filter };
    },
  };
}

for (const mode of ["density", "heatmap"]) {
  test(`${mode}: a city-sized world keeps a narrow crowd stream local`, (t) => {
    const s = setup(t);
    const agents = Array.from({ length: 201 }, (_, i) => [
      200 + i * 2,
      300,
      1,
      0,
      0.35,
    ]);
    const result = s.render(agents, mode);
    assert.ok(result.pixels.length > 100, "The stream remains visible");
    assert.ok(
      result.pixels.every((p) => Math.abs(p.y - 300) <= 4),
      "Color stays beside the crowd, not across neighboring streets",
    );
    assert.equal(
      result.filter,
      "none",
      "Canvas blur must not widen the stream",
    );
    assert.ok(
      result.image.width <= 1025 && result.image.height <= 1025,
      "Only the visible area is sampled",
    );
    s.simulator.setWorldConfig({ worldWidth: 20000, worldHeight: 8000 });
    const larger = s.render(agents, mode);
    assert.deepEqual(
      larger.pixels.map((p) => [p.x, p.y]),
      result.pixels.map((p) => [p.x, p.y]),
      "Changing offscreen map extent does not enlarge heatmap coverage",
    );
  });

  test(`${mode}: isolated agents remain visible between grid centers, including after zoom and pan`, (t) => {
    const s = setup(t);
    const agents = [[400, 300, 1, 0, 0.35]];
    const result = s.render(agents, mode, 16);
    assert.ok(result.pixels.length > 0);
    assert.ok(
      result.pixels.every(
        (p) => Math.abs(p.x - 400) <= 4 && Math.abs(p.y - 300) <= 4,
      ),
    );
    s.simulator.setTransform({ scale: 2, offsetX: -600, offsetY: -400 });
    const zoomed = s.render(agents, mode, 16);
    assert.ok(zoomed.pixels.length > 0);
    assert.ok(
      zoomed.pixels.every(
        (p) => Math.abs(p.x - 200) <= 4 && Math.abs(p.y - 200) <= 4,
      ),
      "Heatmap stays aligned with the projected agent",
    );
  });
}

test("speed heatmap keeps separated fast and slow streams distinct", (t) => {
  const s = setup(t);
  const slow = Array.from({ length: 101 }, (_, i) => [
    200 + i * 2,
    280,
    0.1,
    0,
    0.35,
  ]);
  const fast = Array.from({ length: 101 }, (_, i) => [
    200 + i * 2,
    320,
    1.8,
    0,
    0.35,
  ]);
  const { pixels } = s.render([...slow, ...fast], "heatmap");
  assert.ok(pixels.length > 100);
  assert.ok(
    pixels.every((p) => Math.abs(p.y - 280) <= 4 || Math.abs(p.y - 320) <= 4),
    "The space between streams stays transparent",
  );
  const slowColor = pixels.find((p) => p.y < 300).color;
  const fastColor = pixels.find((p) => p.y > 300).color;
  assert.notDeepEqual(slowColor, fastColor);
});

for (const mode of ["density", "heatmap"]) {
  test(`${mode}: heatmap color does not fill building cells beside agents`, (t) => {
    const s = setup(t);
    const columns = 800,
      rows = 600;
    const blocked = new Uint8Array(columns * rows);
    for (let y = 290; y < 310; y++)
      for (let x = 400; x < 420; x++) blocked[y * columns + x] = 1;
    s.simulator.setMapTerrain(blocked, columns, rows, 1);
    const { pixels } = s.render([[399.5, 300, 1, 0, 0.35]], mode);
    assert.ok(pixels.length > 0);
    assert.ok(
      pixels.every((p) => p.x < 400),
      "A nearby agent must not paint the interior of the adjacent building",
    );
  });
}
