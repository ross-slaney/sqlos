"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useEffect, useState } from "react";
import { DocsSearch } from "@emcy/docs";
import type { DocsNavSection, DocsSearchAction } from "@emcy/docs";
import BrandMark from "@/components/BrandMark";
import DocsSidebarNav from "@/components/docs/DocsSidebarNav";

export default function DocsTopBar({
  navigation,
  searchAction,
}: {
  navigation: DocsNavSection[];
  searchAction: DocsSearchAction;
}) {
  const [isNavOpen, setIsNavOpen] = useState(false);
  const pathname = usePathname();

  useEffect(() => {
    setIsNavOpen(false);
  }, [pathname]);

  useEffect(() => {
    if (!isNavOpen) return;
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") setIsNavOpen(false);
    };
    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [isNavOpen]);

  return (
    <header className="sticky top-0 z-50 w-full border-b bg-background/85 backdrop-blur-md">
      <div className="mx-auto flex h-[60px] w-full max-w-[1440px] items-center gap-5 px-6">
        <button
          type="button"
          className="inline-flex h-9 w-9 items-center justify-center rounded-lg border text-foreground lg:hidden"
          onClick={() => setIsNavOpen(!isNavOpen)}
          aria-label={isNavOpen ? "Close navigation" : "Open navigation"}
          aria-expanded={isNavOpen}
        >
          <svg
            className="h-[18px] w-[18px]"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            strokeLinejoin="round"
            viewBox="0 0 24 24"
          >
            {isNavOpen ? (
              <path d="M6 6l12 12M18 6L6 18" />
            ) : (
              <path d="M3 6h18M3 12h18M3 18h18" />
            )}
          </svg>
        </button>

        <Link
          href="/"
          className="flex shrink-0 items-center gap-2.5 text-base font-bold tracking-tight text-foreground"
        >
          <BrandMark className="h-[26px] w-[26px] drop-shadow-[0_2px_6px_rgba(79,70,229,0.35)]" />
          <span>SqlOS</span>
          <span className="ml-0.5 border-l pl-2.5 text-xs font-semibold tracking-normal text-muted-foreground">
            Docs
          </span>
        </Link>

        <div className="sqlos-docs-search hidden min-w-0 flex-1 justify-center md:flex">
          <DocsSearch searchAction={searchAction} placeholder="Search docs…" />
        </div>

        <div className="ml-auto flex shrink-0 items-center gap-[18px] md:ml-0">
          <Link
            href="/docs/reference/api-reference"
            className="hidden text-sm font-medium text-foreground/70 transition-colors hover:text-foreground sm:inline"
          >
            API Reference
          </Link>
          <a
            href="https://github.com/ross-slaney/sqlos"
            target="_blank"
            rel="noopener noreferrer"
            className="hidden text-sm font-medium text-foreground/70 transition-colors hover:text-foreground sm:inline"
          >
            GitHub
          </a>
          <span className="inline-flex items-center gap-1.5 rounded-[5px] bg-accent px-2.5 py-[5px] text-[12.5px] font-semibold text-accent-foreground">
            SqlOS 3.24.1
            <svg
              className="h-3 w-3"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2.4"
              strokeLinecap="round"
              strokeLinejoin="round"
            >
              <path d="m6 9 6 6 6-6" />
            </svg>
          </span>
        </div>
      </div>

      {isNavOpen && (
        <div className="fixed inset-x-0 top-[60px] bottom-0 z-40 overflow-y-auto border-t bg-background p-4 lg:hidden">
          <DocsSidebarNav navigation={navigation} />
        </div>
      )}
    </header>
  );
}
