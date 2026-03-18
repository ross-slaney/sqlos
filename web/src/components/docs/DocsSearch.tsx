"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useMemo, useRef, useState, useTransition } from "react";
import { searchDocsAction } from "@/app/docs/actions";
import type { DocsSearchResponse, DocsSearchResult } from "@/lib/docs";

interface DocsSearchProps {
  variant?: "desktop" | "mobile";
}

const EMPTY_RESPONSE: DocsSearchResponse = {
  query: "",
  total: 0,
  results: [],
};

const FEATURED_RESULTS = [
  {
    href: "/docs/guides/getting-started",
    title: "Getting Started",
    label: "Start here",
  },
  {
    href: "/docs/guides/authserver/overview",
    title: "AuthServer Overview",
    label: "Identity and sessions",
  },
  {
    href: "/docs/guides/fga/overview",
    title: "FGA Overview",
    label: "Permissions and hierarchy",
  },
  {
    href: "/docs/guides/reference/api-reference",
    title: "API Reference",
    label: "Endpoints and contracts",
  },
];

const SUGGESTED_QUERIES = [
  "saml sso",
  "refresh token",
  "permissions",
  "headless auth",
];

const MATCH_LABELS: Record<DocsSearchResult["matchedFields"][number], string> = {
  title: "Title",
  description: "Summary",
  content: "Content",
};

