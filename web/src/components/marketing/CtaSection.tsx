import Link from "next/link";

export default function CtaSection() {
  return (
    <section className="border-t border-border/70 px-6 py-20 sm:py-24">
      <div className="mx-auto max-w-xl text-center">
        <h2 className="text-3xl font-semibold text-foreground sm:text-4xl">
          Open the stack from your terminal
        </h2>
        <p className="mt-4 text-base leading-7 text-muted-foreground">
          Install the package. Run the Todo sample. Read the source.
        </p>
        <div className="mt-6 overflow-hidden rounded-lg border border-neon-cyan/25 bg-card shadow-[0_18px_70px_oklch(0_0_0_/_0.24)]">
          <pre className="px-4 py-3 font-mono text-[13px] text-neon-green">
            <code>dotnet add package SqlOS</code>
          </pre>
        </div>
        <div className="mt-6 flex flex-wrap items-center justify-center gap-3">
          <Link
            href="/docs/getting-started"
            className="inline-flex items-center justify-center rounded-md bg-neon-green px-5 py-2.5 text-sm font-semibold text-background transition-colors hover:bg-neon-cyan"
          >
            Getting started guide
          </Link>
          <Link
            href="/docs"
            className="inline-flex items-center justify-center rounded-md border border-neon-cyan/35 bg-background/70 px-5 py-2.5 text-sm font-semibold text-neon-cyan transition-colors hover:bg-neon-cyan/10"
          >
            Documentation
          </Link>
        </div>
      </div>
    </section>
  );
}
