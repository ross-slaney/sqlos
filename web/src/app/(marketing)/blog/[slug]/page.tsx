import { notFound } from "next/navigation";
import { getAllPosts, getPostBySlug } from "@/lib/blog";
import { MDXRemote } from "next-mdx-remote/rsc";
import rehypePrettyCode from "rehype-pretty-code";
import remarkGfm from "remark-gfm";
import Link from "next/link";
import { blogMdxComponents } from "@/components/blog/BlogMdxComponents";

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

  const published = new Date(post.date).toLocaleDateString("en-US", {
    year: "numeric",
    month: "long",
    day: "numeric",
  });

  return (
    <div className="sqlos-editorial">
      <div className="mx-auto max-w-3xl px-6 pb-20 pt-12">
        <Link href="/blog" className="sqlos-pill-link">
          <span aria-hidden="true">&larr;</span>
          All posts
        </Link>

        <article className="mt-10">
          <header className="relative mb-12 pb-10">
            <p className="sqlos-eyebrow">
              <time dateTime={post.date}>{published}</time>
            </p>
            <h1 className="mt-4 text-[clamp(2.1rem,1.4rem+1.8vw,2.85rem)] font-bold leading-[1.1] tracking-[-0.03em] text-[hsl(var(--sq-ink))]">
              {post.title}
            </h1>
            <p className="mt-5 max-w-2xl text-xl leading-8 text-[hsl(var(--sq-ink-3))]">
              {post.description}
            </p>
            <div className="mt-6 flex flex-wrap items-center gap-x-4 gap-y-3">
              <p className="text-sm font-medium text-[hsl(var(--sq-ink-2))]">
                By {post.author}
              </p>
              {post.tags.length > 0 && (
                <div className="flex flex-wrap gap-2">
                  {post.tags.map((tag) => (
                    <span key={tag} className="sqlos-tag">
                      {tag}
                    </span>
                  ))}
                </div>
              )}
            </div>
            <div className="sqlos-hairline absolute inset-x-0 bottom-0" />
          </header>

          <div className="sqlos-prose prose max-w-none">
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
    </div>
  );
}
