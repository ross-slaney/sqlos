import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const webRoot = path.join(repoRoot, "web");
const port = Number.parseInt(process.env.SQLOS_DOCS_RUNTIME_PORT ?? "3012", 10);
const origin = `http://127.0.0.1:${port}`;

function findSearchDocsActionId(manifest) {
  const runtimes = [manifest?.node, manifest?.edge];
  for (const runtime of runtimes) {
    if (!runtime || typeof runtime !== "object") {
      continue;
    }

    for (const [id, entry] of Object.entries(runtime)) {
      const serialized = JSON.stringify(entry);
      if (serialized.includes("searchDocsAction")) {
        return id;
      }
    }
  }

  return null;
}

async function waitForReady(url, timeoutMs) {
  const started = Date.now();
  let lastError = "";
  while (Date.now() - started < timeoutMs) {
    try {
      const response = await fetch(url, { redirect: "manual" });
      if (response.status < 500) {
        return;
      }
      lastError = `HTTP ${response.status}`;
    } catch (error) {
      lastError = error.message;
    }
    await new Promise((resolve) => setTimeout(resolve, 250));
  }

  throw new Error(`Timed out waiting for ${url} (${lastError})`);
}

async function postServerAction(actionId, args) {
  const response = await fetch(`${origin}/docs`, {
    method: "POST",
    headers: {
      accept: "text/x-component",
      "content-type": "text/plain;charset=UTF-8",
      origin,
      "next-action": actionId,
      rsc: "1",
      "next-router-state-tree": JSON.stringify([
        "",
        { children: ["docs", { children: ["__PAGE__", {}, null, null] }, null, null] },
        null,
        null,
      ]),
    },
    body: JSON.stringify(args),
  });

  const body = await response.text();
  return { response, body };
}

function startProductionServer() {
  const child = spawn("npm", ["run", "start"], {
    cwd: webRoot,
    env: {
      ...process.env,
      NODE_ENV: "production",
      PORT: String(port),
      HOSTNAME: "127.0.0.1",
      NEXT_TELEMETRY_DISABLED: "1",
    },
    stdio: ["ignore", "pipe", "pipe"],
  });

  let output = "";
  const append = (chunk) => {
    output += chunk.toString();
  };
  child.stdout?.on("data", append);
  child.stderr?.on("data", append);

  return {
    child,
    output: () => output,
    async stop() {
      if (child.exitCode !== null || child.signalCode) {
        return;
      }

      await new Promise((resolve) => {
        const timer = setTimeout(() => {
          child.kill("SIGKILL");
        }, 3_000);
        child.once("exit", () => {
          clearTimeout(timer);
          resolve();
        });
        child.kill("SIGTERM");
      });
    },
  };
}

const nextDir = path.join(webRoot, ".next");
if (!fs.existsSync(path.join(nextDir, "BUILD_ID"))) {
  throw new Error("validate-docs-production-start requires a production Next.js build in web/.next.");
}

const manifestPath = path.join(nextDir, "server/server-reference-manifest.json");
if (!fs.existsSync(manifestPath)) {
  throw new Error(`Missing Server Action manifest at ${manifestPath}`);
}

const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
const actionId = findSearchDocsActionId(manifest);
if (!actionId) {
  throw new Error("Production build does not register searchDocsAction as a Server Action.");
}

const server = startProductionServer();

try {
  await waitForReady(`${origin}/docs`, 60_000);

  const docsHome = await fetch(`${origin}/docs`);
  const docsHomeHtml = await docsHome.text();
  if (docsHome.status !== 200) {
    throw new Error(`GET /docs returned HTTP ${docsHome.status}`);
  }
  if (!docsHomeHtml.includes("Search docs")) {
    throw new Error("Production /docs did not render the docs search control.");
  }

  const authorization = await postServerAction(actionId, ["authorization"]);
  if (authorization.response.status >= 500) {
    throw new Error(
      `searchDocsAction("authorization") failed with HTTP ${authorization.response.status}: ${authorization.body.slice(0, 500)}`,
    );
  }
  if (
    !/"query"\s*:\s*"authorization"/.test(authorization.body) ||
    !/"results"\s*:/.test(authorization.body)
  ) {
    throw new Error(
      `searchDocsAction("authorization") did not return search results over the Server Action runtime: ${authorization.body.slice(0, 800)}`,
    );
  }
  if (!/\/docs\//.test(authorization.body)) {
    throw new Error("searchDocsAction results did not include /docs routes.");
  }

  const gettingStarted = await postServerAction(actionId, ["getting started"]);
  if (gettingStarted.response.status >= 500) {
    throw new Error(
      `searchDocsAction("getting started") failed with HTTP ${gettingStarted.response.status}: ${gettingStarted.body.slice(0, 500)}`,
    );
  }
  if (!/getting-started/.test(gettingStarted.body)) {
    throw new Error(
      `searchDocsAction("getting started") did not include getting-started: ${gettingStarted.body.slice(0, 800)}`,
    );
  }

  for (const query of ["", "x", "<<<>>>"]) {
    const invalid = await postServerAction(actionId, [query]);
    if (invalid.response.status >= 500) {
      throw new Error(
        `searchDocsAction(${JSON.stringify(query)}) failed unsafely with HTTP ${invalid.response.status}: ${invalid.body.slice(0, 500)}`,
      );
    }
    if (!/"total"\s*:\s*0/.test(invalid.body)) {
      throw new Error(
        `searchDocsAction(${JSON.stringify(query)}) must return zero results, got: ${invalid.body.slice(0, 800)}`,
      );
    }
  }

  console.log("Validated production docs start command and searchDocsAction Server Action.");
} catch (error) {
  const output = server.output().trim();
  if (output) {
    console.error(output);
  }
  throw error;
} finally {
  await server.stop();
}
