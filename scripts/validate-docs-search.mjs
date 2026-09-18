import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const actions = fs.readFileSync(path.join(repoRoot, "web/src/app/docs/actions.ts"), "utf8");
const errors = [];

function requireMatch(content, pattern, message) {
  if (!pattern.test(content)) {
    errors.push(message);
  }
}

requireMatch(
  actions,
  /^"use server";/m,
  "web/src/app/docs/actions.ts must remain a Server Action module.",
);
requireMatch(
  actions,
  /export async function searchDocsAction\(query: string\)/,
  "web/src/app/docs/actions.ts must export searchDocsAction(query: string).",
);
requireMatch(
  actions,
  /return docsSource\.search\(query\)/,
  "searchDocsAction must keep delegating to docsSource.search so docs search stays on the shared source.",
);

if (errors.length > 0) {
  console.error("Docs search contract failed:\n");
  for (const error of errors) {
    console.error(`- ${error}`);
  }
  process.exit(1);
}

console.log("Validated searchDocsAction source contract.");
