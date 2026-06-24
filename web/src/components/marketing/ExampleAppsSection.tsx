import Link from "next/link";
import { exampleApps } from "@/components/marketing/constants";
import { ArrowIcon } from "@/components/icons";

export default function ExampleAppsSection() {
  return (
    <section className="border-t px-6 py-20 sm:py-24">
      <div className="mx-auto max-w-6xl">
        <p className="text-[11px] font-semibold uppercase tracking-[0.16em] text-muted-foreground">
          Example apps
        </p>
        <h2 className="mt-3 text-3xl font-semibold tracking-[-0.04em] text-foreground sm:text-4xl">
          Run it, then fork it
        </h2>
        <p className="mt-5 max-w-2xl text-base leading-7 text-muted-foreground">
          Every sample ships in the repo with Aspire hosts, seeded clients, and docs that walk through the
          same flows you will use in production.
        </p>
        <div className="mt-10 grid gap-4 sm:grid-cols-3">
          {exampleApps.map((app) => (
            <Link
              key={app.title}
              href={app.href}
              className="group flex flex-col rounded-xl border bg-card/70 p-5 shadow-sm transition-colors hover:border-primary/30 hover:bg-accent/30"
            >
              <h3 className="text-sm font-semibold text-foreground">{app.title}</h3>
              <p className="mt-2 flex-1 text-sm leading-6 text-muted-foreground">{app.description}</p>
              <span className="mt-4 inline-flex items-center gap-1 text-sm font-semibold text-primary">
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
