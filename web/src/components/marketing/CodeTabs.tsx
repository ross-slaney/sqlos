"use client";

import { useState } from "react";

const tabs = [
  {
    id: "install",
    label: "Install",
    code: "dotnet add package SqlOS",
    language: "bash",
  },
  {
    id: "register",
    label: "Register",
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
    label: "Map routes",
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
      <div className="flex border-b bg-muted/40">
        {tabs.map((tab) => (
          <button
            key={tab.id}
            type="button"
            onClick={() => setActive(tab.id)}
            className={[
              "px-4 py-2.5 text-xs font-semibold transition-colors sm:text-sm",
              active === tab.id
                ? "border-b-2 border-primary bg-background text-foreground"
                : "text-muted-foreground hover:text-foreground",
            ].join(" ")}
          >
            {tab.label}
          </button>
        ))}
      </div>
      <pre className="overflow-x-auto px-4 py-5 font-mono text-[12px] leading-7 text-foreground sm:px-5 sm:text-[13px]">
        <code>{current.code}</code>
      </pre>
    </div>
  );
}
