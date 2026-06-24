import Link from "next/link";
import { Chip } from "@heroui/react";
import { getPaginatedPosts } from "@/lib/blog";

export const metadata = {
  title: "Blog - SqlOS",
  description:
    "Notes on auth, hierarchical authorization, EF Core, and SQL-backed application security.",
};

const PAGE_SIZE = 5;

interface BlogPageProps {
  searchParams: Promise<{ page?: string }>;
}

export default async function BlogPage({ searchParams }: BlogPageProps) {
  const { page: pageParam } = await searchParams;
  const page = Math.max(1, parseInt(pageParam ?? "1", 10) || 1);
  const {
    posts,
    total,
    page: currentPage,
    totalPages,
  } = getPaginatedPosts(page, PAGE_SIZE);

  const prevPage = currentPage > 1 ? currentPage - 1 : null;
  const nextPage = currentPage < totalPages ? currentPage + 1 : null;

  return (
    <div className="mx-4 max-w-[360px] overflow-hidden py-16 sm:mx-auto sm:max-w-[1400px] sm:px-6">
      <Chip
        size="sm"
        variant="soft"
        color="accent"
        className="border border-neon-cyan/30 bg-neon-cyan/10 text-neon-cyan"
      >
        SqlOS dispatches
      </Chip>
      <h1 className="mt-5 text-4xl font-bold text-foreground sm:text-5xl">Blog</h1>
      <p className="mt-4 max-w-2xl text-lg leading-8 text-muted-foreground">
        Notes on OAuth, hosted auth, hierarchical authorization, EF Core, and SQL-backed
        application security.
      </p>

      <nav
        className="mt-8 flex flex-col gap-4 border-b border-border/70 pb-6 sm:flex-row sm:items-center sm:justify-between"
        aria-label="Blog pagination"
      >
        <p className="text-sm text-muted-foreground">
          {total === 0
            ? "No posts"
            : `Showing ${(currentPage - 1) * PAGE_SIZE + 1}–${Math.min(currentPage * PAGE_SIZE, total)} of ${total} posts`}
        </p>
        <div className="flex flex-wrap items-center gap-2">
          {prevPage ? (
            <Link
              href={prevPage === 1 ? "/blog" : `/blog?page=${prevPage}`}
              className="rounded-md border border-neon-cyan/35 px-4 py-2 text-sm font-medium text-neon-cyan transition-colors hover:bg-neon-cyan/10"
            >
              &larr; Previous
            </Link>
          ) : (
            <span className="cursor-not-allowed rounded-md border border-border px-4 py-2 text-sm font-medium text-muted-foreground opacity-50">
              &larr; Previous
            </span>
          )}
          <span className="text-sm text-muted-foreground">
            Page {currentPage} of {totalPages}
          </span>
          {nextPage ? (
            <Link
              href={`/blog?page=${nextPage}`}
              className="rounded-md border border-neon-cyan/35 px-4 py-2 text-sm font-medium text-neon-cyan transition-colors hover:bg-neon-cyan/10"
            >
              Next &rarr;
            </Link>
          ) : (
            <span className="cursor-not-allowed rounded-md border border-border px-4 py-2 text-sm font-medium text-muted-foreground opacity-50">
              Next &rarr;
            </span>
          )}
        </div>
      </nav>

      <div className="mt-12 space-y-12">
        {posts.length === 0 ? (
          <p className="text-muted-foreground">No posts yet. Check back soon!</p>
        ) : (
          posts.map((post) => (
            <article
              key={post.slug}
              className="group max-w-full overflow-hidden rounded-lg border border-border/70 bg-card/55 p-5 shadow-[0_14px_50px_oklch(0_0_0_/_0.18)] transition-colors hover:border-neon-cyan/45"
            >
              <Link href={`/blog/${post.slug}`}>
                <div className="flex flex-col gap-2">
                  <time className="text-sm text-muted-foreground">
                    {new Date(post.date).toLocaleDateString("en-US", {
                      year: "numeric",
                      month: "long",
                      day: "numeric",
                    })}
                  </time>
                  <h2 className="text-balance text-xl font-semibold text-foreground transition-colors group-hover:text-neon-cyan sm:text-2xl">
                    {post.title}
                  </h2>
                  <p className="text-muted-foreground">{post.description}</p>
                  {post.tags.length > 0 && (
                    <div className="mt-2 flex flex-wrap gap-2">
                      {post.tags.map((tag) => (
                        <span
                          key={tag}
                          className="inline-flex items-center rounded-md border border-neon-green/25 bg-neon-green/10 px-3 py-1 text-xs font-medium text-neon-green"
                        >
                          {tag}
                        </span>
                      ))}
                    </div>
                  )}
                </div>
              </Link>
            </article>
          ))
        )}
      </div>
    </div>
  );
}
