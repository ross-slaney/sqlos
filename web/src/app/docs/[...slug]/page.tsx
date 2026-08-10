import { notFound, redirect } from "next/navigation";
import DocsArticle from "@/components/docs/DocsArticle";
import { docsSource } from "@/lib/docs-source";

interface PageProps {
  params: Promise<{ slug: string[] }>;
}

export function generateStaticParams() {
  return docsSource
    .getStaticParams()
    .filter(
      (param): param is { slug: string[] } =>
        Array.isArray(param.slug) && param.slug.length > 0
    );
}

export async function generateMetadata({ params }: PageProps) {
  const { slug } = await params;
  return docsSource.getMetadata(slug);
}

export default async function DocsRoutePage({ params }: PageProps) {
  const { slug } = await params;
  const resolved = docsSource.resolveRoute(slug);

  if (resolved.type === "redirect" && resolved.href) {
    redirect(resolved.href);
  }

  if (resolved.type !== "entry" || !resolved.entry) {
    notFound();
  }

  const sectionHref = resolved.entry.section
    ? (docsSource.getSectionLanding(resolved.entry.section)?.href ?? undefined)
    : undefined;

  return (
    <DocsArticle
      entry={resolved.entry}
      previousEntry={resolved.previousEntry}
      nextEntry={resolved.nextEntry}
      sectionHref={sectionHref}
    />
  );
}
