import Image from "next/image";
import type { TableHTMLAttributes } from "react";

type VisualKind =
  | "one-app-topology"
  | "fga-list-query"
  | "auth0-vs-sqlos"
  | "workos-vs-sqlos"
  | "managed-vs-sqlos";

const visualCopy: Record<
  VisualKind,
  {
    title: string;
    caption: string;
    lanes: {
      label: string;
      tone: "primary" | "neutral" | "accent";
      nodes: string[];
    }[];
  }
> = {
  "one-app-topology": {
    title: "One app, one database, one auth surface",
    caption:
      "SqlOS lives inside the ASP.NET host. Your browser app gets OAuth tokens from /sqlos/auth, your API handles business routes, and SQL Server stores both app data and SqlOS tables.",
    lanes: [
      {
        label: "Browser app",
        tone: "accent",
        nodes: ["Hosted AuthPage", "PKCE client", "Bearer API calls"],
      },
      {
        label: "ASP.NET API",
        tone: "primary",
        nodes: ["/api/*", "/sqlos/auth/*", "/sqlos admin"],
      },
      {
        label: "SQL Server",
        tone: "neutral",
        nodes: ["Users + orgs", "Sessions + refresh tokens", "FGA resources + grants"],
      },
    ],
  },
  "fga-list-query": {
    title: "Authorized rows are filtered before pagination",
    caption:
      "The endpoint builds a normal EF query, then adds a SqlOS authorization expression. SQL Server evaluates the TVF predicate with your business filters, sort, and page size.",
    lanes: [
      {
        label: "Token subject",
        tone: "accent",
        nodes: ["sub: user_123", "org: acme", "permission: TODO_READ"],
      },
      {
        label: "EF Core",
        tone: "primary",
        nodes: [".Where(filter)", ".OrderBy(...)", ".Take(20)"],
      },
      {
        label: "SQL Server",
        tone: "neutral",
        nodes: ["Walk resource ancestors", "Match active grants", "Return authorized rows"],
      },
    ],
  },
  "auth0-vs-sqlos": {
    title: "Two authorization architectures for one app",
    caption:
      "Auth0 FGA centralizes relationship-based authorization in a separate FGA store. SqlOS keeps the resource tree and grants beside the EF data that list pages already query.",
    lanes: [
      {
        label: "Auth0 FGA",
        tone: "accent",
        nodes: ["Authorization model DSL", "Relationship tuples", "Check / ListObjects API"],
      },
      {
        label: "Your app",
        tone: "neutral",
        nodes: ["Sync object relationships", "Join FGA object IDs to rows", "Handle app data separately"],
      },
      {
        label: "SqlOS",
        tone: "primary",
        nodes: ["Resource rows in SQL Server", "Grants in the same transaction", "LINQ filter composes with rows"],
      },
    ],
  },
  "workos-vs-sqlos": {
    title: "Same hierarchy shape, different data boundary",
    caption:
      "WorkOS FGA is an external authorization API with resource instances registered in WorkOS. SqlOS uses the same hierarchy idea but keeps resource rows local to your database.",
    lanes: [
      {
        label: "WorkOS FGA",
        tone: "accent",
        nodes: ["Resource types in dashboard", "Register resource instances", "Authorization API checks"],
      },
      {
        label: "Shared model",
        tone: "neutral",
        nodes: ["Org -> workspace -> project", "Roles scoped to resources", "Permissions inherit downward"],
      },
      {
        label: "SqlOS",
        tone: "primary",
        nodes: ["Resource table in SQL Server", "EF query filters for lists", "FGA dashboard in your app"],
      },
    ],
  },
  "managed-vs-sqlos": {
    title: "The first fork for a B2B SaaS app",
    caption:
      "Managed identity products optimize for speed and outsourced operations. SqlOS optimizes for a .NET app that wants auth, orgs, SSO, and row-level authorization data under the same host and database.",
    lanes: [
      {
        label: "Managed login",
        tone: "accent",
        nodes: ["Hosted UI", "Vendor org dashboard", "SSO and MAU pricing"],
      },
      {
        label: "Your product",
        tone: "neutral",
        nodes: ["Org data model", "API authorization", "Customer data boundary"],
      },
      {
        label: "SqlOS",
        tone: "primary",
        nodes: ["Hosted AuthPage", "Orgs + invites in SQL Server", "FGA in EF queries"],
      },
    ],
  },
};

