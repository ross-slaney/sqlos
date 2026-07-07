import Link from "next/link";
import FgaViz from "@/components/FgaViz";
import ProductScreenshot from "@/components/ProductScreenshot";
import SectionHeading from "@/components/marketing/SectionHeading";
import { fgaConcepts } from "@/components/marketing/constants";

export default function FgaSection() {
  return (
    <section className="border-t px-6 py-24 sm:py-28">
      <div className="mx-auto max-w-6xl">
        <div className="grid items-start gap-12 lg:grid-cols-[1.15fr_1fr] lg:gap-16">
          <div className="order-2 space-y-6 lg:order-1">
            <FgaViz />
            <ProductScreenshot
              src="/docs/dashboard-fga-resources.png"
              alt="SqlOS FGA resources dashboard"
            />
          </div>

          <div className="order-1 lg:order-2">
            <SectionHeading
              index="04"
              eyebrow="Authorization"
              title="Authorization in SQL, not middleware"
            />
            <p className="mt-5 text-base leading-7 text-muted-foreground">
              Every multi-tenant app eventually outgrows{" "}
              <code className="rounded bg-muted px-1.5 py-0.5 font-mono text-[13px] text-foreground">
                if (user.Role == &quot;Admin&quot;)
              </code>
              . SqlOS FGA mirrors your product hierarchy. Assign a role at any node and permissions cascade
              down — no role explosion, no per-request policy service.
            </p>

            <div className="mt-7 divide-y rounded-xl border bg-card/60">
              {fgaConcepts.map((item) => (
                <div key={item.label} className="flex items-start gap-4 p-4">
                  <span className="mt-0.5 w-24 shrink-0 font-mono text-xs font-semibold uppercase tracking-[0.08em] text-primary">
                    {item.label}
                  </span>
                  <span className="text-sm leading-6 text-muted-foreground">{item.description}</span>
                </div>
              ))}
            </div>

            <p className="mt-6 text-sm leading-6 text-muted-foreground">
              Built on{" "}
              <a
                href="https://github.com/ross-slaney/sqlos/blob/main/paper/shrbac-compsac-2026.pdf"
                target="_blank"
                rel="noopener noreferrer"
                className="font-medium text-primary underline underline-offset-4 hover:text-primary/80"
              >
                SHRBAC
              </a>{" "}
              and explained in{" "}
              <Link
                href="/blog/developers-guide-to-hierarchical-rbac"
                className="font-medium text-primary underline underline-offset-4 hover:text-primary/80"
              >
                The Developer&apos;s Guide to Hierarchical RBAC
              </Link>
              .
            </p>
          </div>
        </div>
      </div>
    </section>
  );
}
