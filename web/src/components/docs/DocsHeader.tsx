import Link from "next/link";

export default function DocsHeader() {
  return (
    <header className="sticky top-0 z-50 border-b border-stone-200 bg-white/95 backdrop-blur-sm">
      <div className="flex items-center justify-between px-6 py-3.5">
        <Link href="/" className="flex items-center gap-2">
          <div className="flex h-7 w-7 items-center justify-center rounded-md bg-violet-600 text-xs font-bold text-white">
            SO
          </div>
          <span className="text-base font-bold text-stone-950">SqlOS</span>
          <span className="text-sm text-stone-400">Docs</span>
        </Link>

        <div className="mx-8 hidden max-w-sm flex-1 md:block">
          <div className="flex items-center rounded-lg border border-stone-200 bg-stone-50 px-3 py-1.5 text-sm text-stone-400">
            <svg
              className="mr-2 h-4 w-4 text-stone-400"
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              strokeWidth={2}
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z"
              />
            </svg>
            <span>Search the docs...</span>
            <kbd className="ml-auto rounded border border-stone-300 bg-white px-1.5 py-0.5 text-xs text-stone-400">
              ⌘K
            </kbd>
          </div>
        </div>

        <div className="flex items-center gap-5 text-sm">
          <Link
            href="/docs/guides/reference/api-reference"
            className="hidden text-stone-600 transition-colors hover:text-stone-900 md:block"
          >
            API reference
          </Link>
          <Link
            href="/docs/guides"
            className="hidden text-stone-600 transition-colors hover:text-stone-900 md:block"
          >
            Guides
          </Link>
          <Link
            href="/blog"
            className="hidden text-stone-600 transition-colors hover:text-stone-900 md:block"
          >
            Blog
          </Link>
        </div>
      </div>
    </header>
  );
}
