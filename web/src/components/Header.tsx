"use client";

import Link from "next/link";
import { useEffect, useState } from "react";

const navLinks = [
  { href: "/docs", label: "Docs" },
  { href: "/docs/guides/reference/api-reference", label: "API" },
  { href: "/blog", label: "Blog" },
];

function BrandMark({ className }: { className?: string }) {
  return (
    <svg
      aria-hidden="true"
      viewBox="0 0 64 64"
      className={className}
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
    >
      <defs>
        <linearGradient
          id="sqlos-header-gradient"
          x1="10"
          y1="6"
          x2="54"
          y2="58"
          gradientUnits="userSpaceOnUse"
        >
          <stop stopColor="#8B5CF6" />
          <stop offset="1" stopColor="#6D28D9" />
        </linearGradient>
      </defs>
      <rect width="64" height="64" rx="16" fill="url(#sqlos-header-gradient)" />
      <path
        d="M14 14C24 8 44 8 52 22C44 18 28 17 18 20C15 18 14 16 14 14Z"
        fill="#FFFFFF"
        fillOpacity="0.12"
      />
      <text
        x="50%"
        y="52%"
        fill="#FFFFFF"
        fontFamily="Manrope, 'Helvetica Neue', Arial, sans-serif"
        fontSize="26"
        fontWeight="800"
        letterSpacing="-1.25"
        textAnchor="middle"
        dominantBaseline="middle"
      >
        SO
      </text>
    </svg>
  );
}

export default function Header() {
  const [isMenuOpen, setIsMenuOpen] = useState(false);

  useEffect(() => {
    const desktopQuery = window.matchMedia("(min-width: 1024px)");

    const handleDesktopChange = (event: MediaQueryListEvent) => {
      if (event.matches) {
        setIsMenuOpen(false);
      }
    };

    desktopQuery.addEventListener("change", handleDesktopChange);

    return () => {
      desktopQuery.removeEventListener("change", handleDesktopChange);
    };
  }, []);

  useEffect(() => {
    if (!isMenuOpen) {
      return;
    }

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setIsMenuOpen(false);
      }
    };

    window.addEventListener("keydown", handleKeyDown);

    return () => {
      window.removeEventListener("keydown", handleKeyDown);
    };
  }, [isMenuOpen]);

  return (
    <header className="sticky top-0 z-50 border-b border-stone-200/80 bg-[var(--background)]/90 backdrop-blur-md">
      <div className="px-6">
        <div className="mx-auto max-w-5xl">
          <nav className="flex h-14 items-center justify-between">
            <Link
              href="/"
              className="flex items-center gap-2 text-[16px] font-bold tracking-[-0.02em] text-stone-950"
              onClick={() => setIsMenuOpen(false)}
            >
              <BrandMark className="h-6 w-6 shrink-0" />
              SqlOS
            </Link>

            <div className="hidden items-center gap-1 lg:flex">
              {navLinks.map((link) => (
                <Link
                  key={link.href}
                  href={link.href}
                  className="rounded-md px-3 py-1.5 text-[13px] font-medium text-stone-500 transition-colors hover:text-stone-950"
                >
                  {link.label}
                </Link>
              ))}
              <Link
                href="/docs/guides/getting-started"
                className="ml-2 rounded-md bg-stone-950 px-3.5 py-1.5 text-[13px] font-semibold text-white transition hover:bg-stone-800"
              >
                Get started
              </Link>
            </div>

            <button
              type="button"
              aria-expanded={isMenuOpen}
              aria-controls="marketing-mobile-nav"
              aria-label={isMenuOpen ? "Close navigation menu" : "Open navigation menu"}
              className="inline-flex h-10 w-10 items-center justify-center rounded-md border border-stone-200 bg-white text-stone-700 transition-colors hover:border-stone-300 hover:text-stone-950 lg:hidden"
              onClick={() => setIsMenuOpen((open) => !open)}
            >
              <svg
                aria-hidden="true"
                viewBox="0 0 24 24"
                className="h-5 w-5"
                fill="none"
                stroke="currentColor"
                strokeWidth="1.8"
                strokeLinecap="round"
                strokeLinejoin="round"
              >
                {isMenuOpen ? (
                  <path d="M6 6l12 12M18 6L6 18" />
                ) : (
                  <path d="M4 7h16M4 12h16M4 17h16" />
                )}
              </svg>
            </button>
          </nav>

          {isMenuOpen ? (
            <div
              id="marketing-mobile-nav"
              className="border-t border-stone-200/80 py-3 lg:hidden"
            >
              <div className="flex flex-col gap-1">
                {navLinks.map((link) => (
                  <Link
                    key={link.href}
                    href={link.href}
                    className="rounded-md px-3 py-2 text-sm font-medium text-stone-700 transition-colors hover:bg-stone-100 hover:text-stone-950"
                    onClick={() => setIsMenuOpen(false)}
                  >
                    {link.label}
                  </Link>
                ))}
                <Link
                  href="/docs/guides/getting-started"
                  className="mt-2 inline-flex items-center justify-center rounded-md bg-stone-950 px-3.5 py-2 text-sm font-semibold text-white transition hover:bg-stone-800"
                  onClick={() => setIsMenuOpen(false)}
                >
                  Get started
                </Link>
              </div>
            </div>
          ) : null}
        </div>
      </div>
    </header>
  );
}
