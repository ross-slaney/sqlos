import Link from "next/link";
import SectionHeading from "@/components/marketing/SectionHeading";
import { productFeatures } from "@/components/marketing/constants";
import { ArrowIcon } from "@/components/icons";

// bento spans on lg: rows of [2,1] / [1,1,1] / [2,1] / [1,1,1]
const spans = [2, 1, 1, 1, 1, 2, 1, 1, 1, 1];

export default function FeaturesSection() {
  return (
    <section className="border-t px-6 py-24 sm:py-28">
      <div className="mx-auto max-w-6xl">
        <SectionHeading
          index="07"
          eyebrow="What ships"
          title="Everything you need for OAuth, AuthN, and AuthZ in .NET"
          description="SqlOS combines authentication and authorization in one library with OAuth 2.0, SAML SSO, OIDC, a branded login page, and FGA-based access control — built for large datasets with strong consistency."
        />

        <div className="mt-12 grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {productFeatures.map((feature, i) => (
            <div
              key={feature.title}
              className={[
                "group relative overflow-hidden rounded-xl border bg-card/70 p-6 shadow-sm transition-colors hover:border-primary/40 hover:bg-accent/30",
                spans[i] === 2 ? "lg:col-span-2" : "",
              ].join(" ")}
            >
              <span className="pointer-events-none absolute right-4 top-4 font-mono text-[10px] text-muted-foreground/40 transition-colors group-hover:text-primary/60">
                {String(i + 1).padStart(2, "0")}
              </span>
              <h3 className="text-sm font-semibold tracking-tight text-foreground">
                {feature.title}
              </h3>
              <p className="mt-2 max-w-md text-sm leading-6 text-muted-foreground">
                {feature.description}
              </p>
            </div>
          ))}
        </div>

        <div className="mt-10">
          <Link
            href="/docs/getting-started"
            className="inline-flex items-center gap-2 text-sm font-semibold text-primary transition-colors hover:text-primary/80"
          >
            Follow the getting started guide
            <ArrowIcon />
          </Link>
        </div>
      </div>
    </section>
  );
}
