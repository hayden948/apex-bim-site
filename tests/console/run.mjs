// Starts the mock, runs the browser test, tears down. Exit code = test result.
import { spawn } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const mock = spawn("node", [path.join(here, "mock-api.mjs")], { stdio: "inherit" });
await new Promise((r) => setTimeout(r, 700));
const test = spawn("node", [path.join(here, "test-console.mjs")], { stdio: "inherit", env: process.env });
test.on("exit", (code) => {
  mock.kill();
  process.exit(code ?? 1);
});
