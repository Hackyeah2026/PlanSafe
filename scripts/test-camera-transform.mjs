import test from "node:test";
import assert from "node:assert/strict";
import { initSimulator } from "../src/PlanSafe.App/wwwroot/js/crowdSimulatorInterop.js";

function context() {
  const calls = [];
  return new Proxy(
    { calls },
    {
      get(target, key) {
        if (key in target) return target[key];
        return (...args) =>
          calls.push({ method: key, args, strokeStyle: target.strokeStyle });
      },
    },
  );
}

function setup(t, mapMode = false) {
  const ctx = context();
  const handlers = new Map();
  const windowHandlers = new Map();
  const mapHandlers = new Map();
  let mapProjectionScale = 10000;
  const canvas = {
    width: 800,
    height: 500,
    clientWidth: 800,
    clientHeight: 500,
    style: {},
    getContext: () => ctx,
    // Canvas is displayed at twice its backing size, like the responsive demo.
    getBoundingClientRect: () => ({
      left: 10,
      top: 20,
      width: 1600,
      height: 1000,
    }),
    addEventListener: (name, handler) => handlers.set(name, handler),
    removeEventListener: (name) => handlers.delete(name),
  };
  const previousWindow = globalThis.window,
    previousDocument = globalThis.document;
  globalThis.window = {
    addEventListener: (name, handler) => windowHandlers.set(name, handler),
    removeEventListener: (name) => windowHandlers.delete(name),
    PlanSafeMap: {
      getMap: () => ({
        on: (name, handler) => mapHandlers.set(name, handler),
        off: (name) => mapHandlers.delete(name),
        latLngToContainerPoint: ([lat, lng]) => ({
          x: (lng - 19.9) * mapProjectionScale,
          y: (50.07 - lat) * mapProjectionScale,
        }),
      }),
    },
  };
  globalThis.document = {
    createElement: () => ({ getContext: () => context() }),
  };
  const simulator = initSimulator(canvas, mapMode ? "map" : null);
  t.after(() => {
    simulator.dispose();
    globalThis.window = previousWindow;
    globalThis.document = previousDocument;
  });
  const obstacles = [
    { x: 70, y: 36, width: 24, height: 56 },
    { x: 70, y: 108, width: 24, height: 56 },
  ];
  const targets = [
    { x: 180, y: 24, width: 16, height: 40 },
    { x: 180, y: 136, width: 16, height: 40 },
  ];
  const agents = new Float32Array([14, 100, 1, 0, 2, 170, 80, 1, 0, 2]);
  simulator.setWorldConfig({
    worldWidth: 200,
    worldHeight: 200,
    obstacles,
    targets,
  });
  function render() {
    ctx.calls.length = 0;
    simulator.renderBinary(agents, 2, "agents", false, 2.5, 1, false);
    const rectangles = ctx.calls
      .filter((c) => c.method === "strokeRect")
      .map((c) => c.args);
    const circles = ctx.calls
      .filter((c) => c.method === "arc")
      .map((c) => c.args);
    return { rectangles, circles };
  }
  return {
    simulator,
    handlers,
    windowHandlers,
    mapHandlers,
    ctx,
    agents,
    setMapProjectionScale: (value) => {
      mapProjectionScale = value;
      mapHandlers.get("zoom")();
    },
    render,
    obstacles,
    targets,
  };
}

test("map startup and empty redraws leave the basemap visible", (t) => {
  const s = setup(t, true);
  const assertTransparent = () => {
    assert.deepEqual(
      s.ctx.calls.filter((call) =>
        ["fillRect", "strokeRect", "fillText"].includes(call.method),
      ),
      [],
      "Empty map frames must not paint the demo background, grid or HUD",
    );
    assert.ok(s.ctx.calls.some((call) => call.method === "clearRect"));
  };
  // Configuration redraws before agents exist, including while GPU boots.
  assertTransparent();
  s.ctx.calls.length = 0;
  s.simulator.setWorldConfig({
    originLat: 50.07,
    originLng: 19.9,
    minLat: 50.05,
    maxLng: 19.92,
  });
  assertTransparent();
  s.ctx.calls.length = 0;
  s.mapHandlers.get("move")();
  assertTransparent();
  const frame = s.render();
  assert.equal(frame.circles.length, 2);
  assert.equal(frame.rectangles.length, 0);
  assertTransparent();
  s.ctx.calls.length = 0;
  s.simulator.renderBinary(
    new Float32Array(0),
    0,
    "agents",
    false,
    2.5,
    1,
    false,
  );
  s.mapHandlers.get("resize")();
  assertTransparent();
});

function close(actual, expected) {
  assert.ok(Math.abs(actual - expected) < 1e-8, `${actual} != ${expected}`);
}

function geometry(frame, obstacles, targets) {
  // Derive the transform from the visible world boundary, independently of
  // camera state, and require every rendered object to share that transform.
  const [x, y, width, height] = frame.rectangles[0];
  const sx = width / 200,
    sy = height / 200;
  const expected = [...obstacles, ...targets];
  assert.equal(frame.rectangles.length, expected.length + 1);
  expected.forEach((rect, i) => {
    const actual = frame.rectangles[i + 1];
    [
      x + rect.x * sx,
      y + rect.y * sy,
      rect.width * sx,
      rect.height * sy,
    ].forEach((value, axis) => close(actual[axis], value));
  });
  [
    [14, 100],
    [170, 80],
  ].forEach(([wx, wy], i) => {
    close(frame.circles[i][0], x + wx * sx);
    close(frame.circles[i][1], y + wy * sy);
  });
  return { x, y, sx, sy };
}

