"use client";

import { useEffect, useState } from "react";
import { useSearchParams } from "next/navigation";
import {
  createCodeChallenge,
  createOpaqueToken,
  getExampleAuthServerUrl,
  getExampleClientId,
  getExampleRedirectUri,
  normalizeNextPath,
  persistSqlOSAuthFlow,
  type SqlOSAuthView,
} from "@/lib/sqlos-auth";

type SqlOSAuthRedirectProps = {
  view: SqlOSAuthView;
};

export function SqlOSAuthRedirect({ view }: SqlOSAuthRedirectProps) {
  const searchParams = useSearchParams();
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    const start = async () => {
      try {
        const verifier = createOpaqueToken(48);
        const state = createOpaqueToken(24);
        const challenge = await createCodeChallenge(verifier);
        const nextPath = normalizeNextPath(searchParams.get("next"));

        if (cancelled) {
          return;
        }

        persistSqlOSAuthFlow(view, state, verifier, nextPath);

        const url = new URL(`${getExampleAuthServerUrl()}/authorize`);
        url.searchParams.set("response_type", "code");
        url.searchParams.set("client_id", getExampleClientId());
        url.searchParams.set("redirect_uri", getExampleRedirectUri());
        url.searchParams.set("state", state);
        url.searchParams.set("code_challenge", challenge);
        url.searchParams.set("code_challenge_method", "S256");
        if (view === "signup") {
          url.searchParams.set("view", "signup");
        }

        window.location.replace(url.toString());
      } catch (err) {
        setError(err instanceof Error ? err.message : "Failed to start the hosted SqlOS auth flow.");
      }
    };

    void start();

    return () => {
      cancelled = true;
    };
  }, [searchParams, view]);

  return error ? <p className="error">{error}</p> : null;
}
