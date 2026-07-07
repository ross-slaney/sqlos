import Link from "next/link";
import SectionHeading from "@/components/marketing/SectionHeading";
import { exampleApps } from "@/components/marketing/constants";
import { ArrowIcon } from "@/components/icons";

export default function ExampleAppsSection() {
  return (
    <section className="border-t px-6 py-24 sm:py-28">
      <div className="mx-auto max-w-6xl">
        <SectionHeading
          index="06"
          eyebrow="Example apps"
          title="Run it, then fork it"
          description="Every sample ships in the repo with Aspire hosts, seeded clients, and docs that walk through the same flows you will use in production."
        />
        <div className="mt-12 grid gap-4 sm:grid-cols-3">
          {exampleApps.map((app, i) => (
            <Link
              key={app.title}
              href={app.href}
              className="group relative flex flex-col rounded-xl border bg-card/70 p-6 shadow-sm transition-all hover:-translate-y-0.5 hover:border-primary/40 hover:shadow-lg hover:shadow-primary/5"
            >
              <span className="font-mono text-[11px] text-primary/70">0{i + 1}</span>
              <h3 className="mt-3 text-base font-semibold tracking-tight text-foreground">
                {app.title}
              </h3>
              <p className="mt-2 flex-1 text-sm leading-6 text-muted-foreground">{app.description}</p>
              <span className="mt-5 inline-flex items-center gap-1 text-sm font-semibold text-primary">
                {app.cta}
                <ArrowIcon className="h-3.5 w-3.5 transition-transform group-hover:translate-x-0.5" />
              </span>
            </Link>
          ))}
        </div>
      </div>
    </section>
  );
}
