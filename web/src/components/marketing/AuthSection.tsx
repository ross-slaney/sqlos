import AuthPageViz from "@/components/AuthPageViz";
import { authHighlights } from "@/components/marketing/constants";

export default function AuthSection() {
  return (
    <section className="border-t border-border/70 px-6 py-20 sm:py-24">
      <div className="mx-auto max-w-6xl">
        <div className="grid items-start gap-12 lg:grid-cols-[1fr_1.15fr] lg:gap-16">
          <div>
            <SectionEyebrow>Authentication</SectionEyebrow>
            <h2 className="mt-3 text-3xl font-semibold text-foreground sm:text-4xl">
              Start with login. Keep the whole auth stack.
            </h2>
            <p className="mt-5 text-base leading-7 text-muted-foreground">
              SqlOS ships a brandable login page backed by a real OAuth 2.0 server in your ASP.NET pipeline.
              Start with password auth, add social login from the dashboard, and enable SAML SSO when your
              customers need it without rewriting the integration.
            </p>
            <div className="mt-7 space-y-5">
              {authHighlights.map((item) => (
                <Detail key={item.title} title={item.title} body={item.body} />
              ))}
            </div>
          </div>

          <div className="space-y-6 lg:mt-8">
            <AuthPageViz />
          </div>
        </div>
      </div>
    </section>
  );
}

function SectionEyebrow({ children }: { children: string }) {
  return (
    <p className="font-mono text-[11px] font-semibold uppercase text-neon-green">
      {children}
    </p>
  );
}

function Detail({ title, body }: { title: string; body: string }) {
  return (
    <div className="rounded-lg border border-border/70 bg-card/60 p-4 shadow-[0_14px_50px_oklch(0_0_0_/_0.18)]">
      <h3 className="text-sm font-semibold text-foreground">{title}</h3>
      <p className="mt-1.5 text-sm leading-6 text-muted-foreground">{body}</p>
    </div>
  );
}
