import Link from "next/link";
import { LoginMini, ProviderRow, SsoPill, TreeMini } from "@/components/marketing/fragments";
import { ArrowIcon } from "@/components/icons";

export default function TrioSection() {
  return (
    <section className="px-6 py-24 sm:py-28">
      <div className="mx-auto max-w-6xl">
        <h2 className="mx-auto max-w-2xl text-balance text-center text-3xl font-semibold tracking-[-0.045em] text-foreground sm:text-[2.6rem] sm:leading-[1.1]">
          Everything between “Sign in” and a row of data
        </h2>
        <p className="mx-auto mt-4 max-w-xl text-center text-base leading-7 text-muted-foreground">
          Three products&apos; worth of surface area, one package. Each piece is live the
          moment you map the routes.
        </p>

        <div className="mt-14 grid gap-5 lg:grid-cols-3">
          {/* Hosted auth */}
          <Canvas tint="bg-violet-50/80 border-violet-100">
            <div className="flex justify-center py-6">
              <LoginMini className="rotate-[-2deg]" />
            </div>
            <CanvasCopy
              title="Hosted login, your brand"
              body="Server-rendered signup, login, and logout on your domain — logo, colors, and providers configured from the dashboard."
              href="/docs/getting-started"
              cta="Ship the login page"
            />
          </Canvas>

          {/* SSO */}
          <Canvas tint="bg-sky-50/80 border-sky-100">
            <div className="flex flex-col items-center gap-5 py-6">
              <ProviderRow />
              <SsoPill className="rotate-[1.5deg]" />
            </div>
            <CanvasCopy
              title="Enterprise SSO in an afternoon"
              body="SAML and OIDC per organization. Hand your customer the ACS URL, import their metadata, and home realm discovery routes by email domain."
              href="/docs"
              cta="See the SSO flow"
            />
          </Canvas>

          {/* FGA */}
          <Canvas tint="bg-rose-50/80 border-rose-100">
            <div className="px-8 py-6">
              <TreeMini />
            </div>
            <CanvasCopy
              title="Permissions that cascade"
              body="Model your real hierarchy, grant a role at any node, and access inherits downward. Checks fold into EF Core queries as a WHERE clause."
              href="/docs/fga/overview"
              cta="Explore FGA"
            />
          </Canvas>
        </div>
      </div>
    </section>
  );
}

function Canvas({ tint, children }: { tint: string; children: React.ReactNode }) {
  return (
    <div
      className={`flex flex-col justify-between overflow-hidden rounded-[1.75rem] border p-6 transition-transform duration-300 hover:-translate-y-1 ${tint}`}
    >
      {children}
    </div>
  );
}

function CanvasCopy({
  title,
  body,
  href,
  cta,
}: {
  title: string;
  body: string;
  href: string;
  cta: string;
}) {
  return (
    <div className="mt-4">
      <h3 className="text-lg font-semibold tracking-tight text-foreground">{title}</h3>
      <p className="mt-2 text-sm leading-6 text-muted-foreground">{body}</p>
      <Link
        href={href}
        className="mt-4 inline-flex items-center gap-1.5 text-sm font-semibold text-primary transition-colors hover:text-primary/80"
      >
        {cta}
        <ArrowIcon className="h-3.5 w-3.5" />
      </Link>
    </div>
  );
}
