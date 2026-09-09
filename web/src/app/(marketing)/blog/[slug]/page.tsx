import { notFound } from "next/navigation";
import { getAllPosts, getPostBySlug } from "@/lib/blog";
import { MDXRemote } from "next-mdx-remote/rsc";
import rehypePrettyCode from "rehype-pretty-code";
import remarkGfm from "remark-gfm";

const prettyCodeOptions = {
  theme: {
    light: "github-light",
    dark: "github-dark-default",
  },
  keepBackground: false,
  defaultLang: {
    block: "plaintext",
  },
};
import Link from "next/link";
import { blogMdxComponents } from "@/components/blog/BlogMdxComponents";

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
        className="text-sm font-medium text-primary hover:text-primary/80"
      >
        &larr; Back to Blog
      </Link>

      <article className="mt-8">
        <header className="mb-10 border-b border-border pb-8">
          <time className="text-sm text-muted-foreground">
            {new Date(post.date).toLocaleDateString("en-US", {
              year: "numeric",
              month: "long",
              day: "numeric",
            })}
          </time>
          <h1 className="mt-2 text-4xl font-bold tracking-tight text-foreground">
            {post.title}
          </h1>
          <p className="mt-4 text-lg leading-7 text-muted-foreground">
            {post.description}
          </p>
          {post.tags.length > 0 && (
            <div className="mt-4 flex flex-wrap gap-2">
              {post.tags.map((tag) => (
                <span
                  key={tag}
                  className="inline-flex items-center rounded-full border border-border bg-secondary px-3 py-1 text-xs font-medium text-secondary-foreground"
                >
                  {tag}
                </span>
              ))}
            </div>
          )}
          <p className="mt-4 text-sm text-muted-foreground">
            By {post.author}
          </p>
        </header>

        <div className="sqlos-prose prose prose-zinc max-w-none prose-headings:font-semibold prose-headings:tracking-tight prose-headings:text-foreground prose-p:text-zinc-800 prose-li:text-zinc-800 prose-strong:text-foreground prose-a:font-medium prose-a:text-primary prose-a:no-underline hover:prose-a:underline prose-code:before:content-none prose-code:after:content-none prose-pre:bg-transparent prose-pre:p-0">
          <MDXRemote
            source={post.content}
            components={blogMdxComponents}
            options={{
              mdxOptions: {
                remarkPlugins: [remarkGfm],
                rehypePlugins: [[rehypePrettyCode, prettyCodeOptions]],
              },
            }}
          />
        </div>
      </article>
    </div>
  );
}
