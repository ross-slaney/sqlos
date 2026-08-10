"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import BrandMark from "@/components/BrandMark";
import { GitHubIcon } from "@/components/icons";

const navLinks = [
  { href: "/#features", label: "Product" },
  { href: "/docs", label: "Docs" },
  { href: "/docs/reference/api-reference", label: "API Reference" },
  { href: "/blog", label: "Blog" },
];

export default function Header() {
  const [isMenuOpen, setIsMenuOpen] = useState(false);

  useEffect(() => {
    if (!isMenuOpen) return;
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") setIsMenuOpen(false);
    };
    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [isMenuOpen]);

  return (
    <header className="sticky top-0 z-50 w-full border-b bg-background/80 backdrop-blur-md supports-[backdrop-filter]:bg-background/80">
      <div className="mx-auto flex h-[62px] w-full max-w-[1160px] items-center gap-7 px-7">
        <Link
          href="/"
          className="flex items-center gap-2.5 text-base font-bold tracking-tight text-foreground"
          onClick={() => setIsMenuOpen(false)}
        >
          <BrandMark className="h-[26px] w-[26px] drop-shadow-[0_2px_6px_rgba(79,70,229,0.35)]" />
          <span>SqlOS</span>
          <span className="rounded-full bg-accent px-2 py-0.5 text-[11px] font-semibold tracking-normal text-accent-foreground">
            v3.24
          </span>
        </Link>

        <nav className="hidden items-center gap-6 md:flex">
          {navLinks.map((link) => (
            <Link
              key={link.href}
              href={link.href}
              className="text-sm font-medium text-foreground/70 transition-colors hover:text-foreground"
            >
              {link.label}
            </Link>
          ))}
        </nav>

        <div className="ml-auto hidden items-center gap-4 md:flex">
          <a
            href="https://github.com/ross-slaney/sqlos"
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-1.5 text-sm font-medium text-foreground/70 transition-colors hover:text-foreground"
          >
            <GitHubIcon className="h-4 w-4" />
            GitHub
          </a>
          <Link
            href="/docs/getting-started"
            className="inline-flex items-center gap-2 whitespace-nowrap rounded-[9px] bg-primary px-4 py-2 text-sm font-semibold text-primary-foreground shadow-[0_1px_2px_rgba(79,70,229,0.35),inset_0_1px_0_rgba(255,255,255,0.15)] transition-colors hover:bg-[#4338ca]"
          >
            Get started
          </Link>
        </div>

        <div className="ml-auto flex items-center gap-2 md:hidden">
          <button
            type="button"
            className="inline-flex h-9 w-9 items-center justify-center rounded-md border text-foreground"
            onClick={() => setIsMenuOpen(!isMenuOpen)}
            aria-label={isMenuOpen ? "Close menu" : "Open menu"}
          >
            <svg
              className="h-[18px] w-[18px]"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              strokeLinejoin="round"
              viewBox="0 0 24 24"
            >
              {isMenuOpen ? (
                <path d="M6 6l12 12M18 6L6 18" />
              ) : (
                <path d="M3 6h18M3 12h18M3 18h18" />
              )}
            </svg>
          </button>
        </div>
      </div>

      {isMenuOpen && (
        <div className="border-t bg-background p-4 md:hidden">
          <nav className="flex flex-col gap-1">
            {navLinks.map((link) => (
              <Link
                key={link.href}
                href={link.href}
                className="rounded-md px-3 py-2 text-sm font-medium text-foreground transition-colors hover:bg-accent"
                onClick={() => setIsMenuOpen(false)}
              >
                {link.label}
              </Link>
            ))}
            <a
              href="https://github.com/ross-slaney/sqlos"
              target="_blank"
              rel="noopener noreferrer"
              className="rounded-md px-3 py-2 text-sm font-medium text-foreground transition-colors hover:bg-accent"
            >
              GitHub
            </a>
            <Link
              href="/docs/getting-started"
              className="mt-2 rounded-[9px] bg-primary px-3 py-2 text-center text-sm font-semibold text-primary-foreground"
              onClick={() => setIsMenuOpen(false)}
            >
              Get started
            </Link>
          </nav>
        </div>
      )}
    </header>
  );
}
