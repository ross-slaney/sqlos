import Link from "next/link";
import AuthStackViz from "@/components/AuthStackViz";
import SectionHeading from "@/components/marketing/SectionHeading";
import { authStackFeatures } from "@/components/marketing/constants";
import { ArrowIcon } from "@/components/icons";

export default function AuthStackSection() {
  return (
    <section className="px-6 py-16 sm:py-20">
      <div className="relative mx-auto max-w-6xl overflow-hidden rounded-[2rem] border border-zinc-800 bg-zinc-950 px-6 py-14 text-zinc-50 shadow-2xl sm:px-12 sm:py-16">
        <div
          className="pointer-events-none absolute inset-0 opacity-[0.05]"
          style={{
            backgroundImage:
              "radial-gradient(currentColor 1px, transparent 1px)",
            backgroundSize: "22px 22px",
          }}
          aria-hidden="true"
        />
        <div className="relative grid items-center gap-12 lg:grid-cols-[1.1fr_0.9fr] lg:gap-16">
          <div>
            <SectionHeading
              dark
              index="03"
              eyebrow="The auth stack"
              title="Enterprise SSO, social auth, and a whole lot more"
              description="One integration connects your app to every identity provider your customers use. Configure Google, Microsoft, Apple, SAML, or custom OIDC from the dashboard — or go headless and build your own login UI on the OAuth APIs."
            />
            <Link
              href="/docs/getting-started"
              className="mt-7 inline-flex items-center gap-2 text-sm font-semibold text-zinc-50 transition-colors hover:text-primary"
            >
              Add auth to your app
              <ArrowIcon />
            </Link>
          </div>

          <AuthStackViz />
        </div>

        <div className="relative mt-14 grid gap-x-10 gap-y-8 border-t border-white/10 pt-10 sm:grid-cols-2">
          {authStackFeatures.map((item, i) => (
            <div key={item.title} className="flex gap-4">
              <span className="mt-0.5 font-mono text-xs text-primary/80">
                0{i + 1}
              </span>
              <div>
                <h3 className="text-sm font-semibold text-zinc-50">{item.title}</h3>
                <p className="mt-1.5 text-sm leading-6 text-zinc-400">{item.description}</p>
              </div>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}
