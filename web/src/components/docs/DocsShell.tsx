"use client";

import { useEffect, useMemo, useState } from "react";
import { usePathname } from "next/navigation";
import type { DocGuide } from "@/lib/docs";
import DocsSidebar from "@/components/docs/DocsSidebar";

interface DocsShellProps {
  guides: DocGuide[];
  children: React.ReactNode;
}

export default function DocsShell({ guides, children }: DocsShellProps) {
  const pathname = usePathname();
  const [isNavOpen, setIsNavOpen] = useState(false);

  useEffect(() => {
    const desktopQuery = window.matchMedia("(min-width: 1024px)");

    const handleDesktopChange = (event: MediaQueryListEvent) => {
      if (event.matches) {
        setIsNavOpen(false);
      }
    };

    desktopQuery.addEventListener("change", handleDesktopChange);

    return () => {
      desktopQuery.removeEventListener("change", handleDesktopChange);
    };
  }, []);

  useEffect(() => {
    if (!isNavOpen) {
      return;
    }

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setIsNavOpen(false);
      }
    };

    window.addEventListener("keydown", handleKeyDown);

    return () => {
      window.removeEventListener("keydown", handleKeyDown);
    };
  }, [isNavOpen]);

  const currentTitle = useMemo(() => {
    const currentGuide = guides.find(
      (guide) => pathname === `/docs/guides/${guide.slug}`
    );

    if (currentGuide) {
      return currentGuide.title;
    }

    if (pathname === "/docs/guides") {
      return "All guides";
    }

    return "Documentation";
  }, [guides, pathname]);

  return (
    <div className="min-h-[calc(100vh-3.5rem)] bg-white lg:min-h-[calc(100vh-4rem)] lg:flex">
      <div className="sticky top-14 z-30 border-b border-stone-200 bg-white/95 backdrop-blur-sm lg:hidden">
        <div className="flex items-center justify-between gap-3 px-4 py-3 sm:px-6">
          <div className="min-w-0">
            <p className="text-[11px] font-semibold uppercase tracking-[0.12em] text-stone-400">
              Documentation
            </p>
            <p className="truncate text-sm font-medium text-stone-900">
              {currentTitle}
            </p>
          </div>

          <button
            type="button"
            aria-expanded={isNavOpen}
            aria-controls="docs-mobile-nav"
            aria-label={
              isNavOpen ? "Close documentation navigation" : "Open documentation navigation"
            }
            className="inline-flex shrink-0 items-center gap-2 rounded-md border border-stone-200 bg-white px-3 py-2 text-sm font-medium text-stone-700 transition-colors hover:border-stone-300 hover:text-stone-950"
            onClick={() => setIsNavOpen((open) => !open)}
          >
            <svg
              aria-hidden="true"
              viewBox="0 0 24 24"
              className="h-4 w-4"
              fill="none"
              stroke="currentColor"
              strokeWidth="1.8"
              strokeLinecap="round"
              strokeLinejoin="round"
            >
              {isNavOpen ? (
                <path d="M6 6l12 12M18 6L6 18" />
              ) : (
                <path d="M4 7h16M4 12h16M4 17h16" />
              )}
            </svg>
            Browse docs
          </button>
        </div>
      </div>

      <DocsSidebar guides={guides} />

      <div className="min-w-0 flex-1">{children}</div>

      {isNavOpen ? (
        <>
          <button
            type="button"
            aria-label="Close documentation navigation overlay"
            className="fixed inset-0 top-14 z-30 bg-stone-950/20 lg:hidden"
            onClick={() => setIsNavOpen(false)}
          />
          <div
            id="docs-mobile-nav"
            className="fixed inset-x-0 bottom-0 top-14 z-40 border-t border-stone-200 bg-white shadow-2xl lg:hidden"
          >
            <div className="flex h-full flex-col">
              <div className="border-b border-stone-200 px-4 py-3 sm:px-6">
                <div className="flex items-center justify-between gap-3">
                  <div className="min-w-0">
                    <p className="text-[11px] font-semibold uppercase tracking-[0.12em] text-stone-400">
                      Documentation
                    </p>
                    <p className="truncate text-sm font-medium text-stone-900">
                      {currentTitle}
                    </p>
                  </div>
                  <button
                    type="button"
                    className="inline-flex h-9 w-9 items-center justify-center rounded-md border border-stone-200 bg-white text-stone-700 transition-colors hover:border-stone-300 hover:text-stone-950"
                    aria-label="Close documentation navigation"
                    onClick={() => setIsNavOpen(false)}
                  >
                    <svg
                      aria-hidden="true"
                      viewBox="0 0 24 24"
                      className="h-4 w-4"
                      fill="none"
                      stroke="currentColor"
                      strokeWidth="1.8"
                      strokeLinecap="round"
                      strokeLinejoin="round"
                    >
                      <path d="M6 6l12 12M18 6L6 18" />
                    </svg>
                  </button>
                </div>
              </div>

              <DocsSidebar
                guides={guides}
                variant="mobile"
                onNavigate={() => setIsNavOpen(false)}
              />
            </div>
          </div>
        </>
      ) : null}
    </div>
  );
}
