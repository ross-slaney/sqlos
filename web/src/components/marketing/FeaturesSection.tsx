import Link from "next/link";
import { productFeatures } from "@/components/marketing/constants";
import { ArrowIcon } from "@/components/icons";

export default function FeaturesSection() {
  return (
    <section className="border-t border-border/70 px-6 py-20 sm:py-24">
      <div className="mx-auto max-w-6xl">
        <p className="font-mono text-[11px] font-semibold uppercase text-neon-green">
          What ships
        </p>
        <h2 className="mt-3 text-3xl font-semibold text-foreground sm:text-4xl">
          Everything you need for OAuth, AuthN, and AuthZ in .NET
        </h2>
        <p className="mt-5 max-w-2xl text-base leading-7 text-muted-foreground">
          SqlOS combines authentication and authorization in one library with OAuth 2.0, SAML SSO, OIDC, a
          branded login page, and FGA-based access control, built for large datasets with strong
          consistency.
        </p>

        <div className="mt-10 grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {productFeatures.map((feature) => (
            <div
              key={feature.title}
              className="rounded-lg border border-border/70 bg-card/70 p-5 shadow-[0_14px_50px_oklch(0_0_0_/_0.18)] transition-colors hover:border-neon-green/40 hover:bg-card"
            >
              <h3 className="text-sm font-semibold text-foreground">{feature.title}</h3>
              <p className="mt-2 text-sm leading-6 text-muted-foreground">{feature.description}</p>
            </div>
          ))}
        </div>

        <div className="mt-10">
          <Link
            href="/docs/getting-started"
            className="inline-flex items-center gap-2 text-sm font-semibold text-neon-cyan transition-colors hover:text-neon-green"
          >
            Follow the getting started guide
            <ArrowIcon />
          </Link>
        </div>
      </div>
    </section>
  );
}
