"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import type { DocsNavSection } from "@emcy/docs";

export default function DocsSidebarNav({
  navigation,
}: {
  navigation: DocsNavSection[];
}) {
  const pathname = usePathname();

  return (
    <nav aria-label="Documentation" className="flex flex-col gap-[22px]">
      {navigation.map((section) => (
        <div key={section.key}>
          <p className="mb-2 flex items-baseline justify-between px-2.5 text-xs font-bold uppercase tracking-[0.055em] text-foreground">
            {section.label}
            {section.items.length > 6 && (
              <span className="text-[11px] font-medium normal-case tracking-normal text-muted-foreground/70">
                {section.items.length}
              </span>
            )}
          </p>
          <ul>
            {section.items.map((item) => {
              const isActive = pathname === item.href;
              return (
                <li key={item.href}>
                  <Link
                    href={item.href}
                    aria-current={isActive ? "page" : undefined}
                    className={[
                      "block rounded-[4px] px-2.5 py-[6px] text-[13.5px] font-medium leading-[1.35] transition-colors",
                      isActive
                        ? "bg-accent font-semibold text-accent-foreground"
                        : "text-muted-foreground hover:bg-secondary hover:text-foreground",
                    ].join(" ")}
                  >
                    {item.title}
                  </Link>
                </li>
              );
            })}
          </ul>
        </div>
      ))}
    </nav>
  );
}
