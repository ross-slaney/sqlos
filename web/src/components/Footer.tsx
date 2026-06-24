import Link from "next/link";
import { Chip } from "@heroui/react";
import BrandMark from "@/components/BrandMark";
import { GitHubIcon } from "@/components/icons";

const productLinks = [
  { href: "/docs", label: "Documentation" },
  { href: "/docs/getting-started", label: "Getting started" },
  { href: "/docs/reference/api-reference", label: "API reference" },
  { href: "/blog", label: "Blog" },
];

const exampleLinks = [
  { href: "/docs/getting-started#run-the-right-sample", label: "Todo sample" },
  { href: "/docs/authserver/todo-sample", label: "Example stack guide" },
  { href: "/docs/fga/overview", label: "FGA overview" },
  { href: "/docs/guides/index", label: "Guides" },
];

const communityLinks = [
  {
    href: "https://github.com/ross-slaney/sqlos",
    label: "GitHub",
    external: true,
  },
  {
    href: "https://github.com/ross-slaney/sqlos/blob/main/paper/shrbac-compsac-2026.pdf",
    label: "SHRBAC paper",
    external: true,
  },
  {
    href: "https://www.nuget.org/packages/SqlOS",
    label: "NuGet",
    external: true,
  },
];

export default function Footer() {
  const currentYear = new Date().getFullYear();

  return (
    <footer className="border-t border-border/70 bg-background/92">
      <div className="mx-auto max-w-[1400px] px-6 py-12">
        <div className="grid gap-10 sm:grid-cols-2 lg:grid-cols-4">
          <div>
            <Link href="/" className="flex items-center gap-2.5 font-semibold text-foreground">
              <BrandMark className="h-7 w-7" />
              <span>SqlOS</span>
            </Link>
            <p className="mt-3 max-w-xs text-sm leading-6 text-muted-foreground">
              A .NET package that adds OAuth endpoints, a hosted AuthPage, FGA policy, dashboard
              routes, and SQL-backed state to an ASP.NET application.
            </p>
            <div className="mt-4 flex flex-wrap gap-2">
              <Chip size="sm" variant="soft" color="accent" className="border border-neon-cyan/25 bg-neon-cyan/10 text-neon-cyan">
                self-hosted
              </Chip>
              <Chip size="sm" variant="soft" color="success" className="border border-neon-green/25 bg-neon-green/10 text-neon-green">
                SQL-backed
              </Chip>
            </div>
          </div>

          <FooterColumn title="Product" links={productLinks} />
          <FooterColumn title="Examples" links={exampleLinks} />
          <FooterColumn title="Community" links={communityLinks} />
        </div>

        <div className="mt-10 flex flex-col items-center justify-between gap-4 border-t border-border/70 pt-8 sm:flex-row">
          <p className="text-sm text-muted-foreground">
            &copy; {currentYear} SqlOS. All rights reserved.
          </p>
          <a
            href="https://github.com/ross-slaney/sqlos"
            target="_blank"
            rel="noopener noreferrer"
            className="text-muted-foreground transition-colors hover:text-neon-cyan"
            aria-label="GitHub"
          >
            <GitHubIcon className="h-5 w-5" />
          </a>
        </div>
      </div>
    </footer>
  );
}

function FooterColumn({
  title,
  links,
}: {
  title: string;
  links: { href: string; label: string; external?: boolean }[];
}) {
  return (
    <div>
      <h3 className="text-xs font-semibold uppercase text-muted-foreground">
        {title}
      </h3>
      <ul className="mt-3 space-y-2">
        {links.map((link) => (
          <li key={link.href}>
            {link.external ? (
              <a
                href={link.href}
                target="_blank"
                rel="noopener noreferrer"
                className="text-sm text-foreground/80 transition-colors hover:text-neon-cyan"
              >
                {link.label}
              </a>
            ) : (
              <Link
                href={link.href}
                className="text-sm text-foreground/80 transition-colors hover:text-neon-cyan"
              >
                {link.label}
              </Link>
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}
