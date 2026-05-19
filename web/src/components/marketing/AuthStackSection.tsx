import Link from "next/link";
import AuthStackViz from "@/components/AuthStackViz";
import { authStackFeatures } from "@/components/marketing/constants";
import { ArrowIcon } from "@/components/icons";

export default function AuthStackSection() {
  return (
    <section className="px-6 py-16 sm:py-20">
      <div className="mx-auto max-w-6xl overflow-hidden rounded-[2rem] border border-zinc-800 bg-zinc-950 px-6 py-12 text-zinc-50 shadow-2xl sm:px-12 sm:py-16">
        <div className="grid items-center gap-12 lg:grid-cols-[1.1fr_0.9fr] lg:gap-16">
          <div>
            <p className="text-[11px] font-semibold uppercase tracking-[0.16em] text-zinc-400">
              The auth stack
            </p>
            <h2 className="mt-3 text-3xl font-semibold tracking-[-0.04em] sm:text-4xl">
              Enterprise SSO, social auth, and a whole lot more
            </h2>
            <p className="mt-5 text-base leading-7 text-zinc-400">
              One integration connects your app to every identity provider your customers use. Configure
              Google, Microsoft, Apple, SAML, or custom OIDC from the dashboard — or go headless and build
              your own login UI on the OAuth APIs.
            </p>
            <Link
              href="/docs/getting-started"
              className="mt-6 inline-flex items-center gap-2 text-sm font-semibold text-zinc-50 transition-colors hover:text-zinc-300"
            >
              Add auth to your app
              <ArrowIcon />
            </Link>
          </div>

          <AuthStackViz />
        </div>

        <div className="mt-14 grid gap-x-10 gap-y-8 border-t border-white/10 pt-8 sm:grid-cols-2">
          {authStackFeatures.map((item) => (
            <div key={item.title}>
              <h3 className="text-sm font-semibold text-zinc-50">{item.title}</h3>
              <p className="mt-1.5 text-sm leading-6 text-zinc-400">{item.description}</p>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}
