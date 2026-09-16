import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const errors = [];

function read(relativePath) {
  return fs.readFileSync(path.join(repoRoot, relativePath), "utf8");
}

function requireMatch(content, pattern, message) {
  if (!pattern.test(content)) {
    errors.push(message);
  }
}

const layout = read("web/src/lib/docs-layout.ts");
const docsLayout = read("web/src/app/docs/layout.tsx");
const css = read("web/src/app/globals.css");

requireMatch(
  layout,
  /layoutWidth:\s*"100%"/,
  "web/src/lib/docs-layout.ts: docs chrome must use layoutWidth 100% so the shell can spread across the viewport.",
);
requireMatch(
  layout,
  /contentWidth:\s*"100%"/,
  "web/src/lib/docs-layout.ts: contentWidth must be 100% so article/home content can fill the main column.",
);
requireMatch(
  layout,
  /sidebarWidth:\s*"17\.5rem"/,
  "web/src/lib/docs-layout.ts: sidebarWidth must stay 17.5rem for a stable reading spread.",
);
requireMatch(
  layout,
  /tocWidth:\s*"15rem"/,
  "web/src/lib/docs-layout.ts: tocWidth must stay 15rem so the right rail sits beside the article.",
);
requireMatch(
  layout,
  /radius:\s*"md"/,
  "web/src/lib/docs-layout.ts: shape.radius must be md for square-er docs surfaces.",
);
requireMatch(
  docsLayout,
  /from "@\/lib\/docs-layout"/,
  "web/src/app/docs/layout.tsx: must apply the shared docsTheme from web/src/lib/docs-layout.ts.",
);
requireMatch(
  docsLayout,
  /theme=\{docsTheme\}/,
  "web/src/app/docs/layout.tsx: DocsLayout must receive theme={docsTheme}.",
);
requireMatch(
  docsLayout,
  /<Header fullBleed/,
  "web/src/app/docs/layout.tsx: docs header must be full-bleed so it shares gutters with the sidebar.",
);
requireMatch(
  docsLayout,
  /<DocsMobileChrome/,
  "web/src/app/docs/layout.tsx: must mount DocsMobileChrome so narrow docs chrome can label Docs and dock search.",
);
requireMatch(
  css,
  /@media \(max-width: 1023px\)/,
  "web/src/app/globals.css: narrow docs chrome must live in a max-width 1023px query so desktop layout stays unchanged.",
);
requireMatch(
  css,
  /\.sqlos-docs-shell \.emcydocs-mobile-toc,[\s\S]*?display:\s*none;/,
  "web/src/app/globals.css: the in-article On this page card must be hidden on narrow viewports.",
);
requireMatch(
  css,
  /content:\s*"Docs";/,
  "web/src/app/globals.css: the embedded nav toggle must read Docs on narrow viewports.",
);
requireMatch(
  css,
  /emcydocs-search-dialog:modal \{[\s\S]*?inset:\s*var\(--sqlos-docs-search-dock-top/,
  "web/src/app/globals.css: mobile search must dock under the docs bar instead of centering as a modal.",
);
requireMatch(
  css,
  /\.sqlos-docs-shell \.emcydocs-embedded-nav-panel \{[\s\S]*?padding:\s*0\.75rem 0 1\.25rem;/,
  "web/src/app/globals.css: the mobile Docs panel must use the shared left gutter instead of centering search and nav.",
);
requireMatch(
  css,
  /\.sqlos-docs-shell \.emcydocs-article,[\s\S]*?\.sqlos-docs-shell \.emcydocs-home-content \{[\s\S]*?max-width:\s*none;/,
  "web/src/app/globals.css: article and docs-home content must drop the 48rem island (max-width: none).",
);
requireMatch(
  css,
  /\.sqlos-docs-shell::before \{\s*\n  display: none;/,
  "web/src/app/globals.css: must disable the decorative EmcyDocs grid behind the docs shell.",
);
requireMatch(
  css,
  /--sqlos-docs-gutter:\s*2rem;/,
  "web/src/app/globals.css: docs gutters must be locked to a 2rem professional margin.",
);
requireMatch(
  css,
  /--sqlos-docs-radius:\s*0\.5rem;/,
  "web/src/app/globals.css: docs surfaces must use a 0.5rem square radius.",
);

if (errors.length > 0) {
  console.error("Docs layout contract failed:\n");
  for (const error of errors) {
    console.error(`- ${error}`);
  }
  process.exit(1);
}

console.log("Validated public docs reading-layout contract.");
