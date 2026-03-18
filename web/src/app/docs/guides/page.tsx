import Link from "next/link";
import { getAllGuides, DocSection } from "@/lib/docs";

export const metadata = {
  title: "Documentation - SqlOS",
  description:
    "SqlOS documentation: getting started, AuthServer guides, FGA guides, and API reference.",
};

const SECTION_META: Record<string, { label: string; description: string }> = {
  "": {
    label: "Getting Started",
    description: "Install, configure, and run the SqlOS example stack.",
  },
  authserver: {
    label: "AuthServer",
    description:
      "Organizations, users, sessions, OIDC providers, SAML SSO, and the auth dashboard.",
  },
  fga: {
    label: "Fine-Grained Authorization",
    description:
      "Resource hierarchy, grants, roles, permissions, and EF Core query-time filtering.",
  },
  reference: {
    label: "Reference",
    description: "SDK methods, API endpoints, and contract types.",
  },
};

const SECTION_ORDER: (DocSection | "")[] = [
  "",
  "authserver",
  "fga",
  "reference",
];

export default function GuidesPage() {
  const guides = getAllGuides();

  const grouped = SECTION_ORDER.map((sectionKey) => {
    const key = sectionKey || "";
    const meta = SECTION_META[key];
    const items = guides.filter((g) => (g.section || "") === key);
    return { key, ...meta, items };
  }).filter((s) => s.items.length > 0);

  return (
    <div className="min-w-0 flex-1 px-4 py-8 sm:px-6 lg:px-12 lg:py-10">
      <div className="max-w-3xl">
        <h1 className="text-3xl font-semibold tracking-tight text-stone-950">
          SqlOS Documentation
        </h1>
        <p className="mt-3 text-base leading-7 text-stone-600">
          AuthServer for identity and sessions, FGA for hierarchical
          authorization, and a shared example stack.
        </p>
        <div className="mt-5 rounded-lg border border-stone-200 bg-stone-50 p-4 text-sm">
          <p className="font-medium text-stone-800">
            For agents: Start with the{" "}
            <Link
              href="/docs/guides/docs-index"
              className="text-violet-700 underline"
            >
              Documentation Index
            </Link>{" "}
            for a complete map of guides, URLs, and APIs.
          </p>
        </div>
      </div>

      {grouped.map((section) => (
        <div key={section.key} className="mt-12 max-w-4xl">
          <h2 className="text-xl font-semibold tracking-tight text-stone-950">
            {section.label}
          </h2>
          <p className="mt-1.5 text-sm text-stone-500">
            {section.description}
          </p>

          <div className="mt-5 grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
            {section.items.map((guide) => (
              <Link
                key={guide.slug}
                href={`/docs/guides/${guide.slug}`}
                className="group rounded-lg border border-stone-200 bg-white p-4 transition-colors hover:border-violet-200 hover:bg-violet-50/30"
              >
                <h3 className="text-sm font-semibold text-stone-950 group-hover:text-violet-700">
                  {guide.title}
                </h3>
                <p className="mt-1 text-xs leading-5 text-stone-500">
                  {guide.description}
                </p>
              </Link>
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}