export default function DocsSearch({ variant = "desktop" }: DocsSearchProps) {
  const router = useRouter();
  const inputRef = useRef<HTMLInputElement>(null);
  const requestIdRef = useRef(0);

  const [isOpen, setIsOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [response, setResponse] = useState<DocsSearchResponse>(EMPTY_RESPONSE);
  const [error, setError] = useState<string | null>(null);
  const [isPending, startTransition] = useTransition();

  const trimmedQuery = query.trim();
  const activeResponse = trimmedQuery.length >= 2 ? response : EMPTY_RESPONSE;
  const topResult = activeResponse.results[0] ?? null;

  const closeSearch = () => {
    requestIdRef.current += 1;
    setIsOpen(false);
    setQuery("");
    setResponse(EMPTY_RESPONSE);
    setError(null);
  };

  useEffect(() => {
    if (variant !== "desktop") {
      return;
    }

    const mediaQuery = window.matchMedia("(min-width: 1024px)");

    const handleKeyDown = (event: KeyboardEvent) => {
      const isShortcut = (event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "k";

      if (!isShortcut || !mediaQuery.matches) {
        return;
      }

      event.preventDefault();
      setIsOpen(true);
    };

    window.addEventListener("keydown", handleKeyDown);

    return () => {
      window.removeEventListener("keydown", handleKeyDown);
    };
  }, [variant]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    const frameId = window.requestAnimationFrame(() => {
      inputRef.current?.focus();
    });

    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = "hidden";

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        closeSearch();
      }
    };

    window.addEventListener("keydown", handleKeyDown);

    return () => {
      document.body.style.overflow = previousOverflow;
      window.cancelAnimationFrame(frameId);
      window.removeEventListener("keydown", handleKeyDown);
    };
  }, [isOpen]);

  useEffect(() => {
    if (!isOpen || trimmedQuery.length < 2) {
      return;
    }

    const requestId = ++requestIdRef.current;
    const timeoutId = window.setTimeout(() => {
      startTransition(async () => {
        try {
          const nextResponse = await searchDocsAction(trimmedQuery);

          if (requestIdRef.current !== requestId) {
            return;
          }

          setResponse(nextResponse);
        } catch {
          if (requestIdRef.current !== requestId) {
            return;
          }

          setError("Search is taking a moment. Please try again.");
        }
      });
    }, 260);

    return () => {
      window.clearTimeout(timeoutId);
    };
  }, [isOpen, trimmedQuery, startTransition]);

  const resultSummary = useMemo(() => {
    if (trimmedQuery.length < 2) {
      return "Search titles, summaries, and full guide content.";
    }

    if (error) {
      return error;
    }

    if (activeResponse.total === 0) {
      return `No docs matched "${trimmedQuery}".`;
    }

    const resultWord = activeResponse.total === 1 ? "result" : "results";
    return `${activeResponse.total} ${resultWord} for "${activeResponse.query}"`;
  }, [activeResponse.query, activeResponse.total, error, trimmedQuery]);

  return (
    <>
      {variant === "desktop" ? (
        <button
          type="button"
          onClick={() => setIsOpen(true)}
          className="group flex w-full items-center gap-3 rounded-xl border border-stone-200 bg-stone-50/90 px-4 py-2 text-sm text-stone-500 shadow-[inset_0_1px_0_rgba(255,255,255,0.85)] transition-all hover:border-violet-200 hover:bg-white hover:text-stone-700 hover:shadow-[0_16px_35px_rgba(124,58,237,0.08)]"
        >
          <SearchIcon className="h-4 w-4 shrink-0 text-stone-400 transition-colors group-hover:text-violet-500" />
          <span className="flex-1 text-left">Search the docs...</span>
          <kbd className="rounded-md border border-stone-200 bg-white px-1.5 py-0.5 text-[11px] font-medium text-stone-400 shadow-sm">
            ⌘K
          </kbd>
        </button>
      ) : (
        <button
          type="button"
          onClick={() => setIsOpen(true)}
          className="inline-flex shrink-0 items-center gap-2 rounded-md border border-stone-200 bg-white px-3 py-2 text-sm font-medium text-stone-700 transition-colors hover:border-violet-200 hover:text-violet-700"
        >
          <SearchIcon className="h-4 w-4" />
          <span className="hidden sm:inline">Search</span>
        </button>
      )}

      {isOpen ? (
        <div className="fixed inset-0 z-[90] bg-stone-950/20 p-4 backdrop-blur-[2px] sm:p-6 lg:p-10">
          <button
            type="button"
            aria-label="Close docs search"
            className="absolute inset-0"
            onClick={closeSearch}
          />

          <div
            role="dialog"
            aria-modal="true"
            aria-labelledby="docs-search-title"
            className="relative mx-auto flex h-full w-full max-w-4xl flex-col overflow-hidden rounded-[28px] border border-white/70 bg-white shadow-[0_30px_120px_rgba(28,25,23,0.18)] sm:h-auto sm:max-h-[min(84vh,760px)]"
          >
            <div className="border-b border-stone-200/80 bg-[radial-gradient(circle_at_top_left,_rgba(139,92,246,0.10),_transparent_34%),linear-gradient(to_bottom,_rgba(250,250,249,0.98),_rgba(255,255,255,0.98))] px-4 py-4 sm:px-6">
              <div className="flex items-start justify-between gap-3">
                <div>
                  <p
                    id="docs-search-title"
                    className="text-sm font-semibold tracking-tight text-stone-950"
                  >
                    Search SqlOS docs
                  </p>
                  <p className="mt-1 text-sm text-stone-500">{resultSummary}</p>
                </div>

                <button
                  type="button"
                  onClick={closeSearch}
                  className="inline-flex h-10 w-10 items-center justify-center rounded-xl border border-stone-200 bg-white text-stone-500 transition-colors hover:border-stone-300 hover:text-stone-900"
                  aria-label="Close docs search"
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

              <div className="mt-4 rounded-2xl border border-stone-200 bg-white/95 p-2 shadow-[0_16px_40px_rgba(28,25,23,0.08)]">
                <div className="flex items-center gap-3 rounded-xl bg-stone-50 px-3 py-2.5">
                  <SearchIcon className="h-5 w-5 shrink-0 text-violet-500" />
                  <input
                    ref={inputRef}
                    value={query}
                    onChange={(event) => {
                      const nextValue = event.target.value;
                      requestIdRef.current += 1;
                      setQuery(nextValue);
                      setError(null);
                    }}
                    onKeyDown={(event) => {
                      if (event.key === "Enter" && topResult) {
                        event.preventDefault();
                        closeSearch();
                        router.push(topResult.href);
                      }
                    }}
                    placeholder="Search titles, guides, setup steps, APIs, and embedded code..."
                    className="w-full bg-transparent text-[15px] text-stone-950 outline-none placeholder:text-stone-400"
                  />
                  {isPending && trimmedQuery.length >= 2 ? (
                    <div className="h-5 w-5 rounded-full border-2 border-violet-200 border-t-violet-600 animate-spin" />
                  ) : (
                    <kbd className="hidden rounded-md border border-stone-200 bg-white px-1.5 py-0.5 text-[11px] font-medium text-stone-400 sm:inline-block">
                      ESC
                    </kbd>
                  )}
                </div>
              </div>
            </div>

            <div className="min-h-0 flex-1 overflow-y-auto bg-[linear-gradient(to_bottom,_rgba(250,250,249,0.7),_rgba(255,255,255,1))] px-3 py-3 sm:px-4 sm:py-4">
              {trimmedQuery.length < 2 ? (
                <div className="grid gap-4 lg:grid-cols-[1.2fr_0.8fr]">
                  <div className="rounded-2xl border border-stone-200 bg-white/90 p-5 shadow-sm">
                    <p className="text-xs font-semibold uppercase tracking-[0.16em] text-violet-600">
                      Search everywhere
                    </p>
                    <h3 className="mt-2 text-lg font-semibold tracking-tight text-stone-950">
                      Titles, summaries, and deep guide content
                    </h3>
                    <p className="mt-2 text-sm leading-6 text-stone-600">
                      Search across the full docs corpus, including AuthServer,
                      FGA, setup guides, and API reference content.
                    </p>

                    <div className="mt-5 flex flex-wrap gap-2">
                      {SUGGESTED_QUERIES.map((suggestion) => (
                        <button
                          key={suggestion}
                          type="button"
                          className="rounded-full border border-stone-200 bg-stone-50 px-3 py-1.5 text-sm font-medium text-stone-700 transition-colors hover:border-violet-200 hover:bg-violet-50 hover:text-violet-700"
                          onClick={() => {
                            setQuery(suggestion);
                            setError(null);
                          }}
                        >
                          {suggestion}
                        </button>
                      ))}
                    </div>
                  </div>

                  <div className="rounded-2xl border border-stone-200 bg-white/90 p-5 shadow-sm">
                    <p className="text-xs font-semibold uppercase tracking-[0.16em] text-stone-400">
                      Jump straight in
                    </p>
                    <div className="mt-4 space-y-3">
                      {FEATURED_RESULTS.map((item) => (
                        <Link
                          key={item.href}
                          href={item.href}
                          onClick={closeSearch}
                          className="group flex items-center justify-between rounded-xl border border-stone-200 bg-stone-50/80 px-4 py-3 transition-colors hover:border-violet-200 hover:bg-violet-50/50"
                        >
                          <div>
                            <p className="text-sm font-medium text-stone-900 group-hover:text-violet-700">
                              {item.title}
                            </p>
                            <p className="mt-1 text-xs text-stone-500">{item.label}</p>
                          </div>
                          <ArrowIcon className="h-4 w-4 text-stone-300 transition-colors group-hover:text-violet-500" />
                        </Link>
                      ))}
                    </div>
                  </div>
                </div>
              ) : error ? (
                <EmptyState
                  title="Search hit a snag"
                  body={error}
                  query={trimmedQuery}
                  onReset={() => {
                    setError(null);
                    setQuery("");
                  }}
                />
              ) : activeResponse.results.length === 0 && isPending ? (
                <div className="space-y-3">
                  {[0, 1, 2].map((placeholder) => (
                    <div
                      key={placeholder}
                      className="rounded-2xl border border-stone-200 bg-white/90 p-5 shadow-sm"
                    >
                      <div className="h-3 w-28 rounded-full bg-stone-100" />
                      <div className="mt-4 h-5 w-48 rounded-full bg-stone-100" />
                      <div className="mt-3 space-y-2">
                        <div className="h-3 rounded-full bg-stone-100" />
                        <div className="h-3 w-5/6 rounded-full bg-stone-100" />
                      </div>
                    </div>
                  ))}
                </div>
              ) : activeResponse.results.length === 0 ? (
                <EmptyState
                  title="No docs matched that query"
                  body="Try a product area, API name, workflow, or capability. Multi-word phrases like 'home realm discovery' work well."
                  query={trimmedQuery}
                  onReset={() => setQuery("")}
                />
              ) : (
                <div className="space-y-3">
                  {activeResponse.results.map((result) => (
                    <SearchResultCard
                      key={result.href}
                      result={result}
                      query={trimmedQuery}
                      onSelect={closeSearch}
                    />
                  ))}
                </div>
              )}
            </div>
          </div>
        </div>
      ) : null}
    </>
  );
}

