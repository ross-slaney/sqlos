"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import {
  useCallback,
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
  useTransition,
} from "react";
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

const PANEL_RESULT_LIMIT = 8;

const MATCH_LABELS: Record<DocsSearchResult["matchedFields"][number], string> = {
  title: "Title",
  description: "Summary",
  content: "Content",
};

export default function DocsSearch({ variant = "desktop" }: DocsSearchProps) {
  const pathname = usePathname();
  const router = useRouter();
  const searchId = useId();
  const listboxId = `${searchId}-results`;

  const containerRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const requestIdRef = useRef(0);
  const resultRefs = useRef<Array<HTMLAnchorElement | null>>([]);

  const [isOpen, setIsOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [response, setResponse] = useState<DocsSearchResponse>(EMPTY_RESPONSE);
  const [error, setError] = useState<string | null>(null);
  const [activeResultHref, setActiveResultHref] = useState<string | null>(null);
  const [isPending, startTransition] = useTransition();

  const trimmedQuery = query.trim();
  const activeResponse = trimmedQuery.length >= 2 ? response : EMPTY_RESPONSE;
  const hasFreshResults =
    trimmedQuery.length >= 2 && activeResponse.query === trimmedQuery;
  const visibleResults = hasFreshResults
    ? activeResponse.results.slice(0, PANEL_RESULT_LIMIT)
    : [];
  const isSearching =
    trimmedQuery.length >= 2 && !error && (isPending || !hasFreshResults);
  const activeResultIndex = activeResultHref
    ? visibleResults.findIndex((result) => result.href === activeResultHref)
    : -1;
  const selectedResultIndex =
    activeResultIndex >= 0 ? activeResultIndex : visibleResults.length > 0 ? 0 : -1;
  const selectedResult =
    selectedResultIndex >= 0 ? visibleResults[selectedResultIndex] : null;
  const showPanel = isOpen && trimmedQuery.length > 0;

  const closePanel = useCallback(
    ({
      blur = false,
      clearQuery = false,
    }: {
      blur?: boolean;
      clearQuery?: boolean;
    } = {}) => {
      requestIdRef.current += 1;
      setIsOpen(false);
      setActiveResultHref(null);

      if (clearQuery) {
        setQuery("");
        setResponse(EMPTY_RESPONSE);
        setError(null);
      }

      if (blur) {
        window.requestAnimationFrame(() => {
          inputRef.current?.blur();
        });
      }
    },
    []
  );

  const focusSearch = useCallback(() => {
    setIsOpen(true);
    window.requestAnimationFrame(() => {
      const input = inputRef.current;
      if (!input) {
        return;
      }

      input.focus();
      input.select();
    });
  }, []);

  const clearSearch = useCallback(() => {
    requestIdRef.current += 1;
    setQuery("");
    setResponse(EMPTY_RESPONSE);
    setError(null);
    setActiveResultHref(null);
    setIsOpen(true);
    window.requestAnimationFrame(() => {
      inputRef.current?.focus();
    });
  }, []);

  useEffect(() => {
    const frameId = window.requestAnimationFrame(() => {
      requestIdRef.current += 1;
      setIsOpen(false);
      setQuery("");
      setResponse(EMPTY_RESPONSE);
      setError(null);
      setActiveResultHref(null);
    });

    return () => {
      window.cancelAnimationFrame(frameId);
    };
  }, [pathname]);

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
      focusSearch();
    };

    window.addEventListener("keydown", handleKeyDown);

    return () => {
      window.removeEventListener("keydown", handleKeyDown);
    };
  }, [focusSearch, variant]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        closePanel({ blur: true });
      }
    };

    window.addEventListener("keydown", handleKeyDown);

    return () => {
      window.removeEventListener("keydown", handleKeyDown);
    };
  }, [closePanel, isOpen]);

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    const handlePointerDown = (event: PointerEvent) => {
      const target = event.target;
      if (!(target instanceof Node)) {
        return;
      }

      if (!containerRef.current?.contains(target)) {
        closePanel();
      }
    };

    document.addEventListener("pointerdown", handlePointerDown);

    return () => {
      document.removeEventListener("pointerdown", handlePointerDown);
    };
  }, [closePanel, isOpen]);

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
    }, 220);

    return () => {
      window.clearTimeout(timeoutId);
    };
  }, [isOpen, trimmedQuery, startTransition]);

  useEffect(() => {
    if (!showPanel || selectedResultIndex < 0) {
      return;
    }

    resultRefs.current[selectedResultIndex]?.scrollIntoView({
      block: "nearest",
    });
  }, [selectedResultIndex, showPanel]);

  const resultSummary = useMemo(() => {
    if (trimmedQuery.length < 2) {
      return "Type at least 2 characters to search titles, guides, APIs, and code snippets.";
    }

    if (error) {
      return error;
    }

    if (isSearching) {
      return `Searching for "${trimmedQuery}"...`;
    }

    if (activeResponse.total === 0) {
      return `No docs matched "${trimmedQuery}".`;
    }

    const resultWord = activeResponse.total === 1 ? "result" : "results";
    const truncatedNotice =
      activeResponse.total > visibleResults.length
        ? ` Showing the top ${visibleResults.length}.`
        : "";

    return `${activeResponse.total} ${resultWord} for "${activeResponse.query}".${truncatedNotice}`;
  }, [
    activeResponse.query,
    activeResponse.total,
    error,
    isSearching,
    trimmedQuery,
    visibleResults.length,
  ]);

  const handleInputBlur = () => {
    window.requestAnimationFrame(() => {
      const activeElement = document.activeElement;

      if (activeElement instanceof Node && containerRef.current?.contains(activeElement)) {
        return;
      }

      closePanel();
    });
  };

  return (
    <div ref={containerRef} className="relative w-full overflow-visible">
      <div
        className={`flex items-center gap-3 rounded-xl border bg-white/95 px-3 shadow-[0_8px_24px_rgba(28,25,23,0.06)] transition-all focus-within:border-stone-300 focus-within:shadow-[0_12px_30px_rgba(28,25,23,0.08)] ${
          isOpen ? "border-stone-300" : "border-stone-200"
        } ${variant === "desktop" ? "h-11" : "h-10"}`}
      >
        <SearchIcon className="h-4 w-4 shrink-0 text-stone-400" />
        <input
          ref={inputRef}
          type="search"
          value={query}
          role="combobox"
          aria-autocomplete="list"
          aria-expanded={showPanel}
          aria-controls={showPanel ? listboxId : undefined}
          aria-activedescendant={
            selectedResultIndex >= 0
              ? `${searchId}-result-${selectedResultIndex}`
              : undefined
          }
          aria-label="Search SqlOS docs"
          autoComplete="off"
          autoCapitalize="none"
          spellCheck={false}
          placeholder={variant === "desktop" ? "Search the docs..." : "Search docs..."}
          className="w-full bg-transparent text-sm text-stone-950 outline-none placeholder:text-stone-400"
          onFocus={() => setIsOpen(true)}
          onBlur={handleInputBlur}
          onChange={(event) => {
            const nextValue = event.target.value;
            requestIdRef.current += 1;
            setQuery(nextValue);
            setError(null);
            setActiveResultHref(null);
            setIsOpen(true);
          }}
          onKeyDown={(event) => {
            if (event.key === "ArrowDown" && visibleResults.length > 0) {
              event.preventDefault();
              const nextIndex =
                selectedResultIndex >= visibleResults.length - 1
                  ? 0
                  : selectedResultIndex + 1;
              setActiveResultHref(visibleResults[nextIndex]?.href ?? null);
              return;
            }

            if (event.key === "ArrowUp" && visibleResults.length > 0) {
              event.preventDefault();
              const nextIndex =
                selectedResultIndex <= 0
                  ? visibleResults.length - 1
                  : selectedResultIndex - 1;
              setActiveResultHref(visibleResults[nextIndex]?.href ?? null);
              return;
            }

            if (event.key === "Home" && visibleResults.length > 0) {
              event.preventDefault();
              setActiveResultHref(visibleResults[0]?.href ?? null);
              return;
            }

            if (event.key === "End" && visibleResults.length > 0) {
              event.preventDefault();
              setActiveResultHref(
                visibleResults[visibleResults.length - 1]?.href ?? null
              );
              return;
            }

            if (event.key === "Enter" && selectedResult) {
              event.preventDefault();
              closePanel({ clearQuery: true, blur: true });
              router.push(selectedResult.href);
            }
          }}
        />

        {isSearching ? (
          <div className="h-4 w-4 rounded-full border-2 border-violet-200 border-t-violet-600 animate-spin" />
        ) : query.length > 0 ? (
          <button
            type="button"
            onMouseDown={(event) => event.preventDefault()}
            onClick={clearSearch}
            aria-label="Clear search"
            className="inline-flex h-7 w-7 items-center justify-center rounded-full text-stone-400 transition-colors hover:bg-stone-100 hover:text-stone-700"
          >
            <CloseIcon className="h-3.5 w-3.5" />
          </button>
        ) : variant === "desktop" ? (
          <kbd className="hidden rounded-md border border-stone-200 bg-stone-50 px-1.5 py-0.5 text-[11px] font-medium text-stone-400 lg:inline-flex">
            ⌘K
          </kbd>
        ) : null}
      </div>

      {showPanel ? (
        <div className="absolute inset-x-0 top-[calc(100%+0.5rem)] z-50 animate-searchPanelIn">
          <div className="overflow-hidden rounded-2xl border border-stone-200/80 bg-white/95 shadow-[0_24px_70px_rgba(28,25,23,0.14)] ring-1 ring-black/5 backdrop-blur-sm">
            <div className="flex items-center justify-between gap-3 border-b border-stone-200/80 px-4 py-3">
              <p className="min-w-0 text-xs font-medium text-stone-500">
                {resultSummary}
              </p>
              {variant === "desktop" && visibleResults.length > 0 ? (
                <span className="hidden text-[11px] font-medium text-stone-400 xl:inline">
                  ↑↓ to navigate
                </span>
              ) : null}
            </div>

            <div className="max-h-[min(65vh,30rem)] overflow-y-auto p-2">
              {trimmedQuery.length < 2 ? (
                <SearchPanelMessage
                  title="Keep typing"
                  body="Results will appear right under the search bar as soon as your query is specific enough."
                />
              ) : error ? (
                <SearchPanelMessage
                  title="Search hit a snag"
                  body={error}
                  actionLabel="Clear search"
                  onAction={clearSearch}
                />
              ) : isSearching ? (
                <SearchLoadingState />
              ) : visibleResults.length === 0 ? (
                <SearchPanelMessage
                  title="No docs matched that query"
                  body="Try a product area, API name, workflow, or capability."
                  actionLabel="Clear search"
                  onAction={clearSearch}
                />
              ) : (
                <div
                  id={listboxId}
                  role="listbox"
                  aria-label="Documentation search results"
                  className="space-y-1.5"
                >
                  {visibleResults.map((result, index) => (
                    <SearchResultRow
                      key={result.href}
                      id={`${searchId}-result-${index}`}
                      result={result}
                      query={trimmedQuery}
                      isActive={index === selectedResultIndex}
                      resultRef={(node) => {
                        resultRefs.current[index] = node;
                      }}
                      onMouseEnter={() => setActiveResultHref(result.href)}
                      onSelect={() => closePanel({ clearQuery: true })}
                    />
                  ))}
                </div>
              )}
            </div>
          </div>
        </div>
      ) : null}
    </div>
  );
}

