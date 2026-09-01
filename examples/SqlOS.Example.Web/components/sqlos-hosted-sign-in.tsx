"use client";

import { signIn } from "next-auth/react";
import { useSearchParams } from "next/navigation";
import { useEffect, useState } from "react";
import { normalizeNextPath } from "@/lib/sqlos-config";

type SqlOSHostedSignInProps = {
  view: "login" | "signup";
};

export function startHostedSqlOSSignIn(view: "login" | "signup", nextPath?: string | null) {
  return signIn(
    "sqlos",
    { callbackUrl: normalizeNextPath(nextPath) },
    view === "signup" ? { view: "signup" } : { prompt: "login" },
  );
}

export function SqlOSHostedSignIn({ view }: SqlOSHostedSignInProps) {
  const searchParams = useSearchParams();
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    void startHostedSqlOSSignIn(view, searchParams.get("next")).catch((err: unknown) => {
      if (!cancelled) {
        setError(err instanceof Error ? err.message : "Failed to start the hosted SqlOS sign-in.");
      }
    });

    return () => {
      cancelled = true;
    };
  }, [searchParams, view]);

  return error ? <p className="error">{error}</p> : null;
}
