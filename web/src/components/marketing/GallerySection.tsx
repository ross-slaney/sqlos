import Image from "next/image";

export default function GallerySection() {
  return (
    <section className="overflow-hidden bg-zinc-50 px-6 py-24 sm:py-28 dark:bg-zinc-900/40">
      <div className="mx-auto max-w-6xl">
        <div className="flex flex-col gap-4 sm:flex-row sm:items-end sm:justify-between">
          <h2 className="max-w-md text-balance text-3xl font-semibold tracking-[-0.045em] text-foreground sm:text-[2.6rem] sm:leading-[1.1]">
            And a control plane you don’t have to build
          </h2>
          <p className="max-w-sm text-sm leading-6 text-muted-foreground">
            The dashboard ships in the package — orgs, users, sessions, providers,
            grants, audit, and a live access tester at{" "}
            <code className="rounded bg-muted px-1.5 py-0.5 font-mono text-[12px]">/sqlos</code>.
          </p>
        </div>

        {/* layered cluster */}
        <div className="relative mt-14 hidden lg:block" style={{ height: "640px" }}>
          <div className="absolute left-0 top-0 w-[60%] overflow-hidden rounded-2xl border bg-card shadow-2xl">
            <Image
              src="/docs/dashboard-home.png"
              alt="SqlOS dashboard home"
              width={1280}
              height={840}
              className="h-auto w-full"
            />
          </div>
          <div className="absolute right-0 top-[6%] w-[42%] rotate-[1.5deg] overflow-hidden rounded-2xl border bg-card shadow-2xl">
            <Image
              src="/docs/dashboard-grants.png"
              alt="Grants across the resource tree"
              width={1280}
              height={840}
              className="h-auto w-full"
            />
          </div>
          <div className="absolute bottom-0 left-[34%] w-[42%] rotate-[-1.5deg] overflow-hidden rounded-2xl border bg-card shadow-2xl">
            <Image
              src="/docs/dashboard-access-tester.png"
              alt="Access tester tracing a decision"
              width={1280}
              height={840}
              className="h-auto w-full"
            />
          </div>
        </div>

        {/* stacked on smaller screens */}
        <div className="mt-10 space-y-6 lg:hidden">
          {[
            ["/docs/dashboard-home.png", "SqlOS dashboard home"],
            ["/docs/dashboard-grants.png", "Grants across the resource tree"],
            ["/docs/dashboard-access-tester.png", "Access tester tracing a decision"],
          ].map(([src, alt]) => (
            <div key={src} className="overflow-hidden rounded-2xl border bg-card shadow-lg">
              <Image src={src} alt={alt} width={1280} height={840} className="h-auto w-full" />
            </div>
          ))}
        </div>

        <div className="mt-10 flex flex-wrap gap-x-8 gap-y-2 font-mono text-[11px] uppercase tracking-[0.16em] text-muted-foreground">
          <span>● live counts from your database</span>
          <span>● time-windowed grants</span>
          <span>● trace any access decision</span>
        </div>
      </div>
    </section>
  );
}
