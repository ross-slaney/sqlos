import Link from "next/link";

export default function Header() {
  return (
    <header className="sticky top-0 z-50 border-b border-stone-200 bg-white/95 backdrop-blur-sm">
      <nav className="mx-auto flex max-w-6xl items-center justify-between px-6 py-4">
        <Link href="/" className="flex items-center gap-2">
          <div className="flex h-7 w-7 items-center justify-center rounded-md bg-violet-600 text-xs font-bold text-white">
            SO
          </div>
          <span className="text-lg font-bold text-stone-950">SqlOS</span>
        </Link>
        <div className="flex items-center gap-6">
          <Link
            href="/docs"
            className="text-sm font-medium text-stone-600 transition-colors hover:text-stone-950"
          >
            Docs
          </Link>
          <Link
            href="/docs/guides/reference/api-reference"
            className="text-sm font-medium text-stone-600 transition-colors hover:text-stone-950"
          >
            API reference
          </Link>
          <Link
            href="/docs/guides/getting-started"
            className="text-sm font-medium text-stone-600 transition-colors hover:text-stone-950"
          >
            Quick start
          </Link>
          <Link
            href="/blog"
            className="text-sm font-medium text-stone-600 transition-colors hover:text-stone-950"
          >
            Blog
          </Link>
        </div>
      </nav>
    </header>
  );
}