function SearchResultCard({
  result,
  query,
  onSelect,
}: {
  result: DocsSearchResult;
  query: string;
  onSelect: () => void;
}) {
  return (
    <Link
      href={result.href}
      onClick={onSelect}
      className="group block rounded-2xl border border-stone-200 bg-white/90 p-5 shadow-sm transition-all hover:-translate-y-0.5 hover:border-violet-200 hover:bg-violet-50/30 hover:shadow-[0_18px_40px_rgba(124,58,237,0.10)]"
    >
      <div className="flex items-start gap-4">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="rounded-full bg-violet-50 px-2.5 py-1 text-[11px] font-semibold uppercase tracking-[0.12em] text-violet-700">
              {result.sectionLabel}
            </span>
            {result.matchedFields.map((field) => (
              <span
                key={field}
                className="rounded-full bg-stone-100 px-2.5 py-1 text-[11px] font-medium text-stone-500"
              >
                {MATCH_LABELS[field]}
              </span>
            ))}
          </div>

          <h3 className="mt-3 text-lg font-semibold tracking-tight text-stone-950 transition-colors group-hover:text-violet-700">
            <HighlightText text={result.title} query={query} />
          </h3>

          <p className="mt-2 text-sm leading-6 text-stone-600">
            <HighlightText text={result.description} query={query} />
          </p>

          <p className="mt-3 text-sm leading-6 text-stone-500">
            <HighlightText text={result.snippet} query={query} />
          </p>

          <div className="mt-4 flex items-center gap-2 text-xs text-stone-400">
            <span className="rounded-full bg-stone-100 px-2 py-1 font-mono">
              {result.href}
            </span>
          </div>
        </div>

        <ArrowIcon className="mt-1 h-5 w-5 shrink-0 text-stone-300 transition-colors group-hover:text-violet-500" />
      </div>
    </Link>
  );
}

