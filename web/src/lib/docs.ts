import fs from "fs";
import path from "path";
import matter from "gray-matter";

const guidesDirectory = path.join(process.cwd(), "content/docs");

export type DocSection = "authserver" | "fga" | "reference" | null;

const DOC_SECTION_LABELS: Record<string, string> = {
  "": "Getting Started",
  authserver: "AuthServer",
  fga: "Fine-Grained Auth",
  reference: "Reference",
};

export interface DocGuide {
  slug: string;
  title: string;
  description: string;
  order: number;
  section: DocSection;
  content: string;
}

export interface DocsSearchResult {
  slug: string;
  href: string;
  title: string;
  description: string;
  section: DocSection;
  sectionLabel: string;
  snippet: string;
  matchedFields: ("title" | "description" | "content")[];
}

export interface DocsSearchResponse {
  query: string;
  total: number;
  results: DocsSearchResult[];
}

export function getDocSectionLabel(section: DocSection): string {
  return DOC_SECTION_LABELS[section ?? ""] ?? "Documentation";
}

function findMdxFiles(dir: string, baseDir: string = dir): string[] {
  if (!fs.existsSync(dir)) return [];
  const entries = fs.readdirSync(dir, { withFileTypes: true });
  const files: string[] = [];
  for (const entry of entries) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      files.push(...findMdxFiles(fullPath, baseDir));
    } else if (entry.name.endsWith(".mdx")) {
      const relativePath = path.relative(baseDir, fullPath);
      files.push(path.join(baseDir, relativePath));
    }
  }
  return files;
}

export function getAllGuides(): DocGuide[] {
  if (!fs.existsSync(guidesDirectory)) {
    return [];
  }

  const filePaths = findMdxFiles(guidesDirectory);
  const guides = filePaths
    .map((fullPath) => {
      const relativePath = path.relative(guidesDirectory, fullPath);
      const slug = relativePath.replace(/\.mdx$/, "").replace(/\\/g, "/");
      const fileContents = fs.readFileSync(fullPath, "utf8");
      const { data, content } = matter(fileContents);

      return {
        slug,
        title: data.title || slug,
        description: data.description || "",
        order: typeof data.order === "number" ? data.order : 999,
        section: (data.section as DocSection) ?? null,
        content,
      };
    })
    .sort((a, b) => a.order - b.order);

  return guides;
}

export function getGuideBySlug(slug: string): DocGuide | null {
  const fullPath = path.join(guidesDirectory, `${slug}.mdx`);

  if (!fs.existsSync(fullPath)) {
    return null;
  }

  const fileContents = fs.readFileSync(fullPath, "utf8");
  const { data, content } = matter(fileContents);

  return {
    slug,
    title: data.title || slug,
    description: data.description || "",
    order: typeof data.order === "number" ? data.order : 999,
    section: (data.section as DocSection) ?? null,
    content,
  };
}
