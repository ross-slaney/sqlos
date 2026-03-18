"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useState } from "react";
import type { DocGuide, DocSection } from "@/lib/docs";

interface SidebarSection {
  key: string;
  label: string;
  items: DocGuide[];
}

const SECTION_LABELS: Record<string, string> = {
  "": "Getting Started",
  authserver: "AuthServer",
  fga: "Fine-Grained Auth",
  reference: "Reference",
};

const SECTION_ORDER: (DocSection | "")[] = [
  "",
  "authserver",
  "fga",
  "reference",
];

interface DocsSidebarProps {
  guides: DocGuide[];
  variant?: "desktop" | "mobile";
  onNavigate?: () => void;
}

export default function DocsSidebar({
  guides,
  variant = "desktop",
  onNavigate,
}: DocsSidebarProps) {
  const pathname = usePathname();
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({});

  const sections: SidebarSection[] = SECTION_ORDER.map((key) => ({
    key: key || "",
    label: SECTION_LABELS[key || ""],
    items: guides.filter((g) => (g.section || "") === (key || "")),
  })).filter((s) => s.items.length > 0);

  const toggle = (key: string) =>
    setCollapsed((prev) => ({ ...prev, [key]: !prev[key] }));

  return (
    <nav
      aria-label="Documentation navigation"
      className={
        variant === "desktop"
          ? "hidden w-64 shrink-0 border-r border-stone-200 bg-white lg:block"
          : "w-full bg-white"
      }
    >
      <div
        className={
          variant === "desktop"
            ? "sticky top-14 h-[calc(100vh-3.5rem)] overflow-y-auto px-4 py-6 lg:top-16 lg:h-[calc(100vh-4rem)]"
            : "h-full overflow-y-auto px-4 py-4 sm:px-6"
        }
      >
        {sections.map((section) => (
          <div key={section.key} className="mb-6">
            <button
              onClick={() => toggle(section.key)}
              className="flex w-full items-center justify-between text-xs font-semibold uppercase tracking-wider text-stone-500 hover:text-stone-700"
            >
              {section.label}
              <svg
                className={`h-3.5 w-3.5 transition-transform ${collapsed[section.key] ? "" : "rotate-90"}`}
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
                strokeWidth={2}
              >
                <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
              </svg>
            </button>
            {!collapsed[section.key] && (
              <ul className="mt-2 space-y-0.5">
                {section.items.map((guide) => {
                  const href = `/docs/guides/${guide.slug}`;
                  const isActive = pathname === href;
                  return (
                    <li key={guide.slug}>
                      <Link
                        href={href}
                        onClick={onNavigate}
                        className={`block rounded-md px-3 py-1.5 text-sm transition-colors ${
                          isActive
                            ? "bg-violet-50 font-medium text-violet-700"
                            : "text-stone-600 hover:bg-stone-50 hover:text-stone-900"
                        }`}
                      >
                        {guide.title}
                      </Link>
                    </li>
                  );
                })}
              </ul>
            )}
          </div>
        ))}
      </div>
    </nav>
  );
}
