import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { createServer } from "node:http";
import { mkdirSync, mkdtempSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

test(
  "GPU map collisions reject embedded centers and movement across thin buildings",
  {
    skip: !process.env.PLANSAFE_PLAYWRIGHT_MODULE,
  },
  async () => {
    const root = fileURLToPath(new URL("../", import.meta.url));
    const output = join(root, ".run");
    mkdirSync(output, { recursive: true });
    const reference = join(
      mkdtempSync(join(output, "map-collision-")),
      "reference.json",
    );
    const run = spawnSync(
      "dotnet",
      [
        "test",
        "tests/PlanSafe.Tests",
        "--no-restore",
        "--filter",
        "FullyQualifiedName~ContinuousMapCollisionTests",
        "--verbosity",
        "quiet",
      ],
      {
        cwd: root,
        env: { ...process.env, PLANSAFE_MAP_COLLISION_REFERENCE: reference },
        encoding: "utf8",
      },
    );
    assert.equal(run.status, 0, run.stdout + run.stderr);
    const fixtures = JSON.parse(readFileSync(reference, "utf8"));
    const assets = new URL("../src/PlanSafe.App/wwwroot/js/", import.meta.url);
    const server = createServer((req, res) => {
      if (req.url === "/") return res.end("<!doctype html>");
      if (!/^\/[a-zA-Z]+\.js$/.test(req.url ?? ""))
        return res.writeHead(404).end();
      res.setHeader("Content-Type", "text/javascript");
      res.end(readFileSync(new URL(req.url.slice(1), assets)));
    });
    let browser;
    try {
      await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
      const { chromium } = await import(process.env.PLANSAFE_PLAYWRIGHT_MODULE);
      browser = await chromium.launch({
        headless: true,
        executablePath: process.env.PLANSAFE_BROWSER_PATH,
        args: [
          "--enable-unsafe-webgpu",
          "--disable-dawn-features=disallow_unsafe_apis",
        ],
      });
      const page = await browser.newPage();
      const errors = [];
      page.on("pageerror", (error) => errors.push(error.message));
      await page.goto(`http://127.0.0.1:${server.address().port}`);
      const result = await page.evaluate(async (fixtures) => {
        const { GpuSimulationEngine, parseMapScenario } =
          await import("/crowdSimulatorGpu.js");
        const engine = new GpuSimulationEngine();
        await engine.boot();
        const errors = [];
        engine.device.addEventListener("uncapturederror", (event) =>
          errors.push(event.error.message),
        );
        const failures = [];
        for (const fixture of fixtures) {
          const snapshot = Object.fromEntries(
            Object.entries(fixture.snapshot).map(([key, value]) => [
              key,
              ["scenario", "agents", "fields", "blocked"].includes(key)
                ? Uint8Array.from(atob(value), (char) => char.charCodeAt(0))
                : value,
            ]),
          );
          engine.initializeMap(snapshot);
          const map = parseMapScenario(snapshot.scenario);
          for (let tick = 0; tick < 100; tick++) {
            await engine.advanceFixedTicks(1);
            const preview = await engine.capturePreview();
            const x = preview.values[0],
              y = preview.values[preview.count],
              radius = preview.values[preview.count * 4];
            if (!fixture.embedded && x >= 30 && y > 5 && y < 35)
              failures.push(
                `${fixture.name}: crossed wall at tick ${tick}: (${x},${y})`,
              );
            for (
              let row = Math.max(0, Math.floor((y - radius) / map.cellSize));
              row <=
              Math.min(map.rows - 1, Math.floor((y + radius) / map.cellSize));
              row++
            ) {
              for (
                let col = Math.max(0, Math.floor((x - radius) / map.cellSize));
                col <=
                Math.min(
                  map.columns - 1,
                  Math.floor((x + radius) / map.cellSize),
                );
                col++
              ) {
                if (!map.blocked[row * map.columns + col]) continue;
                const dx = Math.max(
                  col * map.cellSize - x,
                  x - (col + 1) * map.cellSize,
                  0,
                );
                const dy = Math.max(
                  row * map.cellSize - y,
                  y - (row + 1) * map.cellSize,
                  0,
                );
                if (Math.hypot(dx, dy) < radius - 0.00001)
                  failures.push(
                    `${fixture.name}: overlaps blocked cell at tick ${tick}: (${x},${y}), cell (${col},${row})`,
                  );
              }
            }
            if (failures.length) break;
          }
        }
        engine.dispose();
        return { errors, failures };
      }, fixtures);
      assert.deepEqual(errors, []);
      assert.deepEqual(result.errors, []);
      assert.deepEqual(result.failures, []);
    } finally {
      await browser?.close();
      if (server.listening)
        await new Promise((resolve) => server.close(resolve));
    }
  },
);
