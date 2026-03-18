import Header from "@/components/Header";
import Link from "next/link";
import DocsSearch from "@/components/docs/DocsSearch";

const navLinks = [
  { href: "/docs/guides/reference/api-reference", label: "API reference" },
  { href: "/docs/guides", label: "Guides" },
  { href: "/blog", label: "Blog" },
];

export default function DocsHeader() {
  return (
    <>
      <div id="docs-mobile-site-header" className="lg:hidden">
        <Header />
      </div>

      <header className="sticky top-0 z-50 hidden border-b border-stone-200 bg-white/95 backdrop-blur-sm lg:block">
        <div className="flex h-16 items-center justify-between gap-4 px-6">
          <Link href="/" className="flex items-center gap-2">
            <div className="flex h-7 w-7 items-center justify-center rounded-md bg-violet-600 text-xs font-bold text-white">
              SO
            </div>
            <span className="text-base font-bold text-stone-950">SqlOS</span>
            <span className="text-sm text-stone-400">Docs</span>
          </Link>

          <div className="mx-6 min-w-0 max-w-xl flex-1">
            <DocsSearch variant="desktop" />
          </div>

          <div className="flex items-center gap-5 text-sm">
            {navLinks.map((link) => (
              <Link
                key={link.href}
                href={link.href}
                className="text-stone-600 transition-colors hover:text-stone-900"
              >
                {link.label}
              </Link>
            ))}
          </div>
        </div>
      </header>
    </>
  );
}
