import Link from "next/link";

export default function CtaSection() {
  return (
    <section className="border-t px-6 py-20 sm:py-24">
      <div className="mx-auto max-w-xl text-center">
        <h2 className="text-3xl font-semibold tracking-[-0.04em] text-foreground sm:text-4xl">
          Get started in minutes
        </h2>
        <p className="mt-4 text-base leading-7 text-muted-foreground">
          Install the package. Run the Todo sample. Read the source.
        </p>
        <div className="mt-6 overflow-hidden rounded-xl border bg-card">
          <pre className="px-4 py-3 font-mono text-[13px] text-foreground">
            <code>dotnet add package SqlOS</code>
          </pre>
        </div>
        <div className="mt-6 flex flex-wrap items-center justify-center gap-3">
          <Link
            href="/docs/getting-started"
            className="inline-flex items-center justify-center rounded-md bg-primary px-5 py-2.5 text-sm font-medium text-primary-foreground transition-colors hover:bg-primary/90"
          >
            Getting started guide
          </Link>
          <Link
            href="/docs"
            className="inline-flex items-center justify-center rounded-md border bg-background px-5 py-2.5 text-sm font-medium text-foreground transition-colors hover:bg-accent"
          >
            Documentation
          </Link>
        </div>
      </div>
    </section>
  );
}
