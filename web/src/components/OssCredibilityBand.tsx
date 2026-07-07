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
    <section className="border-y bg-muted/30 px-6">
      <div className="mx-auto grid max-w-6xl divide-y divide-border sm:grid-cols-2 sm:divide-y-0 lg:grid-cols-4 lg:divide-x">
        <CredibilityItem
          label="Open source"
          title="SqlOS on GitHub"
          description={
            githubStars
              ? `${githubStars} stars — auth, SSO, and FGA in one .NET package.`
              : "Auth, SSO, and FGA in one .NET package."
          }
          href={GITHUB_REPO}
          external
          icon={<GitHubIcon className="h-4 w-4" />}
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
    "group flex flex-col px-1 py-6 transition-colors hover:bg-accent/30 sm:px-5 lg:py-8";

  const content = (
    <>
      <p className="font-mono text-[10px] uppercase tracking-[0.2em] text-primary/80">
        {label}
      </p>
      <div className="mt-2 flex items-center gap-2">
        {icon ? (
          <span className="text-muted-foreground transition-colors group-hover:text-primary">
            {icon}
          </span>
        ) : null}
        <h3 className="text-sm font-semibold text-foreground">{title}</h3>
      </div>
      <p className="mt-1.5 text-[13px] leading-6 text-muted-foreground">{description}</p>
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
