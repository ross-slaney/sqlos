import { notFound } from "next/navigation";
import { getAllPosts, getPostBySlug } from "@/lib/blog";
import { MDXRemote } from "next-mdx-remote/rsc";
import remarkGfm from "remark-gfm";
import Link from "next/link";

interface PageProps {
  params: Promise<{ slug: string }>;
}

export async function generateStaticParams() {
  const posts = getAllPosts();
  return posts.map((post) => ({ slug: post.slug }));
}

export async function generateMetadata({ params }: PageProps) {
  const { slug } = await params;
  const post = getPostBySlug(slug);

  if (!post) {
    return { title: "Post Not Found" };
  }

  return {
    title: `${post.title} - SqlOS Blog`,
    description: post.description,
  };
}

export default async function BlogPostPage({ params }: PageProps) {
  const { slug } = await params;
  const post = getPostBySlug(slug);

  if (!post) {
    notFound();
  }

  return (
    <div className="mx-auto max-w-3xl px-6 py-16">
      <Link
        href="/blog"
        className="text-sm text-indigo-600 hover:text-indigo-500 dark:text-indigo-400"
      >
        &larr; Back to Blog
      </Link>

      <article className="mt-8">
        <header className="mb-8">
          <time className="text-sm text-zinc-500 dark:text-zinc-500">
            {new Date(post.date).toLocaleDateString("en-US", {
              year: "numeric",
              month: "long",
              day: "numeric",
            })}
          </time>
          <h1 className="mt-2 text-4xl font-bold text-zinc-900 dark:text-white">
            {post.title}
          </h1>
          <p className="mt-4 text-lg text-zinc-600 dark:text-zinc-400">
            {post.description}
          </p>
          {post.tags.length > 0 && (
            <div className="flex flex-wrap gap-2 mt-4">
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
          <p className="mt-4 text-sm text-zinc-500 dark:text-zinc-500">
            By {post.author}
          </p>
        </header>

        <div className="prose prose-zinc dark:prose-invert max-w-none prose-headings:font-semibold prose-a:text-indigo-600 dark:prose-a:text-indigo-400 prose-pre:bg-zinc-900 prose-pre:text-zinc-300 prose-code:text-indigo-600 dark:prose-code:text-indigo-400 prose-code:before:content-none prose-code:after:content-none">
          <MDXRemote
            source={post.content}
            options={{ mdxOptions: { remarkPlugins: [remarkGfm] } }}
          />
        </div>
      </article>
    </div>
  );
}