function SearchResultRow({
  id,
  result,
  query,
  isActive,
  resultRef,
  onMouseEnter,
  onSelect,
}: {
  id: string;
  result: DocsSearchResult;
  query: string;
  isActive: boolean;
  resultRef?: (node: HTMLAnchorElement | null) => void;
  onMouseEnter?: () => void;
  onSelect: () => void;
}) {
  const previewText = getPreviewText(result);

  return (
    <Link
      id={id}
      ref={resultRef}
      role="option"
      aria-selected={isActive}
      href={result.href}
      onClick={onSelect}
      onMouseEnter={onMouseEnter}
      className={`group block rounded-xl border px-4 py-3 transition-all ${
        isActive
          ? "border-stone-200 bg-stone-50 shadow-[0_12px_28px_rgba(28,25,23,0.06)]"
          : "border-transparent bg-white hover:border-stone-200 hover:bg-stone-50/80"
      }`}
    >
      <div className="flex items-start gap-3">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="rounded-full bg-stone-100 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-[0.12em] text-stone-700">
              {result.sectionLabel}
            </span>
            {result.matchedFields.slice(0, 2).map((field) => (
              <span
                key={field}
                className="rounded-full bg-stone-100 px-2 py-0.5 text-[10px] font-medium text-stone-500"
              >
                {MATCH_LABELS[field]}
              </span>
            ))}
          </div>

          <p className="mt-2 text-sm font-semibold tracking-tight text-stone-950">
            <HighlightText text={result.title} query={query} />
          </p>

          {previewText ? (
            <p className="mt-1 text-sm leading-6 text-stone-600">
              <HighlightText text={previewText} query={query} />
            </p>
          ) : null}

          <p className="mt-2 truncate text-xs font-mono text-stone-400">{result.href}</p>
        </div>

        <ArrowIcon
          className={`mt-1 h-4 w-4 shrink-0 transition-colors ${
            isActive ? "text-stone-500" : "text-stone-300 group-hover:text-stone-500"
          }`}
        />
      </div>
    </Link>
  );
}

