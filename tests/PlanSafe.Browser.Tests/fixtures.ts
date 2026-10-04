import { test as base, type Page } from "@playwright/test";
import { spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../../", import.meta.url));

/** Serves the generated production modules in an isolated page for GPU readback tests. */
export const test = base.extend<{ gpuPage: Page }>({
  gpuPage: async ({ page }, use) => {
    await page.route("**/__gpu_test__", (route) =>
      route.fulfill({
        contentType: "text/html",
        body: '<!doctype html><canvas width="800" height="500"></canvas>',
      }),
    );
    await page.route(/\/([a-zA-Z]+)\.js$/, async (route) => {
      const name = new URL(route.request().url()).pathname.slice(1);
      await route.fulfill({
        contentType: "text/javascript",
        path: join(root, "src/PlanSafe.App/wwwroot/js", name),
      });
    });
    await page.goto("/__gpu_test__");
    await use(page);
  },
});

/** Runs a C# reference exporter and removes its temporary output, including on failure. */
export function readReference<T>(testClass: string, variable: string): T {
  const temp = mkdtempSync(join(tmpdir(), "plansafe-browser-"));
  const destination = join(temp, "reference.json");
  try {
    const result = spawnSync(
      "dotnet",
      [
        "test",
        "tests/PlanSafe.Tests",
        "--no-build",
        "--no-restore",
        "--filter",
        `FullyQualifiedName~${testClass}`,
        "--verbosity",
        "quiet",
      ],
      {
        cwd: root,
        env: { ...process.env, [variable]: destination },
        encoding: "utf8",
        timeout: 60_000,
      },
    );
    if (result.error || result.status !== 0)
      throw new Error(
        `C# reference export failed: ${result.error?.message ?? ""}\n${result.stdout}${result.stderr}`,
      );
    return JSON.parse(readFileSync(destination, "utf8")) as T;
  } finally {
    rmSync(temp, { recursive: true, force: true });
  }
}
