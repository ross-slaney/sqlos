import fs from "fs";
import path from "path";
import matter from "gray-matter";

const postsDirectory = path.join(process.cwd(), "content/blog");

export interface BlogPost {
  slug: string;
  title: string;
  description: string;
  date: string;
  author: string;
  tags: string[];
  content: string;
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

export function getAllPosts(): BlogPost[] {
  if (!fs.existsSync(postsDirectory)) {
    return [];
  }

  const filePaths = findMdxFiles(postsDirectory);
  const posts = filePaths
    .map((fullPath) => {
      const relativePath = path.relative(postsDirectory, fullPath);
      const slug = relativePath.replace(/\.mdx$/, "").replace(/\\/g, "/");
      const fileContents = fs.readFileSync(fullPath, "utf8");
      const { data, content } = matter(fileContents);

      return {
        slug,
        title: data.title || slug,
        description: data.description || "",
        date: data.date || new Date().toISOString(),
        author: data.author || "Sqlzibar Team",
        tags: data.tags || [],
        content,
      };
    })
    .sort((a, b) => (new Date(b.date) > new Date(a.date) ? 1 : -1));

  return posts;
}

const DEFAULT_PAGE_SIZE = 5;

export interface PaginatedPosts {
  posts: BlogPost[];
  total: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

export function getPaginatedPosts(
  page: number = 1,
  pageSize: number = DEFAULT_PAGE_SIZE
): PaginatedPosts {
  const allPosts = getAllPosts();
  const total = allPosts.length;
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const safePage = Math.max(1, Math.min(page, totalPages));
  const start = (safePage - 1) * pageSize;
  const posts = allPosts.slice(start, start + pageSize);

  return {
    posts,
    total,
    page: safePage,
    pageSize,
    totalPages,
  };
}

export function getPostBySlug(slug: string): BlogPost | null {
  const fullPath = path.join(postsDirectory, `${slug}.mdx`);

  if (!fs.existsSync(fullPath)) {
    return null;
  }

  const fileContents = fs.readFileSync(fullPath, "utf8");
  const { data, content } = matter(fileContents);

  return {
    slug,
    title: data.title || slug,
    description: data.description || "",
    date: data.date || new Date().toISOString(),
    author: data.author || "Sqlzibar Team",
    tags: data.tags || [],
    content,
  };
}
