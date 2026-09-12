import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const docsRoot = path.join(repoRoot, "web/content/docs");
const pages = JSON.parse(fs.readFileSync(path.join(repoRoot, "web/docs-nav-pages.json"), "utf8"));

function walk(dir) {
  const out = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) out.push(...walk(full));
    else if (entry.name.endsWith(".mdx")) out.push(full);
  }
  return out;
}

function upsertFrontmatter(content, fields) {
  const match = content.match(/^---\n([\s\S]*?)\n---\n/);
  if (!match) {
    throw new Error("missing frontmatter");
  }

  const kept = match[1]
    .split("\n")
    .filter((line) => !/^(section|sectionLabel|group|groupLabel|order|sidebar):/.test(line))
    .join("\n")
    .replace(/\n+$/, "");

  const extra = [`section: "${fields.section}"`];
  if (fields.group) extra.push(`group: "${fields.group}"`);
  extra.push(`order: ${fields.order}`);
  if (fields.sidebar === false) extra.push("sidebar: false");

  return `---\n${kept}\n${extra.join("\n")}\n---\n${content.slice(match[0].length)}`;
}

const redirects = [
  { source: "/docs/guides", destination: "/docs/getting-started", permanent: true },
  { source: "/docs/authserver", destination: "/docs/getting-started", permanent: true },
  { source: "/docs/quickstarts", destination: "/docs/quickstarts/add-to-app", permanent: true },
];
const replacements = [];
const mapped = new Set();

for (const [slug, spec] of Object.entries(pages)) {
  mapped.add(slug);
  const filePath = path.join(docsRoot, `${slug}.mdx`);
  if (!fs.existsSync(filePath)) {
    throw new Error(`missing ${slug}.mdx`);
  }

  if (spec.redirect) {
    redirects.push({
      source: `/docs/${slug}`,
      destination: spec.redirect,
      permanent: true,
    });
    replacements.push({ from: `/docs/${slug}`, to: spec.redirect });
    if (fs.existsSync(filePath)) {
      fs.unlinkSync(filePath);
    }
    continue;
  }

  const next = upsertFrontmatter(fs.readFileSync(filePath, "utf8"), spec);
  fs.writeFileSync(filePath, next);
}

const unmapped = walk(docsRoot)
  .map((file) => path.relative(docsRoot, file).replace(/\.mdx$/, "").replaceAll("\\", "/"))
  .filter((slug) => slug !== "docs-index" && !mapped.has(slug));

if (unmapped.length > 0) {
  throw new Error(`unmapped docs:\n${unmapped.join("\n")}`);
}

replacements.sort((left, right) => right.from.length - left.from.length);

function rewrite(content) {
  let next = content;
  for (const { from, to } of replacements) {
    next = next.split(from).join(to);
  }
  return next;
}

function walkRepo(dir, files = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if ([".git", "node_modules", "bin", "obj", ".next"].includes(entry.name)) continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walkRepo(full, files);
    else if (/\.(md|mdx|ts|tsx)$/.test(entry.name)) files.push(full);
  }
  return files;
}

for (const file of walkRepo(repoRoot)) {
  const original = fs.readFileSync(file, "utf8");
  const next = rewrite(original);
  if (next !== original) fs.writeFileSync(file, next);
}

fs.writeFileSync(
  path.join(repoRoot, "web/docs-redirects.json"),
  `${JSON.stringify(redirects, null, 2)}\n`
);

console.log(`Applied nav to ${mapped.size} pages, ${redirects.length} redirects.`);
