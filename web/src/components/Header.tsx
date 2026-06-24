"use client";

import Link from "next/link";
import { Button, Chip } from "@heroui/react";
import { Menu, X } from "lucide-react";
import { useEffect, useState } from "react";
import BrandMark from "@/components/BrandMark";
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
          ? "border-b border-border/70 bg-background/88 shadow-[0_0_34px_oklch(0.82_0.17_200_/_0.08)] backdrop-blur-xl"
          : "bg-background/35 backdrop-blur-sm",
      ].join(" ")}
    >
      <div className="relative mx-auto flex h-14 w-full max-w-[1400px] items-center justify-between px-6">
        <Link
          href="/"
          className="flex min-w-0 items-center gap-2.5 font-semibold text-foreground"
          onClick={() => setIsMenuOpen(false)}
        >
          <BrandMark className="h-7 w-7" />
          <span>SqlOS</span>
          <Chip
            size="sm"
            variant="soft"
            color="success"
            className="hidden border border-neon-green/30 bg-neon-green/10 text-[10px] text-neon-green sm:inline-flex"
          >
            auth stack
          </Chip>
        </Link>

        <nav className="hidden items-center gap-1 lg:flex">
          {navLinks.map((link) => (
            <Link
              key={link.href}
              href={link.href}
              className="rounded-md px-3 py-1.5 text-sm font-medium text-muted-foreground transition-colors hover:bg-accent/10 hover:text-neon-cyan"
            >
              {link.label}
            </Link>
          ))}
          <a
            href="https://github.com/ross-slaney/sqlos"
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex h-9 w-9 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-accent/10 hover:text-neon-cyan"
            aria-label="GitHub"
          >
            <GitHubIcon className="h-4 w-4" />
          </a>
          <Link
            href="/docs/getting-started"
            className="ml-2 rounded-md border border-neon-green/40 bg-neon-green px-3 py-1.5 text-sm font-semibold text-background shadow-[0_0_22px_oklch(0.88_0.2_146_/_0.22)] transition-colors hover:bg-neon-cyan"
          >
            Start configuring
          </Link>
        </nav>

        <div className="absolute right-6 top-1/2 flex -translate-y-1/2 items-center gap-2 lg:hidden">
          <Button
            isIconOnly
            size="sm"
            variant="outline"
            className="shrink-0 border-neon-cyan/35 bg-card/70 text-neon-cyan"
            onPress={() => setIsMenuOpen(!isMenuOpen)}
            aria-label={isMenuOpen ? "Close menu" : "Open menu"}
          >
            {isMenuOpen ? <X className="h-4 w-4" /> : <Menu className="h-4 w-4" />}
          </Button>
        </div>
      </div>

      {isMenuOpen && (
        <div className="border-t border-border/70 bg-background/96 p-4 shadow-[0_24px_70px_oklch(0_0_0_/_0.42)] backdrop-blur-xl lg:hidden">
          <nav className="flex flex-col gap-1">
            {navLinks.map((link) => (
              <Link
                key={link.href}
                href={link.href}
                className="rounded-md px-3 py-2 text-sm font-medium text-foreground transition-colors hover:bg-accent/10 hover:text-neon-cyan"
                onClick={() => setIsMenuOpen(false)}
              >
                {link.label}
              </Link>
            ))}
            <a
              href="https://github.com/ross-slaney/sqlos"
              target="_blank"
              rel="noopener noreferrer"
              className="rounded-md px-3 py-2 text-sm font-medium text-foreground transition-colors hover:bg-accent/10 hover:text-neon-cyan"
            >
              GitHub
            </a>
            <Link
              href="/docs/getting-started"
              className="mt-2 rounded-md bg-neon-green px-3 py-2 text-center text-sm font-semibold text-background"
              onClick={() => setIsMenuOpen(false)}
            >
              Start configuring
            </Link>
          </nav>
        </div>
      )}
    </header>
  );
}
