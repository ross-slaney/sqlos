import Link from "next/link";
import type { ReactNode } from "react";
import { GitHubIcon } from "@/components/icons";

const GITHUB_REPO = "https://github.com/ross-slaney/sqlos";
const PAPER_URL =
  "https://github.com/ross-slaney/sqlos/blob/main/paper/shrbac-compsac-2026.pdf";

type OssCredibilityBandProps = {
  githubStars?: string | null;
};

export default function OssCredibilityBand({ githubStars }: OssCredibilityBandProps) {
  return (
    <section className="border-y border-border/70 bg-background/72 px-6 py-8">
      <div className="mx-auto max-w-6xl">
        <div className="grid gap-6 sm:grid-cols-2 lg:grid-cols-4">
          <CredibilityItem
            label="Open source"
            title="SqlOS on GitHub"
            description={
              githubStars
                ? `${githubStars} stars for the self-hosted .NET auth stack.`
                : "Auth, SSO, and FGA in one .NET package."
            }
            href={GITHUB_REPO}
            external
            icon={<GitHubIcon className="h-5 w-5" />}
          />
          <CredibilityItem
            label="Research"
            title="SHRBAC / COMPSAC 2026"
            description="Hierarchical RBAC with SQL-native access checks and bounded query cost."
            href={PAPER_URL}
            external
          />
          <CredibilityItem
            label="Performance"
            title="3.47ms per page"
            description="At 1.2M rows with O(k·D) point checks — authorization in the query plan."
            href="/blog/developers-guide-to-hierarchical-rbac"
          />
          <CredibilityItem
            label="Example apps"
            title="Todo, Retail FGA, full stack"
            description="Runnable samples with hosted auth, headless flows, and EF Core filters."
            href="/docs/getting-started#run-the-right-sample"
          />
        </div>
      </div>
    </section>
  );
}

function CredibilityItem({
  label,
  title,
  description,
  href,
  external,
  icon,
}: {
  label: string;
  title: string;
  description: string;
  href: string;
  external?: boolean;
  icon?: ReactNode;
}) {
  const className =
    "group flex flex-col rounded-lg border border-border/70 bg-card/70 p-4 shadow-[0_14px_50px_oklch(0_0_0_/_0.18)] transition-colors hover:border-neon-cyan/45 hover:bg-card";

  const content = (
    <>
      <p className="text-[10px] font-semibold uppercase text-muted-foreground">
        {label}
      </p>
      <div className="mt-2 flex items-center gap-2">
        {icon ? (
          <span className="text-neon-cyan transition-colors group-hover:text-neon-green">
            {icon}
          </span>
        ) : null}
        <h3 className="text-sm font-semibold text-foreground">{title}</h3>
      </div>
      <p className="mt-1.5 text-sm leading-6 text-muted-foreground">{description}</p>
    </>
  );

  if (external) {
    return (
      <a href={href} target="_blank" rel="noopener noreferrer" className={className}>
        {content}
      </a>
    );
  }

  return (
    <Link href={href} className={className}>
      {content}
    </Link>
  );
}
