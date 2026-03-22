import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";

const scriptPath = fileURLToPath(import.meta.url);
const repoRoot = path.resolve(path.dirname(scriptPath), "..");
const webRoot = path.join(repoRoot, "web");
const webPublicRoot = path.join(webRoot, "public");
const docsContentRoot = path.join(webRoot, "content", "docs");
const blogContentRoot = path.join(webRoot, "content", "blog");

const ignoredDirectories = new Set([
  ".git",
  ".next",
  "bin",
  "node_modules",
  "obj",
]);

const markdownExtensions = new Set([".md", ".mdx"]);
const skippedUrlPrefixes = [
  "data:",
  "http://",
  "https://",
  "mailto:",
  "tel:",
];

function walkFiles(rootDirectory, predicate) {
  const files = [];

  function walk(currentDirectory) {
    const entries = fs.readdirSync(currentDirectory, { withFileTypes: true });
    for (const entry of entries) {
      if (ignoredDirectories.has(entry.name)) {
        continue;
      }

      const fullPath = path.join(currentDirectory, entry.name);
      if (entry.isDirectory()) {
        walk(fullPath);
        continue;
      }

      if (predicate(fullPath)) {
        files.push(fullPath);
      }
    }
  }

  walk(rootDirectory);
  return files.sort();
}

function toPosixPath(filePath) {
  return filePath.replace(/\\/g, "/");
}

function buildDocRoutes() {
  const routes = new Set(["/docs", "/docs/guides"]);
  const files = walkFiles(docsContentRoot, (fullPath) => path.extname(fullPath) === ".mdx");

  for (const fullPath of files) {
    const relativePath = toPosixPath(path.relative(docsContentRoot, fullPath));
    const slug = relativePath.replace(/\.mdx$/, "");
    routes.add(`/docs/guides/${slug}`);
  }

  return routes;
}

function buildBlogRoutes() {
  const routes = new Set(["/blog"]);
  const files = walkFiles(blogContentRoot, (fullPath) => path.extname(fullPath) === ".mdx");

  for (const fullPath of files) {
    const relativePath = toPosixPath(path.relative(blogContentRoot, fullPath));
    const slug = relativePath.replace(/\.mdx$/, "");
    routes.add(`/blog/${slug}`);
  }

  return routes;
}

function buildPublicAssetRoutes() {
  const routes = new Set();
  const files = walkFiles(webPublicRoot, () => true);

  for (const fullPath of files) {
    const relativePath = toPosixPath(path.relative(webPublicRoot, fullPath));
    routes.add(`/${relativePath}`);
  }

  return routes;
}

function extractMarkdownLinks(content) {
  const links = [];
  const markdownLinkPattern = /!?\[[^\]]*]\(([^)]+)\)/g;

  for (const match of content.matchAll(markdownLinkPattern)) {
    let target = match[1].trim();
    if (!target) {
      continue;
    }

    if (target.startsWith("<") && target.endsWith(">")) {
      target = target.slice(1, -1);
    }

    const titleSeparator = target.search(/\s+(?=(["']).*\1$)/);
    if (titleSeparator >= 0) {
      target = target.slice(0, titleSeparator).trim();
    }

    links.push(target);
  }

  return links;
}

function stripHashAndQuery(rawUrl) {
  return rawUrl.split("#", 1)[0].split("?", 1)[0];
}

function isSkippableUrl(rawUrl) {
  if (!rawUrl || rawUrl.startsWith("#")) {
    return true;
  }

  return skippedUrlPrefixes.some((prefix) => rawUrl.startsWith(prefix));
}

function fileExists(fullPath) {
  try {
    return fs.statSync(fullPath).isFile();
  } catch {
    return false;
  }
}

function directoryExists(fullPath) {
  try {
    return fs.statSync(fullPath).isDirectory();
  } catch {
    return false;
  }
}

function resolveRelativeLink(sourceFile, rawUrl) {
  const sourceDirectory = path.dirname(sourceFile);
  const bareTarget = stripHashAndQuery(rawUrl);
  const absoluteTarget = path.resolve(sourceDirectory, bareTarget);

  const candidates = [
    absoluteTarget,
    `${absoluteTarget}.md`,
    `${absoluteTarget}.mdx`,
  ];

  for (const candidate of candidates) {
    if (fileExists(candidate)) {
      return true;
    }
  }

  if (directoryExists(absoluteTarget)) {
    const directoryCandidates = [
      path.join(absoluteTarget, "README.md"),
      path.join(absoluteTarget, "README.mdx"),
      path.join(absoluteTarget, "index.md"),
      path.join(absoluteTarget, "index.mdx"),
    ];

    return directoryCandidates.some(fileExists);
  }

  return false;
}

const docRoutes = buildDocRoutes();
const blogRoutes = buildBlogRoutes();
const publicAssetRoutes = buildPublicAssetRoutes();
const markdownFiles = walkFiles(repoRoot, (fullPath) => markdownExtensions.has(path.extname(fullPath)));
const errors = [];

for (const filePath of markdownFiles) {
  const fileContents = fs.readFileSync(filePath, "utf8");
  const links = extractMarkdownLinks(fileContents);

  for (const rawUrl of links) {
    if (isSkippableUrl(rawUrl)) {
      continue;
    }

    const normalizedUrl = stripHashAndQuery(rawUrl);

    if (!normalizedUrl) {
      continue;
    }

    if (normalizedUrl.startsWith("/")) {
      const isKnownRoute =
        docRoutes.has(normalizedUrl) ||
        blogRoutes.has(normalizedUrl) ||
        publicAssetRoutes.has(normalizedUrl);

      if (!isKnownRoute) {
        errors.push(`${toPosixPath(path.relative(repoRoot, filePath))}: broken site link '${rawUrl}'`);
      }

      continue;
    }

    if (!resolveRelativeLink(filePath, rawUrl)) {
      errors.push(`${toPosixPath(path.relative(repoRoot, filePath))}: broken relative link '${rawUrl}'`);
    }
  }
}

if (errors.length > 0) {
  console.error("Docs link validation failed:\n");
  for (const error of errors) {
    console.error(`- ${error}`);
  }
  process.exit(1);
}

console.log(`Validated ${markdownFiles.length} markdown files with no broken local links.`);
