import Link from "next/link";
import AsciiShader from "@/components/marketing/AsciiShader";
import InstallCommand from "@/components/marketing/InstallCommand";

type ClosingSectionProps = {
  githubStars?: string | null;
};

export default function ClosingSection({ githubStars }: ClosingSectionProps) {
  return (
    <section className="px-6 py-20 sm:py-24">
      <div className="relative mx-auto max-w-6xl overflow-hidden rounded-[2.5rem] bg-gradient-to-br from-primary via-violet-600 to-indigo-600 px-6 py-20 text-center shadow-2xl shadow-primary/20 sm:py-24">
        {/* live ASCII/dither field (Codrops "Efecto" technique), white ink on violet */}
        <div className="pointer-events-none absolute inset-0" aria-hidden="true">
          <AsciiShader ink="white" cell={12} intensity={0.32} speed={0.7} />
          <div
            className="absolute inset-0"
            style={{
              background:
                "radial-gradient(42rem 22rem at 50% 38%, hsl(263 70% 45% / 0.65), transparent 72%)",
            }}
          />
        </div>
        <div className="relative">
          <h2 className="mx-auto max-w-2xl text-balance text-3xl font-semibold tracking-[-0.045em] text-white sm:text-5xl">
            Your auth server is one package away
          </h2>
          <p className="mx-auto mt-5 max-w-md text-base leading-7 text-white/80">
            Install it, map the routes, run the Todo sample. Everything on this page is
            in the box.
          </p>
          <div className="mt-8 flex justify-center">
            <InstallCommand className="border-white/20 bg-white/10 text-white shadow-none backdrop-blur hover:border-white/40 [&_span]:text-white" />
          </div>
          <div className="mt-7 flex flex-wrap items-center justify-center gap-3">
            <Link
              href="/docs/getting-started"
              className="inline-flex items-center justify-center rounded-full bg-white px-6 py-3 text-sm font-semibold text-primary shadow-lg transition-transform hover:scale-[1.02]"
            >
              Get started
            </Link>
            <Link
              href="/docs"
              className="inline-flex items-center justify-center rounded-full border border-white/30 px-6 py-3 text-sm font-medium text-white transition-colors hover:bg-white/10"
            >
              Documentation
            </Link>
          </div>
          <div className="mt-12 flex flex-wrap items-center justify-center gap-x-6 gap-y-2 font-mono text-[11px] text-white/70">
            <a
              href="https://github.com/ross-slaney/sqlos"
              target="_blank"
              rel="noopener noreferrer"
              className="transition-colors hover:text-white"
            >
              {githubStars ? `★ ${githubStars} on GitHub` : "open source on GitHub"}
            </a>
            <span className="text-white/30">·</span>
            <a
              href="https://github.com/ross-slaney/sqlos/blob/main/paper/shrbac-compsac-2026.pdf"
              target="_blank"
              rel="noopener noreferrer"
              className="transition-colors hover:text-white"
            >
              SHRBAC — COMPSAC 2026
            </a>
            <span className="text-white/30">·</span>
            <Link
              href="/docs/getting-started#run-the-right-sample"
              className="transition-colors hover:text-white"
            >
              runnable samples
            </Link>
          </div>
        </div>
      </div>
    </section>
  );
}
