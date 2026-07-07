"use client";

import { useState } from "react";

const tabs = [
  {
    id: "install",
    label: "01 Install",
    file: "terminal",
    code: "dotnet add package SqlOS",
    language: "bash",
  },
  {
    id: "register",
    label: "02 Register",
    file: "Program.cs",
    code: `builder.AddSqlOS<AppDbContext>(options =>
{
    options.AuthServer.SeedOwnedWebApp(
        "web",
        "My App",
        "https://localhost:5001/auth/callback");
});`,
    language: "csharp",
  },
  {
    id: "map",
    label: "03 Map routes",
    file: "Program.cs",
    code: `var app = builder.Build();
app.MapSqlOS();
app.Run();`,
    language: "csharp",
  },
] as const;

export default function CodeTabs() {
  const [active, setActive] = useState<(typeof tabs)[number]["id"]>("install");
  const current = tabs.find((tab) => tab.id === active) ?? tabs[0];

  return (
    <div className="overflow-hidden rounded-2xl border bg-card shadow-lg">
      <div className="flex items-center justify-between border-b bg-muted/40 pr-4">
        <div className="flex">
          {tabs.map((tab) => (
            <button
              key={tab.id}
              type="button"
              onClick={() => setActive(tab.id)}
              className={[
                "px-4 py-2.5 font-mono text-[11px] font-semibold uppercase tracking-[0.1em] transition-colors sm:text-xs",
                active === tab.id
                  ? "border-b-2 border-primary bg-background text-foreground"
                  : "text-muted-foreground hover:text-foreground",
              ].join(" ")}
            >
              {tab.label}
            </button>
          ))}
        </div>
        <span className="hidden font-mono text-[11px] text-muted-foreground sm:block">
          {current.file}
        </span>
      </div>
      <pre className="min-h-[220px] overflow-x-auto px-4 py-5 font-mono text-[12px] leading-7 text-foreground sm:px-5 sm:text-[13px]">
        <code>{current.code}</code>
      </pre>
    </div>
  );
}
