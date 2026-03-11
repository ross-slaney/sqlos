"use client";

import { useEffect, useState } from "react";

interface TOCItem {
  id: string;
  text: string;
  level: number;
}

export default function OnThisPage() {
  const [headings, setHeadings] = useState<TOCItem[]>([]);
  const [activeId, setActiveId] = useState("");

  useEffect(() => {
    const article = document.querySelector("article");
    if (!article) return;

    const elements = article.querySelectorAll("h2, h3");
    const items: TOCItem[] = Array.from(elements).map((el) => ({
      id: el.id || slugify(el.textContent || ""),
      text: el.textContent || "",
      level: el.tagName === "H2" ? 2 : 3,
    }));

    elements.forEach((el) => {
      if (!el.id) el.id = slugify(el.textContent || "");
    });

    setHeadings(items);
  }, []);

  useEffect(() => {
    if (headings.length === 0) return;

    const observer = new IntersectionObserver(
      (entries) => {
        const visible = entries.filter((e) => e.isIntersecting);
        if (visible.length > 0) {
          setActiveId(visible[0].target.id);
        }
      },
      { rootMargin: "-80px 0px -60% 0px", threshold: 0.1 }
    );

    headings.forEach((h) => {
      const el = document.getElementById(h.id);
      if (el) observer.observe(el);
    });

    return () => observer.disconnect();
  }, [headings]);

  if (headings.length === 0) return null;

  return (
    <div className="w-56 shrink-0">
      <div className="sticky top-[89px] max-h-[calc(100vh-89px)] overflow-y-auto py-6">
        <p className="text-xs font-semibold uppercase tracking-wider text-stone-500">
          On this page
        </p>
        <ul className="mt-3 space-y-1.5">
          {headings.map((h) => (
            <li key={h.id}>
              <a
                href={`#${h.id}`}
                className={`block text-sm leading-snug transition-colors ${
                  h.level === 3 ? "pl-3" : ""
                } ${
                  activeId === h.id
                    ? "font-medium text-violet-700"
                    : "text-stone-500 hover:text-stone-800"
                }`}
              >
                {h.text}
              </a>
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}

function slugify(text: string): string {
  return text
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-|-$/g, "");
}
