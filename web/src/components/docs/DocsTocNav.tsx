"use client";

import { useEffect, useState } from "react";
import type { DocsHeading } from "@emcy/docs";

export default function DocsTocNav({ headings }: { headings: DocsHeading[] }) {
  const visible = headings.filter((h) => h.level === 2 || h.level === 3);
  const [activeId, setActiveId] = useState<string | null>(null);

  useEffect(() => {
    if (!visible.length) return;
    const spy = () => {
      const y = window.scrollY + 100;
      let current = visible[0].id;
      for (const h of visible) {
        const el = document.getElementById(h.id);
        if (el && el.offsetTop <= y) current = h.id;
      }
      setActiveId(current);
    };
    spy();
    window.addEventListener("scroll", spy, { passive: true });
    return () => window.removeEventListener("scroll", spy);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [headings]);

  if (!visible.length) return null;

  return (
    <nav aria-label="On this page">
      <div className="mb-3.5 text-[11.5px] font-bold uppercase tracking-[0.06em] text-muted-foreground/80">
        On this page
      </div>
      {visible.map((h) => {
        const isActive = h.id === activeId;
        return (
          <a
            key={h.id}
            href={`#${h.id}`}
            className={[
              "block border-l-2 py-[5px] text-[13px] leading-[1.4] transition-colors",
              h.level === 3 ? "pl-6 text-[12.5px]" : "pl-3",
              isActive
                ? "border-primary font-semibold text-accent-foreground"
                : "border-border text-muted-foreground hover:text-foreground",
            ].join(" ")}
          >
            {h.text}
          </a>
        );
      })}
    </nav>
  );
}
