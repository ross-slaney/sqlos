import Link from "next/link";
import BrandMark from "@/components/BrandMark";

const productLinks = [
  { href: "/#features", label: "Features" },
  { href: "/docs", label: "Documentation" },
  { href: "/docs/reference/api-reference", label: "API Reference" },
  { href: "/blog", label: "Blog" },
];

const exampleLinks = [
  { href: "/docs/getting-started", label: "Getting Started" },
  { href: "/docs/authserver/todo-sample", label: "Todo sample app" },
  { href: "/docs/quickstarts/add-to-app", label: "Quickstarts" },
  { href: "/docs/guides/index", label: "Guides" },
];

const communityLinks = [
  {
    href: "https://github.com/ross-slaney/sqlos",
    label: "GitHub",
    external: true,
  },
  {
    href: "https://www.nuget.org/packages/SqlOS",
    label: "NuGet",
    external: true,
  },
  { href: "/blog", label: "Blog" },
  {
    href: "https://github.com/ross-slaney/sqlos/blob/main/paper/shrbac-compsac-2026.pdf",
    label: "SHRBAC paper",
    external: true,
  },
];

export default function Footer() {
  const currentYear = new Date().getFullYear();

  return (
    <footer className="border-t bg-secondary/60">
      <div className="mx-auto max-w-[1160px] px-7 pb-10 pt-14">
        <div className="mb-10 grid gap-8 sm:grid-cols-2 lg:grid-cols-[1.6fr_1fr_1fr_1fr]">
          <div>
            <Link
              href="/"
              className="flex items-center gap-2.5 text-base font-bold tracking-tight text-foreground"
            >
              <BrandMark className="h-[26px] w-[26px]" />
              <span>SqlOS</span>
            </Link>
            <p className="mt-3.5 max-w-[30ch] text-[13.5px] leading-relaxed text-muted-foreground">
              Enterprise authentication and authorization, embedded in your SQL
              Server.
            </p>
          </div>

          <FooterColumn title="Product" links={productLinks} />
          <FooterColumn title="Examples" links={exampleLinks} />
          <FooterColumn title="Community" links={communityLinks} />
        </div>

        <div className="flex flex-wrap items-center justify-between gap-3 border-t pt-5 text-[13px] text-muted-foreground/80">
          <span>
            © {currentYear} SqlOS · SqlOS 3.24.1 · .NET 9 · EF Core 9 · SQL
            Server
          </span>
          <div className="flex gap-4">
            <a
              href="https://github.com/ross-slaney/sqlos"
              target="_blank"
              rel="noopener noreferrer"
              className="transition-colors hover:text-foreground"
            >
              GitHub
            </a>
            <a
              href="https://www.nuget.org/packages/SqlOS"
              target="_blank"
              rel="noopener noreferrer"
              className="transition-colors hover:text-foreground"
            >
              NuGet
            </a>
          </div>
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
      <h3 className="mb-3.5 text-xs font-semibold uppercase tracking-[0.06em] text-muted-foreground/80">
        {title}
      </h3>
      <ul className="space-y-2.5">
        {links.map((link) => (
          <li key={`${link.href}-${link.label}`}>
            {link.external ? (
              <a
                href={link.href}
                target="_blank"
                rel="noopener noreferrer"
                className="text-sm text-foreground/70 transition-colors hover:text-foreground"
              >
                {link.label}
              </a>
            ) : (
              <Link
                href={link.href}
                className="text-sm text-foreground/70 transition-colors hover:text-foreground"
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
