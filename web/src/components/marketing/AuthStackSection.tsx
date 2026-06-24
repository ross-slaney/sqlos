import Link from "next/link";
import AuthStackViz from "@/components/AuthStackViz";
import { authStackFeatures } from "@/components/marketing/constants";
import { ArrowIcon } from "@/components/icons";

export default function AuthStackSection() {
  return (
    <section className="px-6 py-16 sm:py-20">
      <div className="neon-panel mx-auto max-w-6xl overflow-hidden rounded-lg px-6 py-12 text-foreground sm:px-12 sm:py-16">
        <div className="grid items-center gap-12 lg:grid-cols-[1.1fr_0.9fr] lg:gap-16">
          <div>
            <p className="font-mono text-[11px] font-semibold uppercase text-neon-green">
              The auth stack
            </p>
            <h2 className="mt-3 text-3xl font-semibold sm:text-4xl">
              Hosted login, social auth, SSO, and admin UI in one loop
            </h2>
            <p className="mt-5 text-base leading-7 text-muted-foreground">
              One integration connects your app to every identity provider your customers use. Configure
              Google, Microsoft, Apple, SAML, or custom OIDC from the dashboard, or go headless and build
              your own login UI on the OAuth APIs.
            </p>
            <Link
              href="/docs/getting-started"
              className="mt-6 inline-flex items-center gap-2 text-sm font-semibold text-neon-cyan transition-colors hover:text-neon-green"
            >
              Add auth to your app
              <ArrowIcon />
            </Link>
          </div>

          <AuthStackViz />
        </div>

        <div className="mt-14 grid gap-x-10 gap-y-8 border-t border-border/70 pt-8 sm:grid-cols-2">
          {authStackFeatures.map((item) => (
            <div key={item.title}>
              <h3 className="text-sm font-semibold text-foreground">{item.title}</h3>
              <p className="mt-1.5 text-sm leading-6 text-muted-foreground">{item.description}</p>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}
