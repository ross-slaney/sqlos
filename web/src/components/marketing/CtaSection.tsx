import Link from "next/link";
import AsciiShader from "@/components/marketing/AsciiShader";
import InstallCommand from "@/components/marketing/InstallCommand";

export default function CtaSection() {
  return (
    <section className="border-t px-6 py-16 sm:py-20">
      <div className="relative mx-auto max-w-6xl overflow-hidden rounded-[2rem] border">
        <div className="absolute inset-0 -z-10">
          <AsciiShader cell={10} intensity={0.55} speed={0.7} />
          <div className="absolute inset-0 bg-gradient-to-b from-background/85 via-background/60 to-background/85" />
        </div>

        <div className="mx-auto max-w-xl px-6 py-20 text-center sm:py-24">
          <p className="font-mono text-[11px] uppercase tracking-[0.22em] text-primary">
            [08] Ship it
          </p>
          <h2 className="mt-5 text-balance text-3xl font-semibold tracking-[-0.045em] text-foreground sm:text-[2.75rem] sm:leading-[1.05]">
            Get started in minutes
          </h2>
          <p className="mt-4 text-base leading-7 text-muted-foreground">
            Install the package. Run the Todo sample. Read the source.
          </p>
          <div className="mt-7 flex justify-center">
            <InstallCommand />
          </div>
          <div className="mt-7 flex flex-wrap items-center justify-center gap-3">
            <Link
              href="/docs/getting-started"
              className="inline-flex items-center justify-center rounded-lg bg-primary px-5 py-2.5 text-sm font-semibold text-primary-foreground shadow-lg shadow-primary/20 transition-all hover:bg-primary/90"
            >
              Getting started guide
            </Link>
            <Link
              href="/docs"
              className="inline-flex items-center justify-center rounded-lg border bg-background/70 px-5 py-2.5 text-sm font-medium text-foreground backdrop-blur transition-colors hover:bg-accent"
            >
              Documentation
            </Link>
          </div>
        </div>
      </div>
    </section>
  );
}
