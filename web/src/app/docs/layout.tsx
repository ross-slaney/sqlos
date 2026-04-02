import Link from "next/link";
import { DocsLayout } from "@emcy/docs";
import { searchDocsAction } from "@/app/docs/actions";
import { docsSource } from "@/lib/docs-source";

function SqlosDocsBrand() {
  return (
    <Link href="/docs/guides" className="flex items-center gap-3">
      <span className="flex h-9 w-9 items-center justify-center rounded-xl bg-[linear-gradient(135deg,#6d28d9,#8b5cf6)] text-xs font-bold text-white shadow-[0_12px_28px_rgba(109,40,217,0.28)]">
        SO
      </span>
      <span className="flex flex-col leading-none">
        <span className="text-base font-bold tracking-[-0.03em]">SqlOS</span>
        <span className="text-[0.72rem] font-semibold uppercase tracking-[0.16em] text-violet-700/70">
          Embedded docs
        </span>
      </span>
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
      theme={{
        preset: "sqlos",
        mode: "light",
        layoutWidth: "1520px",
        contentWidth: "47rem",
        sidebarWidth: "320px",
        tocWidth: "220px",
        radius: "xl",
      }}
      topLinks={[
        { href: "/docs/guides/getting-started", label: "Quick start" },
        { href: "/docs/guides/authserver/overview", label: "AuthServer" },
        { href: "/docs/guides/fga/overview", label: "FGA" },
        { href: "/docs/guides/reference/api-reference", label: "API reference" },
      ]}
    >
      {children}
    </DocsLayout>
  );
}
