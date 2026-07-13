import { DocsHomePage } from "@emcy/docs";
import { docsSource } from "@/lib/docs-source";

export const metadata = {
  title: "SqlOS documentation",
  description:
    "Start with one .NET application, then add authentication, organizations, and SQL-backed authorization as your B2B SaaS grows.",
};

export default function DocsPage() {
  return (
    <DocsHomePage
      entry={docsSource.getHomeEntry()}
      navigation={docsSource.getNavigation()}
    />
  );
}
