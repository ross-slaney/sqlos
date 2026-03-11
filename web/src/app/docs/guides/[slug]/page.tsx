import { notFound } from "next/navigation";
import { getAllGuides, getGuideBySlug } from "@/lib/docs";
import { MDXRemote } from "next-mdx-remote/rsc";
import remarkGfm from "remark-gfm";
import Link from "next/link";

interface PageProps {
  params: Promise<{ slug: string }>;
}

export async function generateStaticParams() {
  const guides = getAllGuides();
  return guides.map((guide) => ({ slug: guide.slug }));
}

export async function generateMetadata({ params }: PageProps) {
  const { slug } = await params;
  const guide = getGuideBySlug(slug);

  if (!guide) {
    return { title: "Guide Not Found" };
  }

  return {
    title: `${guide.title} - SqlOS Docs`,
    description: guide.description,
  };
}

export default async function GuidePage({ params }: PageProps) {
  const { slug } = await params;
  const guide = getGuideBySlug(slug);

  if (!guide) {
    notFound();
  }

  const guides = getAllGuides();
  const currentIndex = guides.findIndex((g) => g.slug === slug);
  const prevGuide = currentIndex > 0 ? guides[currentIndex - 1] : null;
  const nextGuide =
    currentIndex >= 0 && currentIndex < guides.length - 1
      ? guides[currentIndex + 1]
      : null;

  return (
    <div className="mx-auto max-w-5xl px-6 py-16">
      <Link
        href="/docs/guides"
        className="text-sm text-stone-600 hover:text-stone-950"
      >
        &larr; Back to Guides
      </Link>

      <article className="mt-8">
        <header className="mb-8">
          <h1 className="text-4xl font-semibold tracking-tight text-stone-950">
            {guide.title}
          </h1>
          <p className="mt-4 text-lg text-stone-700">{guide.description}</p>
        </header>

        <div className="prose prose-stone max-w-none prose-headings:font-semibold prose-headings:text-stone-950 prose-a:text-emerald-800 prose-a:no-underline hover:prose-a:underline prose-pre:bg-stone-950 prose-pre:text-stone-200 prose-code:text-emerald-800 prose-code:before:content-none prose-code:after:content-none prose-img:rounded-xl prose-img:border prose-img:border-stone-200 prose-img:shadow-sm">
          <MDXRemote
            source={guide.content}
            options={{ mdxOptions: { remarkPlugins: [remarkGfm] } }}
          />
        </div>

        <nav
          className="mt-12 flex items-center justify-between border-t border-stone-200 pt-8"
          aria-label="Guide navigation"
        >
          {prevGuide ? (
            <Link
              href={`/docs/guides/${prevGuide.slug}`}
              className="text-sm font-medium text-stone-600 hover:text-stone-950"
            >
              ← {prevGuide.title}
            </Link>
          ) : (
            <span />
          )}
          {nextGuide ? (
            <Link
              href={`/docs/guides/${nextGuide.slug}`}
              className="text-sm font-medium text-stone-600 hover:text-stone-950"
            >
              {nextGuide.title} →
            </Link>
          ) : (
            <span />
          )}
        </nav>
      </article>
    </div>
  );
}