test("zoom buttons and restored transforms keep agents, both obstacles and both shelters aligned", (t) => {
  const s = setup(t),
    originalAgents = s.agents.slice();
  const initial = geometry(s.render(), s.obstacles, s.targets);
  for (const factor of [1.25, 1.25, 0.8, 0.4]) {
    const before = geometry(s.render(), s.obstacles, s.targets);
    s.simulator.zoom(factor);
    const after = geometry(s.render(), s.obstacles, s.targets);
    close(after.sx / before.sx, factor);
    close((400 - before.x) / before.sx, (400 - after.x) / after.sx);
    close((250 - before.y) / before.sy, (250 - after.y) / after.sy);
  }
  s.simulator.setTransform({ scale: 2, offsetX: 7, offsetY: 11 });
  assert.deepEqual(geometry(s.render(), s.obstacles, s.targets), {
    x: 7,
    y: 11,
    sx: 2,
    sy: 2,
  });
  s.simulator.resetView();
  assert.deepEqual(geometry(s.render(), s.obstacles, s.targets), initial);
  assert.deepEqual(s.agents, originalAgents);
});

test("wheel zoom stays anchored under the cursor on a CSS-resized canvas; panning uses backing pixels", (t) => {
  const s = setup(t);
  const before = geometry(s.render(), s.obstacles, s.targets);
  let prevented = false;
  // CSS (610, 420) maps to backing-canvas (300, 200).
  s.handlers.get("wheel")({
    clientX: 610,
    clientY: 420,
    deltaY: -100,
    preventDefault: () => {
      prevented = true;
    },
  });
  const after = geometry(s.render(), s.obstacles, s.targets);
  assert.ok(prevented);
  close(after.sx / before.sx, 1.15);
  close((300 - before.x) / before.sx, (300 - after.x) / after.sx);
  close((200 - before.y) / before.sy, (200 - after.y) / after.sy);
  s.handlers.get("pointerdown")({ clientX: 610, clientY: 420 });
  s.windowHandlers.get("pointermove")({ clientX: 650, clientY: 440 });
  s.windowHandlers.get("pointerup")();
  const panned = geometry(s.render(), s.obstacles, s.targets);
  close(panned.x - after.x, 20);
  close(panned.y - after.y, 10);
  s.handlers.get("dblclick")();
  assert.deepEqual(geometry(s.render(), s.obstacles, s.targets), before);
});

test("the fallback evacuation zone and agent radii follow zoom", (t) => {
  const s = setup(t);
  const exit = { x: 180, y: 80, width: 16, height: 40 };
  s.simulator.setWorldConfig({ targets: [], exitZone: exit });
  const beforeFrame = s.render();
  const before = geometry(beforeFrame, s.obstacles, [exit]);
  s.simulator.zoom(1.25);
  const afterFrame = s.render();
  const after = geometry(afterFrame, s.obstacles, [exit]);
  close(after.sx / before.sx, 1.25);
  close(afterFrame.circles[0][2] / beforeFrame.circles[0][2], 1.25);
});

for (const mapMode of [false, true]) {
  test(`${mapMode ? "map" : "demo"} agents keep shrinking at distant zoom levels`, (t) => {
    const s = setup(t, mapMode);
    // An individual pedestrian and one larger cluster representative.
    s.agents[4] = 0.35;
    s.agents[9] = 0.7;
    s.agents[1] = 10;
    s.agents[5] = 17;
    s.agents[6] = 8;
    const originalAgents = s.agents.slice();
    const setScale = (value) => {
      if (mapMode) {
        s.simulator.setWorldConfig({
          originLat: 50.07,
          originLng: 19.9,
          minLat: 50.05,
          maxLng: 19.92,
        });
        s.setMapProjectionScale(value * 10000);
      } else {
        s.simulator.setTransform({ scale: value, offsetX: 100, offsetY: 100 });
      }
    };
    let previousRadius = Infinity;
    for (const scale of [4, 1, 0.25, 0.0625, 0.02]) {
      setScale(scale);
      const frame = s.render();
      assert.equal(frame.circles.length, 2);
      const radius = frame.circles[0][2];
      assert.ok(radius > 0 && radius < previousRadius);
      if (scale <= 1)
        assert.ok(
          radius < 1,
          "Overview dots must be smaller than one pixel in radius",
        );
      previousRadius = radius;
    }
    // Physical sizes still take over at close zoom, including larger clusters.
    setScale(20);
    const closeFrame = s.render();
    close(closeFrame.circles[1][2] / closeFrame.circles[0][2], 2);
    assert.deepEqual(s.agents, originalAgents);
  });
}

test("cluster outlines do not inflate tiny overview dots", (t) => {
  const s = setup(t);
  s.simulator.setTransform({ scale: 0.1, offsetX: 100, offsetY: 100 });
  s.ctx.calls.length = 0;
  s.simulator.renderBinary(s.agents, 2, "agents", false, 2.5, 4, false);
  assert.equal(s.ctx.calls.filter((call) => call.method === "arc").length, 2);
  s.simulator.setTransform({ scale: 4, offsetX: 0, offsetY: 0 });
  s.ctx.calls.length = 0;
  s.simulator.renderBinary(s.agents, 2, "agents", false, 2.5, 4, false);
  assert.equal(s.ctx.calls.filter((call) => call.method === "arc").length, 4);
});
