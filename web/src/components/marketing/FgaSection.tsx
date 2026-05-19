import Link from "next/link";
import FgaViz from "@/components/FgaViz";
import ProductScreenshot from "@/components/ProductScreenshot";
import { fgaConcepts } from "@/components/marketing/constants";

export default function FgaSection() {
  return (
    <section className="border-t px-6 py-20 sm:py-24">
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
            <p className="text-[11px] font-semibold uppercase tracking-[0.16em] text-muted-foreground">
              Authorization
            </p>
            <h2 className="mt-3 text-3xl font-semibold tracking-[-0.04em] text-foreground sm:text-4xl">
              Authorization in SQL, not middleware
            </h2>
            <p className="mt-5 text-base leading-7 text-muted-foreground">
              Every multi-tenant app eventually outgrows{" "}
              <code className="rounded bg-muted px-1.5 py-0.5 font-mono text-[13px] text-foreground">
                if (user.Role == &quot;Admin&quot;)
              </code>
              . SqlOS FGA mirrors your product hierarchy. Assign a role at any node and permissions cascade
              down — no role explosion, no per-request policy service.
            </p>

            <div className="mt-6 space-y-1">
              {fgaConcepts.map((item) => (
                <div key={item.label} className="flex items-start gap-3 py-2">
                  <span className="mt-0.5 w-20 shrink-0 text-xs font-semibold text-foreground">
                    {item.label}
                  </span>
                  <span className="text-sm leading-6 text-muted-foreground">{item.description}</span>
                </div>
              ))}
            </div>

            <p className="mt-5 text-sm leading-6 text-muted-foreground">
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
