import { notFound } from "next/navigation";
import { getAllPosts, getPostBySlug } from "@/lib/blog";
import { MDXRemote } from "next-mdx-remote/rsc";
import remarkGfm from "remark-gfm";
import Link from "next/link";
import { Chip } from "@heroui/react";
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
    <div className="mx-4 max-w-[360px] overflow-hidden py-16 sm:mx-auto sm:max-w-3xl sm:px-6">
      <Link
        href="/blog"
        className="text-sm font-semibold text-neon-cyan transition-colors hover:text-neon-green"
      >
        &larr; Back to Blog
      </Link>

      <article className="mt-8">
        <header className="mb-8">
          <time className="font-mono text-sm text-muted-foreground">
            {new Date(post.date).toLocaleDateString("en-US", {
              year: "numeric",
              month: "long",
              day: "numeric",
            })}
          </time>
          <h1 className="mt-2 text-balance text-3xl font-bold text-foreground sm:text-4xl">
            {post.title}
          </h1>
          <p className="mt-4 text-lg leading-8 text-muted-foreground">
            {post.description}
          </p>
          {post.tags.length > 0 && (
            <div className="flex flex-wrap gap-2 mt-4">
              {post.tags.map((tag) => (
                <Chip
                  key={tag}
                  size="sm"
                  variant="soft"
                  color="success"
                  className="border border-neon-green/25 bg-neon-green/10 text-neon-green"
                >
                  {tag}
                </Chip>
              ))}
            </div>
          )}
          <p className="mt-4 text-sm text-muted-foreground">
            By {post.author}
          </p>
        </header>

        <div className="prose prose-invert max-w-none prose-headings:font-semibold prose-a:text-neon-cyan prose-pre:border prose-pre:border-neon-cyan/25 prose-pre:bg-[oklch(0.055_0.022_248)] prose-pre:text-foreground prose-code:text-neon-green prose-code:before:content-none prose-code:after:content-none">
          <MDXRemote
            source={post.content}
            components={blogMdxComponents}
            options={{ mdxOptions: { remarkPlugins: [remarkGfm] } }}
          />
        </div>
      </article>
    </div>
  );
}
