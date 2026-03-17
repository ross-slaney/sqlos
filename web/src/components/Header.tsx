import Link from "next/link";

export default function Header() {
  return (
    <header className="sticky top-0 z-50 border-b border-stone-200/80 bg-[var(--background)]/90 backdrop-blur-md">
      <nav className="mx-auto flex h-14 max-w-5xl items-center justify-between">
        <Link
          href="/"
          className="text-[16px] font-bold tracking-[-0.02em] text-stone-950"
        >
          SqlOS
        </Link>
        <div className="flex items-center gap-1">
          <Link
            href="/docs"
            className="hidden sm:inline-flex rounded-md px-3 py-1.5 text-[13px] font-medium text-stone-500 transition-colors hover:text-stone-950"
          >
            Docs
          </Link>
          <Link
            href="/docs/guides/reference/api-reference"
            className="hidden sm:inline-flex rounded-md px-3 py-1.5 text-[13px] font-medium text-stone-500 transition-colors hover:text-stone-950"
          >
            API
          </Link>
          <Link
            href="/blog"
            className="hidden sm:inline-flex rounded-md px-3 py-1.5 text-[13px] font-medium text-stone-500 transition-colors hover:text-stone-950"
          >
            Blog
          </Link>
          <Link
            href="/docs"
            className="sm:hidden rounded-md px-3 py-1.5 text-[13px] font-medium text-stone-500 transition-colors hover:text-stone-950"
          >
            Docs
          </Link>
          <Link
            href="/docs/guides/getting-started"
            className="ml-2 rounded-md bg-stone-950 px-3.5 py-1.5 text-[13px] font-semibold text-white transition hover:bg-stone-800"
          >
            Get started
          </Link>
        </div>
      </nav>
    </header>
  );
}
