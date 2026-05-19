import Link from "next/link";
import { productFeatures } from "@/components/marketing/constants";
import { ArrowIcon } from "@/components/icons";

export default function FeaturesSection() {
  return (
    <section className="border-t px-6 py-20 sm:py-24">
      <div className="mx-auto max-w-6xl">
        <p className="text-[11px] font-semibold uppercase tracking-[0.16em] text-muted-foreground">
          What ships
        </p>
        <h2 className="mt-3 text-3xl font-semibold tracking-[-0.04em] text-foreground sm:text-4xl">
          Everything you need for OAuth, AuthN, and AuthZ in .NET
        </h2>
        <p className="mt-5 max-w-2xl text-base leading-7 text-muted-foreground">
          SqlOS combines authentication and authorization in one library with OAuth 2.0, SAML SSO, OIDC, a
          branded login page, and FGA-based access control — built for large datasets with strong
          consistency.
        </p>

        <div className="mt-10 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {productFeatures.map((feature) => (
            <div
              key={feature.title}
              className="rounded-xl border bg-card/70 p-5 shadow-sm transition-colors hover:bg-accent/40"
            >
              <h3 className="text-sm font-semibold text-foreground">{feature.title}</h3>
              <p className="mt-2 text-sm leading-6 text-muted-foreground">{feature.description}</p>
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