function EmptyState({
  title,
  body,
  query,
  onReset,
}: {
  title: string;
  body: string;
  query: string;
  onReset: () => void;
}) {
  return (
    <div className="rounded-2xl border border-dashed border-stone-200 bg-white/90 p-8 text-center shadow-sm">
      <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-2xl bg-violet-50 text-violet-600">
        <SearchIcon className="h-5 w-5" />
      </div>
      <h3 className="mt-4 text-lg font-semibold tracking-tight text-stone-950">
        {title}
      </h3>
      <p className="mt-2 text-sm leading-6 text-stone-500">{body}</p>
      <p className="mt-3 text-xs font-medium uppercase tracking-[0.16em] text-stone-400">
        Query: {query}
      </p>
      <button
        type="button"
        onClick={onReset}
        className="mt-5 rounded-full border border-stone-200 bg-stone-50 px-4 py-2 text-sm font-medium text-stone-700 transition-colors hover:border-violet-200 hover:bg-violet-50 hover:text-violet-700"
      >
        Clear search
      </button>
    </div>
  );
}

function HighlightText({ text, query }: { text: string; query: string }) {
  const tokens = useMemo(() => buildHighlightTokens(query), [query]);

  if (!text || tokens.length === 0) {
    return <>{text}</>;
  }

  const matcher = new RegExp(`(${tokens.map(escapeRegExp).join("|")})`, "gi");
  const parts = text.split(matcher);

  return (
    <>
      {parts.map((part, index) => {
        const isMatch = tokens.some((token) => token.toLowerCase() === part.toLowerCase());

        return isMatch ? (
          <mark
            key={`${part}-${index}`}
            className="rounded-md bg-violet-100 px-1 py-0.5 text-inherit"
          >
            {part}
          </mark>
        ) : (
          <span key={`${part}-${index}`}>{part}</span>
        );
      })}
    </>
  );
}

function buildHighlightTokens(query: string): string[] {
  return Array.from(
    new Set(
      query
        .split(/[^a-z0-9]+/i)
        .map((token) => token.trim())
        .filter((token) => token.length >= 2)
    )
  );
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function SearchIcon({ className }: { className?: string }) {
  return (
    <svg
      aria-hidden="true"
      viewBox="0 0 24 24"
      className={className}
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <path d="M21 21l-4.35-4.35m1.6-5.4a6.75 6.75 0 11-13.5 0 6.75 6.75 0 0113.5 0z" />
    </svg>
  );
}

function ArrowIcon({ className }: { className?: string }) {
  return (
    <svg
      aria-hidden="true"
      viewBox="0 0 24 24"
      className={className}
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <path d="M5 12h14M13 5l7 7-7 7" />
    </svg>
  );
}
