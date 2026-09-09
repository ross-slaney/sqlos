import Link from "next/link";
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
    <div className="sqlos-editorial">
      <div className="mx-auto max-w-5xl px-6 pb-20 pt-14">
        <header className="relative pb-10">
          <p className="sqlos-eyebrow">Blog</p>
          <h1 className="mt-4 text-[clamp(2.6rem,1.6rem+3vw,3.75rem)] font-medium leading-[1.02] tracking-[-0.03em] text-[hsl(var(--sq-ink))]">
            Notes from the SqlOS workshop
          </h1>
          <p className="mt-5 max-w-2xl text-xl leading-8 text-[hsl(var(--sq-ink-3))]">
            Auth, hierarchical authorization, EF Core, and practical .NET
            application security — release by release.
          </p>
          <div className="sqlos-hairline absolute inset-x-0 bottom-0" />
        </header>

        <nav
          className="mt-8 flex flex-wrap items-center justify-between gap-4"
          aria-label="Blog pagination"
        >
          <p className="font-mono text-xs uppercase tracking-[0.12em] text-[hsl(var(--sq-ink-3))]">
            {total === 0
              ? "No posts"
              : `Showing ${(currentPage - 1) * PAGE_SIZE + 1}–${Math.min(currentPage * PAGE_SIZE, total)} of ${total} posts`}
          </p>
          <div className="flex items-center gap-2">
            {prevPage ? (
              <Link
                href={prevPage === 1 ? "/blog" : `/blog?page=${prevPage}`}
                className="sqlos-pill-link"
              >
                &larr; Previous
              </Link>
            ) : (
              <span className="sqlos-pill-link" aria-disabled="true">
                &larr; Previous
              </span>
            )}
            <span className="px-1 font-mono text-xs text-[hsl(var(--sq-ink-3))]">
              {currentPage} / {totalPages}
            </span>
            {nextPage ? (
              <Link href={`/blog?page=${nextPage}`} className="sqlos-pill-link">
                Next &rarr;
              </Link>
            ) : (
              <span className="sqlos-pill-link" aria-disabled="true">
                Next &rarr;
              </span>
            )}
          </div>
        </nav>

        <div className="mt-10 grid gap-5">
          {posts.length === 0 ? (
            <p className="text-[hsl(var(--sq-ink-3))]">
              No posts yet. Check back soon!
            </p>
          ) : (
            posts.map((post) => (
              <article key={post.slug} className="sqlos-post-card group">
                <Link
                  href={`/blog/${post.slug}`}
                  className="grid gap-6 p-6 sm:grid-cols-[9rem_minmax(0,1fr)] sm:p-8"
                >
                  <time
                    dateTime={post.date}
                    className="font-mono text-xs uppercase leading-6 tracking-[0.12em] text-[hsl(var(--sq-violet))]"
                  >
                    {new Date(post.date).toLocaleDateString("en-US", {
                      year: "numeric",
                      month: "short",
                      day: "numeric",
                    })}
                  </time>
                  <div className="min-w-0">
                    <h2 className="font-[family-name:var(--font-display)] text-[1.7rem] font-medium leading-tight tracking-[-0.025em] text-[hsl(var(--sq-ink))] transition-colors group-hover:text-[hsl(var(--sq-violet-deep))]">
                      {post.title}
                    </h2>
                    <p className="mt-3 text-[1.0625rem] leading-7 text-[hsl(var(--sq-ink-3))]">
                      {post.description}
                    </p>
                    {post.tags.length > 0 && (
                      <div className="mt-5 flex flex-wrap gap-2">
                        {post.tags.map((tag) => (
                          <span key={tag} className="sqlos-tag">
                            {tag}
                          </span>
                        ))}
                      </div>
                    )}
                    <span className="mt-5 inline-flex items-center gap-2 text-sm font-semibold text-[hsl(var(--sq-violet))]">
                      Read the post
                      <span
                        aria-hidden="true"
                        className="transition-transform group-hover:translate-x-0.5"
                      >
                        &rarr;
                      </span>
                    </span>
                  </div>
                </Link>
              </article>
            ))
          )}
        </div>
      </div>
    </div>
  );
}
