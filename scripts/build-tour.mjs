import { copyFile, mkdir } from "node:fs/promises";
const target = new URL(
  "../src/PlanSafe.App/wwwroot/lib/driver/",
  import.meta.url,
);
await mkdir(target, { recursive: true });
for (const [source, name] of [
  ["dist/driver.js.mjs", "driver.js"],
  ["dist/driver.css", "driver.css"],
  ["LICENSE", "LICENSE"],
]) {
  await copyFile(
    new URL(`../node_modules/driver.js/${source}`, import.meta.url),
    new URL(name, target),
  );
}
