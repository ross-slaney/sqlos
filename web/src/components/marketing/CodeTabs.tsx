"use client";

import { Button } from "@heroui/react";
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
    <div className="overflow-hidden rounded-lg border border-neon-cyan/25 bg-card/80 shadow-[0_18px_70px_oklch(0_0_0_/_0.32)]">
      <div className="flex flex-wrap gap-2 border-b border-border/70 bg-muted/40 p-3">
        {tabs.map((tab) => (
          <Button
            key={tab.id}
            size="sm"
            variant={active === tab.id ? "primary" : "outline"}
            onPress={() => setActive(tab.id)}
            className={[
              "text-xs font-semibold sm:text-sm",
              active === tab.id
                ? "bg-neon-green text-background"
                : "border-neon-cyan/25 bg-transparent text-neon-cyan",
            ].join(" ")}
          >
            {tab.label}
          </Button>
        ))}
      </div>
      <pre className="overflow-x-auto bg-[oklch(0.055_0.022_248)] px-4 py-5 font-mono text-[12px] leading-7 text-foreground sm:px-5 sm:text-[13px]">
        <code>{current.code}</code>
      </pre>
    </div>
  );
}
