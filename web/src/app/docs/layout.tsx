import Link from "next/link";
import { DocsLayout } from "@emcy/docs";
import { searchDocsAction } from "@/app/docs/actions";
import { docsSource } from "@/lib/docs-source";

function SqlosDocsBrand() {
  return (
    <Link href="/" className="flex items-center gap-2 text-stone-950">
      <span className="flex h-7 w-7 items-center justify-center rounded-md bg-violet-600 text-xs font-bold text-white">
        SO
      </span>
      <span className="text-base font-bold">SqlOS</span>
      <span className="text-sm text-stone-400">Docs</span>
    </Link>
  );
}

export default function DocsRootLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <DocsLayout
      navigation={docsSource.getNavigation()}
      searchAction={searchDocsAction}
      brand={<SqlosDocsBrand />}
      topLinks={[
        { href: "/docs/guides/reference/api-reference", label: "API reference" },
        { href: "/docs/guides", label: "Guides" },
        { href: "/blog", label: "Blog" },
      ]}
    >
      {children}
    </DocsLayout>
  );
}
