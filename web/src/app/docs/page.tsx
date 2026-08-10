import { notFound } from "next/navigation";
import DocsArticle from "@/components/docs/DocsArticle";
import { docsSource } from "@/lib/docs-source";

export const metadata = {
  title: "SqlOS documentation",
  description:
    "Start with one .NET application, then add authentication, organizations, and SQL-backed authorization as your B2B SaaS grows.",
};

export default function DocsPage() {
  const entry = docsSource.getHomeEntry();
  if (!entry) notFound();

  return <DocsArticle entry={entry} />;
}
