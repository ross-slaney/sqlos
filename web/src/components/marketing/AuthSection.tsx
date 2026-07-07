import AuthPageViz from "@/components/AuthPageViz";
import SectionHeading from "@/components/marketing/SectionHeading";
import { authHighlights } from "@/components/marketing/constants";

export default function AuthSection() {
  return (
    <section className="border-t px-6 py-24 sm:py-28">
      <div className="mx-auto max-w-6xl">
        <div className="grid items-start gap-12 lg:grid-cols-[1fr_1.15fr] lg:gap-16">
          <div>
            <SectionHeading
              index="02"
              eyebrow="Authentication"
              title="From first user to enterprise SSO"
              description="SqlOS ships a brandable login page backed by a real OAuth 2.0 server in your ASP.NET pipeline. Start with password auth, add social login from the dashboard, and enable SAML SSO when your customers need it — no rewrites between stages."
            />
            <div className="mt-8 divide-y rounded-xl border bg-card/60">
              {authHighlights.map((item, i) => (
                <div key={item.title} className="flex gap-4 p-5">
                  <span className="mt-0.5 font-mono text-xs text-primary/70">
                    0{i + 1}
                  </span>
                  <div>
                    <h3 className="text-sm font-semibold text-foreground">{item.title}</h3>
                    <p className="mt-1.5 text-sm leading-6 text-muted-foreground">{item.body}</p>
                  </div>
                </div>
              ))}
            </div>
          </div>

          <div className="space-y-6 lg:mt-10">
            <AuthPageViz />
          </div>
        </div>
      </div>
    </section>
  );
}
