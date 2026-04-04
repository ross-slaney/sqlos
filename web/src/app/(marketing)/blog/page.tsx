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
    <div className="mx-auto max-w-[1400px] px-6 py-16">
      <h1 className="text-4xl font-bold text-foreground">Blog</h1>
      <p className="mt-4 text-lg text-muted-foreground">
        Notes on auth, hierarchical authorization, EF Core, and practical .NET
        application security.
      </p>

      <nav
        className="mt-8 flex items-center justify-between border-b pb-6"
        aria-label="Blog pagination"
      >
        <p className="text-sm text-muted-foreground">
          {total === 0
            ? "No posts"
            : `Showing ${(currentPage - 1) * PAGE_SIZE + 1}–${Math.min(currentPage * PAGE_SIZE, total)} of ${total} posts`}
        </p>
        <div className="flex items-center gap-2">
          {prevPage ? (
            <Link
              href={prevPage === 1 ? "/blog" : `/blog?page=${prevPage}`}
              className="rounded-md border px-4 py-2 text-sm font-medium text-foreground transition-colors hover:bg-accent"
            >
              &larr; Previous
            </Link>
          ) : (
            <span className="cursor-not-allowed rounded-md border px-4 py-2 text-sm font-medium text-muted-foreground opacity-50">
              &larr; Previous
            </span>
          )}
          <span className="text-sm text-muted-foreground">
            Page {currentPage} of {totalPages}
          </span>
          {nextPage ? (
            <Link
              href={`/blog?page=${nextPage}`}
              className="rounded-md border px-4 py-2 text-sm font-medium text-foreground transition-colors hover:bg-accent"
            >
              Next &rarr;
            </Link>
          ) : (
            <span className="cursor-not-allowed rounded-md border px-4 py-2 text-sm font-medium text-muted-foreground opacity-50">
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
            <article key={post.slug} className="group">
              <Link href={`/blog/${post.slug}`}>
                <div className="flex flex-col gap-2">
                  <time className="text-sm text-muted-foreground">
                    {new Date(post.date).toLocaleDateString("en-US", {
                      year: "numeric",
                      month: "long",
                      day: "numeric",
                    })}
                  </time>
                  <h2 className="text-2xl font-semibold text-foreground transition-colors group-hover:text-muted-foreground">
                    {post.title}
                  </h2>
                  <p className="text-muted-foreground">{post.description}</p>
                  {post.tags.length > 0 && (
                    <div className="mt-2 flex flex-wrap gap-2">
                      {post.tags.map((tag) => (
                        <span
                          key={tag}
                          className="inline-flex items-center rounded-full bg-secondary px-3 py-1 text-xs font-medium text-secondary-foreground"
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
