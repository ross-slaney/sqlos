import { notFound } from "next/navigation";
import { getAllGuides, getGuideBySlug } from "@/lib/docs";
import { MDXRemote } from "next-mdx-remote/rsc";
import remarkGfm from "remark-gfm";
import Link from "next/link";
import OnThisPage from "@/components/docs/OnThisPage";
import CopyPageButton from "@/components/docs/CopyPageButton";

interface PageProps {
  params: Promise<{ slug: string[] }>;
}

export async function generateStaticParams() {
  const guides = getAllGuides();
  return guides.map((guide) => ({ slug: guide.slug.split("/") }));
}

export async function generateMetadata({ params }: PageProps) {
  const { slug } = await params;
  const slugStr = slug.join("/");
  const guide = getGuideBySlug(slugStr);

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
  const slugStr = slug.join("/");
  const guide = getGuideBySlug(slugStr);

  if (!guide) {
    notFound();
  }

  const guides = getAllGuides();
  const currentIndex = guides.findIndex((g) => g.slug === slugStr);
  const prevGuide = currentIndex > 0 ? guides[currentIndex - 1] : null;
  const nextGuide =
    currentIndex >= 0 && currentIndex < guides.length - 1
      ? guides[currentIndex + 1]
      : null;

  return (
    <div className="flex min-w-0 flex-1 justify-center xl:px-8">
      <article className="w-full min-w-0 max-w-3xl px-4 py-8 sm:px-6 lg:px-12 lg:py-10">
        <div className="mb-6 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <Link
            href="/docs/guides"
            className="text-sm text-stone-500 hover:text-stone-800"
          >
            &larr; All docs
          </Link>
          <CopyPageButton />
        </div>

        <header className="mb-10">
          <h1 className="text-3xl font-semibold tracking-tight text-stone-950">
            {guide.title}
          </h1>
          <p className="mt-3 text-base leading-7 text-stone-600">
            {guide.description}
          </p>
        </header>

        <div className="docs-prose prose prose-stone max-w-none prose-headings:font-semibold prose-headings:tracking-tight prose-headings:text-stone-950 prose-p:leading-7 prose-p:text-stone-700 prose-a:text-violet-700 prose-a:no-underline hover:prose-a:underline prose-strong:text-stone-900 prose-code:text-violet-700 prose-code:before:content-none prose-code:after:content-none prose-img:rounded-xl prose-img:border prose-img:border-stone-200 prose-img:shadow-sm prose-th:text-left prose-table:text-sm">
          <MDXRemote
            source={guide.content}
            options={{ mdxOptions: { remarkPlugins: [remarkGfm] } }}
          />
        </div>

        <nav
          className="mt-14 flex flex-col gap-3 border-t border-stone-200 pt-6 sm:flex-row sm:items-center sm:justify-between"
          aria-label="Guide navigation"
        >
          {prevGuide ? (
            <Link
              href={`/docs/guides/${prevGuide.slug}`}
              className="text-sm font-medium text-stone-500 hover:text-stone-900"
            >
              ← {prevGuide.title}
            </Link>
          ) : (
            <span className="hidden sm:block" />
          )}
          {nextGuide ? (
            <Link
              href={`/docs/guides/${nextGuide.slug}`}
              className={`text-sm font-medium text-stone-500 hover:text-stone-900 ${
                prevGuide ? "" : "sm:ml-auto"
              }`}
            >
              {nextGuide.title} →
            </Link>
          ) : (
            <span className="hidden sm:block" />
          )}
        </nav>
      </article>

      <div className="hidden xl:block xl:pl-8">
        <OnThisPage />
      </div>
    </div>
  );
}
