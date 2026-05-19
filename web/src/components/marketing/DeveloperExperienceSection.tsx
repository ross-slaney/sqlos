import Link from "next/link";
import CodeTabs from "@/components/marketing/CodeTabs";
import { ArrowIcon } from "@/components/icons";

export default function DeveloperExperienceSection() {
  return (
    <section className="border-t px-6 py-20 sm:py-24">
      <div className="mx-auto max-w-6xl">
        <div className="grid items-center gap-12 lg:grid-cols-[0.9fr_1.1fr] lg:gap-16">
          <div>
            <p className="text-[11px] font-semibold uppercase tracking-[0.16em] text-muted-foreground">
              Developer experience
            </p>
            <h2 className="mt-3 text-3xl font-semibold tracking-[-0.04em] text-foreground sm:text-4xl">
              Three steps to a running auth server
            </h2>
            <p className="mt-5 text-base leading-7 text-muted-foreground">
              Install the package, register SqlOS on your EF Core context, and map routes. Your dashboard,
              OAuth endpoints, hosted login, and FGA admin UI are live on the same origin as your API.
            </p>
            <Link
              href="/docs/getting-started"
              className="mt-6 inline-flex items-center gap-2 text-sm font-semibold text-primary transition-colors hover:text-primary/80"
            >
              Full getting started guide
              <ArrowIcon />
            </Link>
          </div>
          <CodeTabs />
        </div>
      </div>
    </section>
  );
}
