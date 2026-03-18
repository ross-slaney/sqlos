"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { usePathname } from "next/navigation";
import type { DocGuide } from "@/lib/docs";
import DocsSearch from "@/components/docs/DocsSearch";
import DocsSidebar from "@/components/docs/DocsSidebar";

interface DocsShellProps {
  guides: DocGuide[];
  children: React.ReactNode;
}

export default function DocsShell({ guides, children }: DocsShellProps) {
  const pathname = usePathname();
  const mobileBarRef = useRef<HTMLDivElement>(null);
  const [isNavOpen, setIsNavOpen] = useState(false);
  const [mobileDocsBarTop, setMobileDocsBarTop] = useState(56);
  const [mobileDocsBarHeight, setMobileDocsBarHeight] = useState(112);

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

  useEffect(() => {
    const mediaQuery = window.matchMedia("(max-width: 1023px)");

    if (!isNavOpen || !mediaQuery.matches) {
      return;
    }

    const previousBodyOverflow = document.body.style.overflow;
    const previousBodyOverscrollBehavior = document.body.style.overscrollBehavior;
    const previousHtmlOverflow = document.documentElement.style.overflow;
    const previousHtmlOverscrollBehavior = document.documentElement.style.overscrollBehavior;

    document.body.style.overflow = "hidden";
    document.body.style.overscrollBehavior = "none";
    document.documentElement.style.overflow = "hidden";
    document.documentElement.style.overscrollBehavior = "none";

    return () => {
      document.body.style.overflow = previousBodyOverflow;
      document.body.style.overscrollBehavior = previousBodyOverscrollBehavior;
      document.documentElement.style.overflow = previousHtmlOverflow;
      document.documentElement.style.overscrollBehavior = previousHtmlOverscrollBehavior;
    };
  }, [isNavOpen]);

  useEffect(() => {
    const mediaQuery = window.matchMedia("(max-width: 1023px)");
    let frameId = 0;
    let headerResizeObserver: ResizeObserver | null = null;
    let barResizeObserver: ResizeObserver | null = null;

    const updateMobileChromeMetrics = () => {
      const header = document.getElementById("docs-mobile-site-header");
      const nextTop = header
        ? Math.max(0, Math.round(header.getBoundingClientRect().bottom))
        : 0;
      const nextHeight = mobileBarRef.current
        ? Math.max(0, Math.round(mobileBarRef.current.getBoundingClientRect().height))
        : 0;

      setMobileDocsBarTop((current) => (current === nextTop ? current : nextTop));
      setMobileDocsBarHeight((current) => (current === nextHeight ? current : nextHeight));
    };

    const scheduleUpdate = () => {
      if (!mediaQuery.matches) {
        return;
      }

      if (frameId !== 0) {
        return;
      }

      frameId = window.requestAnimationFrame(() => {
        frameId = 0;
        updateMobileChromeMetrics();
      });
    };

    updateMobileChromeMetrics();

    const header = document.getElementById("docs-mobile-site-header");
    if (header && typeof ResizeObserver !== "undefined") {
      headerResizeObserver = new ResizeObserver(() => {
        scheduleUpdate();
      });
      headerResizeObserver.observe(header);
    }

    if (mobileBarRef.current && typeof ResizeObserver !== "undefined") {
      barResizeObserver = new ResizeObserver(() => {
        scheduleUpdate();
      });
      barResizeObserver.observe(mobileBarRef.current);
    }

    window.addEventListener("scroll", scheduleUpdate, { passive: true });
    window.addEventListener("resize", scheduleUpdate);

    return () => {
      if (frameId !== 0) {
        window.cancelAnimationFrame(frameId);
      }

      window.removeEventListener("scroll", scheduleUpdate);
      window.removeEventListener("resize", scheduleUpdate);
      headerResizeObserver?.disconnect();
      barResizeObserver?.disconnect();
    };
  }, []);

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
      <div className="lg:hidden">
        <div aria-hidden="true" style={{ height: `${mobileDocsBarHeight}px` }} />
        <div
          ref={mobileBarRef}
          className="fixed inset-x-0 z-30 border-b border-stone-200 bg-white/95 backdrop-blur-sm transition-[top] duration-200 ease-out"
          style={{ top: `${mobileDocsBarTop}px` }}
        >
          <div className="px-4 py-3 sm:px-6">
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
                <span className="hidden sm:inline">Browse docs</span>
              </button>
            </div>

            <div className="mt-3">
              <DocsSearch variant="mobile" />
            </div>
          </div>
        </div>
      </div>

      <DocsSidebar guides={guides} />

      <div className="min-w-0 flex-1">{children}</div>

      {isNavOpen ? (
        <>
          <button
            type="button"
            aria-label="Close documentation navigation overlay"
            className="fixed inset-x-0 bottom-0 z-30 bg-stone-950/20 lg:hidden"
            style={{ top: `${mobileDocsBarTop + mobileDocsBarHeight}px` }}
            onClick={() => setIsNavOpen(false)}
          />
          <div
            id="docs-mobile-nav"
            className="fixed inset-x-0 bottom-0 z-40 overflow-hidden border-t border-stone-200 bg-white shadow-2xl lg:hidden"
            style={{ top: `${mobileDocsBarTop + mobileDocsBarHeight}px` }}
          >
            <div className="flex h-full min-h-0 flex-col">
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