function SearchPanelMessage({
  title,
  body,
  actionLabel,
  onAction,
}: {
  title: string;
  body: string;
  actionLabel?: string;
  onAction?: () => void;
}) {
  return (
    <div className="rounded-xl bg-stone-50/80 px-4 py-5">
      <p className="text-sm font-semibold text-stone-900">{title}</p>
      <p className="mt-1 text-sm leading-6 text-stone-500">{body}</p>
      {actionLabel && onAction ? (
        <button
          type="button"
          onClick={onAction}
          className="mt-3 rounded-full border border-stone-200 bg-white px-3 py-1.5 text-xs font-medium text-stone-700 transition-colors hover:border-violet-200 hover:text-violet-700"
        >
          {actionLabel}
        </button>
      ) : null}
    </div>
  );
}

function SearchLoadingState() {
  return (
    <div className="space-y-1.5">
      {[0, 1, 2].map((placeholder) => (
        <div
          key={placeholder}
          className="rounded-xl border border-stone-200/80 bg-white px-4 py-3"
        >
          <div className="h-2.5 w-20 rounded-full bg-stone-100" />
          <div className="mt-3 h-4 w-40 rounded-full bg-stone-100" />
          <div className="mt-2 h-3 w-5/6 rounded-full bg-stone-100" />
        </div>
      ))}
    </div>
  );
}

function getPreviewText(result: DocsSearchResult): string {
  const source = result.matchedFields.includes("content")
    ? result.snippet || result.description
    : result.description || result.snippet;

  return normalizeWhitespace(source);
}

function normalizeWhitespace(text: string): string {
  return text.replace(/\s+/g, " ").trim();
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
            className="rounded-md bg-stone-200/80 px-1 py-0.5 text-inherit"
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

function CloseIcon({ className }: { className?: string }) {
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
      <path d="M6 6l12 12M18 6L6 18" />
    </svg>
  );
}
