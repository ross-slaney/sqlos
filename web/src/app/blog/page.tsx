import Link from "next/link";
import { getPaginatedPosts } from "@/lib/blog";

export const metadata = {
  title: "Blog - Sqlzibar",
  description:
    "Articles about authorization, RBAC, and building secure .NET applications.",
};

const PAGE_SIZE = 5;

interface BlogPageProps {
  searchParams: Promise<{ page?: string }>;
}

export default async function BlogPage({ searchParams }: BlogPageProps) {
  const { page: pageParam } = await searchParams;
  const page = Math.max(1, parseInt(pageParam ?? "1", 10) || 1);
  const { posts, total, page: currentPage, totalPages } = getPaginatedPosts(
    page,
    PAGE_SIZE
  );

  const prevPage = currentPage > 1 ? currentPage - 1 : null;
  const nextPage = currentPage < totalPages ? currentPage + 1 : null;

  return (
    <div className="mx-auto max-w-4xl px-6 py-16">
      <h1 className="text-4xl font-bold text-zinc-900 dark:text-white">Blog</h1>
      <p className="mt-4 text-lg text-zinc-600 dark:text-zinc-400">
        Articles about authorization, RBAC, and building secure .NET
        applications.
      </p>

      {/* Pagination controls - always at top, always visible */}
      <nav
        className="mt-8 flex items-center justify-between border-b border-zinc-200 pb-6 dark:border-zinc-700"
        aria-label="Blog pagination"
      >
        <p className="text-sm text-zinc-600 dark:text-zinc-400">
          {total === 0
            ? "No posts"
            : `Showing ${(currentPage - 1) * PAGE_SIZE + 1}–${Math.min(currentPage * PAGE_SIZE, total)} of ${total} posts`}
        </p>
        <div className="flex items-center gap-2">
          {prevPage ? (
            <Link
              href={prevPage === 1 ? "/blog" : `/blog?page=${prevPage}`}
              className="rounded-md border border-zinc-300 bg-white px-4 py-2 text-sm font-medium text-zinc-700 hover:bg-zinc-50 dark:border-zinc-600 dark:bg-zinc-800 dark:text-zinc-300 dark:hover:bg-zinc-700"
            >
              ← Previous
            </Link>
          ) : (
            <span
              className="cursor-not-allowed rounded-md border border-zinc-200 bg-zinc-50 px-4 py-2 text-sm font-medium text-zinc-400 dark:border-zinc-700 dark:bg-zinc-800/50 dark:text-zinc-500"
              aria-disabled="true"
            >
              ← Previous
            </span>
          )}
          <span className="text-sm text-zinc-600 dark:text-zinc-400">
            Page {currentPage} of {totalPages}
          </span>
          {nextPage ? (
            <Link
              href={`/blog?page=${nextPage}`}
              className="rounded-md border border-zinc-300 bg-white px-4 py-2 text-sm font-medium text-zinc-700 hover:bg-zinc-50 dark:border-zinc-600 dark:bg-zinc-800 dark:text-zinc-300 dark:hover:bg-zinc-700"
            >
              Next →
            </Link>
          ) : (
            <span
              className="cursor-not-allowed rounded-md border border-zinc-200 bg-zinc-50 px-4 py-2 text-sm font-medium text-zinc-400 dark:border-zinc-700 dark:bg-zinc-800/50 dark:text-zinc-500"
              aria-disabled="true"
            >
              Next →
            </span>
          )}
        </div>
      </nav>

      <div className="mt-12 space-y-12">
        {posts.length === 0 ? (
          <p className="text-zinc-500 dark:text-zinc-400">
            No posts yet. Check back soon!
          </p>
        ) : (
          posts.map((post) => (
            <article key={post.slug} className="group">
              <Link href={`/blog/${post.slug}`}>
                <div className="flex flex-col gap-2">
                  <time className="text-sm text-zinc-500 dark:text-zinc-500">
                    {new Date(post.date).toLocaleDateString("en-US", {
                      year: "numeric",
                      month: "long",
                      day: "numeric",
                    })}
                  </time>
                  <h2 className="text-2xl font-semibold text-zinc-900 group-hover:text-indigo-600 dark:text-white dark:group-hover:text-indigo-400 transition-colors">
                    {post.title}
                  </h2>
                  <p className="text-zinc-600 dark:text-zinc-400">
                    {post.description}
                  </p>
                  {post.tags.length > 0 && (
                    <div className="flex flex-wrap gap-2 mt-2">
                      {post.tags.map((tag) => (
                        <span
                          key={tag}
                          className="inline-flex items-center rounded-full bg-indigo-50 px-3 py-1 text-xs font-medium text-indigo-600 dark:bg-indigo-900/30 dark:text-indigo-400"
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
