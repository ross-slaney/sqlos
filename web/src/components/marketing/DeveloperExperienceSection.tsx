import Link from "next/link";
import CodeTabs from "@/components/marketing/CodeTabs";
import SectionHeading from "@/components/marketing/SectionHeading";
import { ArrowIcon } from "@/components/icons";

export default function DeveloperExperienceSection() {
  return (
    <section className="border-t px-6 py-24 sm:py-28">
      <div className="mx-auto max-w-6xl">
        <div className="grid items-center gap-12 lg:grid-cols-[0.9fr_1.1fr] lg:gap-16">
          <div>
            <SectionHeading
              index="05"
              eyebrow="Developer experience"
              title="Three steps to a running auth server"
              description="Install the package, register SqlOS on your EF Core context, and map routes. Your dashboard, OAuth endpoints, hosted login, and FGA admin UI are live on the same origin as your API."
            />
            <Link
              href="/docs/getting-started"
              className="mt-7 inline-flex items-center gap-2 text-sm font-semibold text-primary transition-colors hover:text-primary/80"
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
