"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import BrandMark from "@/components/BrandMark";
import ThemeSwitcher from "@/components/ThemeSwitcher";
import { GitHubIcon } from "@/components/icons";

const navLinks = [
  { href: "/docs", label: "Docs" },
  { href: "/docs/reference/api-reference", label: "API" },
  { href: "/blog", label: "Blog" },
];

export default function Header() {
  const [isMenuOpen, setIsMenuOpen] = useState(false);
  const [scrolled, setScrolled] = useState(false);

  useEffect(() => {
    const handleScroll = () => setScrolled(window.scrollY > 10);
    handleScroll();
    window.addEventListener("scroll", handleScroll, { passive: true });
    return () => window.removeEventListener("scroll", handleScroll);
  }, []);

  useEffect(() => {
    if (!isMenuOpen) return;
    const handleKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") setIsMenuOpen(false);
    };
    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [isMenuOpen]);

  return (
    <header
      className={[
        "sticky top-0 z-50 w-full transition-all duration-200",
        scrolled
          ? "border-b bg-background/95 backdrop-blur supports-[backdrop-filter]:bg-background/60"
          : "bg-transparent",
      ].join(" ")}
    >
      <div className="mx-auto flex h-14 w-full max-w-[1400px] items-center justify-between px-6">
        <Link
          href="/"
          className="flex items-center gap-2.5 font-semibold text-foreground"
          onClick={() => setIsMenuOpen(false)}
        >
          <BrandMark className="h-7 w-7" />
          <span>SqlOS</span>
        </Link>

        <nav className="hidden items-center gap-1 md:flex">
          {navLinks.map((link) => (
            <Link
              key={link.href}
              href={link.href}
              className="rounded-md px-3 py-1.5 text-sm font-medium text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
            >
              {link.label}
            </Link>
          ))}
          <a
            href="https://github.com/ross-slaney/sqlos"
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex h-9 w-9 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
            aria-label="GitHub"
          >
            <GitHubIcon className="h-4 w-4" />
          </a>
          <ThemeSwitcher className="ml-1" />
          <Link
            href="/docs/getting-started"
            className="ml-1 rounded-md bg-primary px-3 py-1.5 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary/90"
          >
            Get started
          </Link>
        </nav>

        <div className="flex items-center gap-2 md:hidden">
          <ThemeSwitcher />
          <button
            type="button"
            className="inline-flex h-9 w-9 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground"
            onClick={() => setIsMenuOpen(!isMenuOpen)}
            aria-label={isMenuOpen ? "Close menu" : "Open menu"}
          >
            <svg
              className="h-5 w-5"
              fill="none"
              stroke="currentColor"
              strokeWidth="1.8"
              strokeLinecap="round"
              strokeLinejoin="round"
              viewBox="0 0 24 24"
            >
              {isMenuOpen ? (
                <path d="M6 6l12 12M18 6L6 18" />
              ) : (
                <path d="M4 7h16M4 12h16M4 17h16" />
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
              className="mt-2 rounded-md bg-primary px-3 py-2 text-center text-sm font-medium text-primary-foreground"
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
