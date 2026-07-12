import crypto from "crypto";
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";

const scriptPath = fileURLToPath(import.meta.url);
const repoRoot = path.resolve(path.dirname(scriptPath), "..");
const docsImageRoot = path.join(repoRoot, "web", "public", "docs");
const contentRoots = [
  path.join(repoRoot, "web", "content"),
  path.join(repoRoot, "web", "src"),
];
const textExtensions = new Set([
  ".css",
  ".js",
  ".jsx",
  ".md",
  ".mdx",
  ".ts",
  ".tsx",
]);
const supportedImageExtensions = new Set([".jpg", ".jpeg", ".png", ".svg", ".webp"]);
const maximumImageBytes = 1_500_000;

function walkFiles(rootDirectory, predicate) {
  const files = [];

  function walk(currentDirectory) {
    for (const entry of fs.readdirSync(currentDirectory, { withFileTypes: true })) {
      const fullPath = path.join(currentDirectory, entry.name);
      if (entry.isDirectory()) {
        walk(fullPath);
      } else if (predicate(fullPath)) {
        files.push(fullPath);
      }
    }
  }

  walk(rootDirectory);
  return files.sort();
}

function detectImageType(buffer) {
  if (buffer.subarray(0, 8).equals(Buffer.from("89504e470d0a1a0a", "hex"))) {
    return ".png";
  }

  if (buffer.subarray(0, 3).equals(Buffer.from("ffd8ff", "hex"))) {
    return ".jpg";
  }

  if (
    buffer.subarray(0, 4).toString("ascii") === "RIFF" &&
    buffer.subarray(8, 12).toString("ascii") === "WEBP"
  ) {
    return ".webp";
  }

  const textPrefix = buffer.subarray(0, 1024).toString("utf8").trimStart();
  if (textPrefix.startsWith("<svg") || /^<\?xml[^>]*>\s*<svg/s.test(textPrefix)) {
    return ".svg";
  }

  return undefined;
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

const errors = [];
const sourceFiles = contentRoots.flatMap((root) =>
  walkFiles(root, (filePath) => textExtensions.has(path.extname(filePath).toLowerCase())),
);
const sourceContents = sourceFiles.map((filePath) => fs.readFileSync(filePath, "utf8")).join("\n");
const imageFiles = walkFiles(docsImageRoot, (filePath) =>
  supportedImageExtensions.has(path.extname(filePath).toLowerCase()),
);
const hashes = new Map();

for (const imagePath of imageFiles) {
  const fileName = path.basename(imagePath);
  const extension = path.extname(fileName).toLowerCase();
  const buffer = fs.readFileSync(imagePath);
  const detectedType = detectImageType(buffer);
  const normalizedExtension = extension === ".jpeg" ? ".jpg" : extension;

  if (!detectedType) {
    errors.push(`${fileName}: unsupported or unreadable image payload.`);
  } else if (detectedType !== normalizedExtension) {
    errors.push(
      `${fileName}: file extension says '${extension}' but the payload is '${detectedType}'.`,
    );
  }

  if (buffer.length > maximumImageBytes) {
    errors.push(
      `${fileName}: ${buffer.length.toLocaleString()} bytes exceeds the ${maximumImageBytes.toLocaleString()}-byte docs image budget.`,
    );
  }

  const publicPath = `/docs/${fileName}`;
  if (!sourceContents.includes(publicPath)) {
    errors.push(`${fileName}: orphaned asset; no web content or component references '${publicPath}'.`);
  }

  const markdownImagePattern = new RegExp(`!\\[([^\\]]*)\\]\\(${escapeRegExp(publicPath)}(?:[?#][^)]*)?\\)`, "g");
  for (const match of sourceContents.matchAll(markdownImagePattern)) {
    if (!match[1].trim()) {
      errors.push(`${fileName}: Markdown image reference has empty alt text.`);
    }
  }

  const hash = crypto.createHash("sha256").update(buffer).digest("hex");
  const duplicate = hashes.get(hash);
  if (duplicate) {
    errors.push(`${fileName}: byte-for-byte duplicate of '${duplicate}'.`);
  } else {
    hashes.set(hash, fileName);
  }
}

if (errors.length > 0) {
  console.error("Documentation image validation failed:\n");
  for (const error of errors) {
    console.error(`- ${error}`);
  }
  process.exit(1);
}

console.log(
  `Validated ${imageFiles.length} documentation images: referenced, unique, correctly encoded, and within budget.`,
);