function toneClasses(tone: "primary" | "neutral" | "accent") {
  switch (tone) {
    case "primary":
      return "border-indigo-300 bg-indigo-50 text-indigo-950 dark:border-indigo-700/70 dark:bg-indigo-950/40 dark:text-indigo-100";
    case "accent":
      return "border-emerald-300 bg-emerald-50 text-emerald-950 dark:border-emerald-700/70 dark:bg-emerald-950/30 dark:text-emerald-100";
    default:
      return "border-zinc-300 bg-zinc-50 text-zinc-950 dark:border-zinc-700 dark:bg-zinc-900/70 dark:text-zinc-100";
  }
}

export function BlogVisual({ kind }: { kind: VisualKind }) {
  const visual = visualCopy[kind];

  return (
    <figure className="not-prose my-10 overflow-hidden rounded-lg border border-zinc-200 bg-white shadow-sm dark:border-zinc-800 dark:bg-zinc-950">
      <div className="border-b border-zinc-200 bg-zinc-50 px-5 py-4 dark:border-zinc-800 dark:bg-zinc-900">
        <h3 className="text-base font-semibold text-zinc-950 dark:text-white">
          {visual.title}
        </h3>
        <p className="mt-1 text-sm leading-6 text-zinc-600 dark:text-zinc-400">
          {visual.caption}
        </p>
      </div>
      <div className="grid gap-3 p-4 md:grid-cols-3">
        {visual.lanes.map((lane, index) => (
          <div
            key={lane.label}
            className={`relative rounded-lg border p-4 ${toneClasses(lane.tone)}`}
          >
            <div className="flex items-center gap-2">
              <span className="flex h-6 w-6 items-center justify-center rounded-full bg-white/75 text-xs font-semibold text-zinc-800 ring-1 ring-black/10 dark:bg-black/20 dark:text-zinc-100 dark:ring-white/10">
                {index + 1}
              </span>
              <h4 className="text-sm font-semibold">{lane.label}</h4>
            </div>
            <ul className="mt-4 space-y-2">
              {lane.nodes.map((node) => (
                <li
                  key={node}
                  className="rounded-md bg-white/70 px-3 py-2 text-sm ring-1 ring-black/5 dark:bg-black/20 dark:ring-white/10"
                >
                  {node}
                </li>
              ))}
            </ul>
          </div>
        ))}
      </div>
    </figure>
  );
}

export function BlogScreenshot({
  src,
  alt,
  caption,
}: {
  src: string;
  alt: string;
  caption?: string;
}) {
  return (
    <figure className="not-prose my-10 overflow-hidden rounded-lg border border-zinc-200 bg-white shadow-sm dark:border-zinc-800 dark:bg-zinc-950">
      <Image
        src={src}
        alt={alt}
        width={1440}
        height={900}
        unoptimized
        className="h-auto w-full"
      />
      {caption ? (
        <figcaption className="border-t border-zinc-200 bg-zinc-50 px-5 py-3 text-sm text-zinc-600 dark:border-zinc-800 dark:bg-zinc-900 dark:text-zinc-400">
          {caption}
        </figcaption>
      ) : null}
    </figure>
  );
}

export function BlogCallout({
  title,
  children,
}: {
  title: string;
  children: React.ReactNode;
}) {
  return (
    <aside className="not-prose my-8 rounded-lg border border-indigo-200 bg-indigo-50 p-5 text-indigo-950 dark:border-indigo-800 dark:bg-indigo-950/40 dark:text-indigo-100">
      <h3 className="text-sm font-semibold uppercase tracking-wide">{title}</h3>
      <div className="mt-2 text-sm leading-6 text-indigo-900 dark:text-indigo-100">
        {children}
      </div>
    </aside>
  );
}

function BlogTable(props: TableHTMLAttributes<HTMLTableElement>) {
  const { className, ...tableProps } = props;

  return (
    <div className="not-prose my-8 overflow-x-auto rounded-lg border border-zinc-200 dark:border-zinc-800">
      <table
        {...tableProps}
        className={`min-w-full border-collapse bg-white text-sm dark:bg-zinc-950 [&_td]:border-t [&_td]:border-zinc-200 [&_td]:px-4 [&_td]:py-3 [&_td]:align-top [&_td]:text-zinc-700 dark:[&_td]:border-zinc-800 dark:[&_td]:text-zinc-300 [&_th]:bg-zinc-50 [&_th]:px-4 [&_th]:py-3 [&_th]:text-left [&_th]:font-semibold [&_th]:text-zinc-950 dark:[&_th]:bg-zinc-900 dark:[&_th]:text-zinc-100 ${className ?? ""}`}
      />
    </div>
  );
}

export const blogMdxComponents = {
  BlogCallout,
  BlogScreenshot,
  table: BlogTable,
  BlogVisual,
};
