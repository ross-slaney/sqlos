import Link from "next/link";

export default function Footer() {
  return (
    <footer className="border-t border-stone-200/80">
      <div className="mx-auto flex max-w-4xl items-center justify-between px-6 py-6">
        <Link href="/" className="text-[14px] font-semibold text-stone-950">
          SqlOS
        </Link>
        <div className="flex items-center gap-5 text-[13px] text-stone-400">
          <Link href="/docs" className="transition-colors hover:text-stone-600">
            Docs
          </Link>
          <Link href="/blog" className="transition-colors hover:text-stone-600">
            Blog
          </Link>
          <span>MIT License</span>
        </div>
      </div>
    </footer>
  );
}
