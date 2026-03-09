import Link from "next/link";

export default function Header() {
  return (
    <header className="sticky top-0 z-50 border-b border-stone-200 bg-stone-50/90 backdrop-blur-sm">
      <nav className="mx-auto flex max-w-6xl items-center justify-between px-6 py-4">
        <Link href="/" className="flex items-center gap-2">
          <div className="flex h-8 w-8 items-center justify-center rounded-lg bg-emerald-800 text-white font-bold text-sm">
            SO
          </div>
          <span className="text-xl font-bold text-stone-950">
            SqlOS
          </span>
        </Link>
        <div className="flex items-center gap-6">
          <Link
            href="/"
            className="text-sm font-medium text-stone-600 transition-colors hover:text-stone-950"
          >
            Home
          </Link>
          <Link
            href="/docs"
            className="text-sm font-medium text-stone-600 transition-colors hover:text-stone-950"
          >
            Docs
          </Link>
          <Link
            href="/docs#example-stack"
            className="text-sm font-medium text-stone-600 transition-colors hover:text-stone-950"
          >
            Example
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
