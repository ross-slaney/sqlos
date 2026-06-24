"use client";

import { Button, Chip } from "@heroui/react";
import { Check, KeyRound, Mail, ShieldCheck } from "lucide-react";
import type { ReactNode } from "react";
import { useEffect, useState } from "react";

const stages = ["email", "provider", "token", "done"] as const;

export default function AuthPageViz() {
  const [stageIndex, setStageIndex] = useState(0);
  const [typing, setTyping] = useState("");
  const email = "sarah@acme.co";
  const stage = stages[stageIndex] ?? "email";

  useEffect(() => {
    const prefersReducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    if (prefersReducedMotion) {
      setStageIndex(3);
      setTyping(email);
      return;
    }

    const timers: ReturnType<typeof setTimeout>[] = [];

    const run = () => {
      setStageIndex(0);
      setTyping("");

      for (let i = 0; i <= email.length; i++) {
        timers.push(setTimeout(() => setTyping(email.slice(0, i)), 400 + i * 55));
      }

      timers.push(setTimeout(() => setStageIndex(1), 1500));
      timers.push(setTimeout(() => setStageIndex(2), 2500));
      timers.push(setTimeout(() => setStageIndex(3), 3500));
    };

    run();
    const interval = setInterval(run, 5600);

    return () => {
      timers.forEach(clearTimeout);
      clearInterval(interval);
    };
  }, []);

  return (
    <div className="neon-panel overflow-hidden rounded-lg">
      <div className="flex items-center gap-2 border-b border-border/70 bg-muted/45 px-4 py-3">
        <span className="h-2.5 w-2.5 rounded-full bg-neon-coral" />
        <span className="h-2.5 w-2.5 rounded-full bg-neon-yellow" />
        <span className="h-2.5 w-2.5 rounded-full bg-neon-green" />
        <span className="ml-2 truncate font-mono text-[11px] text-muted-foreground">
          app.yourproduct.com/sqlos/auth/login
        </span>
      </div>

      <div className="flex min-h-[350px] items-center justify-center p-6 sm:p-8">
        <div className="w-full max-w-[320px] rounded-lg border border-neon-cyan/20 bg-background/80 p-5 shadow-[inset_0_0_34px_oklch(0_0_0_/_0.22)]">
          <div className="mb-6 text-center">
            <div className="mx-auto mb-3 flex h-11 w-11 items-center justify-center rounded-md border border-neon-cyan/35 bg-neon-cyan/10 text-neon-cyan">
              <KeyRound className="h-5 w-5" />
            </div>
            <h3 className="text-base font-semibold text-foreground">Sign in to YourProduct</h3>
            <p className="mt-1 text-xs text-muted-foreground">Hosted by SqlOS AuthPage</p>
          </div>

          <div className="rounded-md border border-border bg-card/70 px-3 py-2.5 font-mono text-sm">
            <span className={typing ? "text-foreground" : "text-muted-foreground"}>
              {typing || "name@company.com"}
            </span>
            {stage === "email" ? <span className="ml-0.5 animate-pulse text-neon-green">|</span> : null}
          </div>

          <div className="mt-3 grid gap-2">
            <Button
              fullWidth
              variant={stage === "provider" || stage === "token" || stage === "done" ? "primary" : "secondary"}
              className={
                stage === "provider" || stage === "token" || stage === "done"
                  ? "bg-neon-green text-background"
                  : "bg-default text-foreground"
              }
            >
              {stage === "email" ? "Continue" : "Continue with SSO"}
            </Button>
            <div className="grid grid-cols-3 gap-2">
              {["Google", "Microsoft", "Apple"].map((provider) => (
                <Button
                  key={provider}
                  size="sm"
                  variant="outline"
                  className="border-neon-cyan/20 bg-transparent text-xs text-muted-foreground"
                >
                  {provider}
                </Button>
              ))}
            </div>
          </div>

          <div className="mt-5 space-y-2">
            <StatusRow active={stageIndex >= 1} icon={<Mail className="h-4 w-4" />} text="Discover acme.co policy" />
            <StatusRow active={stageIndex >= 2} icon={<ShieldCheck className="h-4 w-4" />} text="Issue OAuth token" />
            <StatusRow active={stageIndex >= 3} icon={<Check className="h-4 w-4" />} text="Redirect back to app" />
          </div>

          <div className="mt-5 flex justify-center">
            <Chip
              size="sm"
              variant="soft"
              color={stage === "done" ? "success" : "accent"}
              className="border border-neon-green/25 bg-neon-green/10 text-neon-green"
            >
              {stage === "done" ? "authenticated" : "auth flow running"}
            </Chip>
          </div>
        </div>
      </div>
    </div>
  );
}

function StatusRow({
  active,
  icon,
  text,
}: {
  active: boolean;
  icon: ReactNode;
  text: string;
}) {
  return (
    <div
      className={[
        "flex items-center gap-2 rounded-md border px-3 py-2 text-xs transition-colors",
        active
          ? "border-neon-green/25 bg-neon-green/10 text-neon-green"
          : "border-border bg-card/45 text-muted-foreground",
      ].join(" ")}
    >
      {icon}
      <span>{text}</span>
    </div>
  );
}
