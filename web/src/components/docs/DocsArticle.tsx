import Link from "next/link";
import { Suspense } from "react";
import { DocsMdx } from "@emcy/docs";
import type { DocsEntry, DocsEntryMeta } from "@emcy/docs";
import DocsTocNav from "@/components/docs/DocsTocNav";
import HelpfulRow from "@/components/docs/HelpfulRow";

export default function DocsArticle({
  entry,
  previousEntry,
  nextEntry,
  sectionHref,
}: {
  entry: DocsEntry;
  previousEntry?: DocsEntryMeta | null;
  nextEntry?: DocsEntryMeta | null;
  sectionHref?: string;
}) {
  return (
    <div className="grid min-w-0 items-start xl:grid-cols-[minmax(0,1fr)_220px]">
      <article className="min-w-0 px-6 pb-[90px] pt-8 md:px-10 md:pt-10 lg:px-14 lg:pt-11">
        <div className="mb-[18px] text-[13px] text-muted-foreground/80">
          {entry.sectionLabel ? (
            <>
              {sectionHref ? (
                <Link
                  href={sectionHref}
                  className="transition-colors hover:text-foreground/80"
                >
                  {entry.sectionLabel}
                </Link>
              ) : (
                <span>{entry.sectionLabel}</span>
              )}
              <span className="mx-1.5">/</span>
              <span className="text-foreground/70">{entry.title}</span>
            </>
          ) : (
            <span className="text-foreground/70">Documentation</span>
          )}
        </div>

        <h1 className="mb-4 text-[30px] font-extrabold leading-[1.08] tracking-[-0.03em] text-foreground md:text-[38px]">
          {entry.title}
        </h1>
        {entry.description ? (
          <p className="mb-8 max-w-[64ch] text-lg leading-[1.55] text-muted-foreground">
            {entry.description}
          </p>
        ) : null}

        <Suspense
          fallback={<div className="h-40" aria-busy="true" aria-label="Loading" />}
        >
          <DocsMdx entry={entry} />
        </Suspense>

        {(previousEntry || nextEntry) && (
          <nav
            aria-label="Document pagination"
            className="mb-[30px] mt-14 grid gap-4 sm:grid-cols-2"
          >
            {previousEntry ? (
              <Link
                href={previousEntry.href}
                className="block rounded-[6px] border px-[18px] py-4 transition-all hover:border-border hover:shadow-sm"
              >
                <div className="mb-1 text-xs text-muted-foreground/80">
                  ← Previous
                </div>
                <div className="text-[15px] font-semibold text-foreground">
                  {previousEntry.title}
                </div>
              </Link>
            ) : (
              <span />
            )}
            {nextEntry ? (
              <Link
                href={nextEntry.href}
                className="block rounded-[6px] border px-[18px] py-4 transition-all hover:border-border hover:shadow-sm sm:text-right"
              >
                <div className="mb-1 text-xs text-muted-foreground/80">
                  Next →
                </div>
                <div className="text-[15px] font-semibold text-foreground">
                  {nextEntry.title}
                </div>
              </Link>
            ) : (
              <span />
            )}
          </nav>
        )}

        <HelpfulRow />
      </article>

      <aside className="sticky top-[60px] hidden max-h-[calc(100vh-60px)] overflow-y-auto border-l py-11 pl-6 pr-6 xl:block">
        <DocsTocNav headings={entry.headings} />
      </aside>
    </div>
  );
}
