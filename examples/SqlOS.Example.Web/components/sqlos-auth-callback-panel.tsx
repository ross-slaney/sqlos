"use client";

import { signIn } from "next-auth/react";
import { jwtDecode } from "jwt-decode";
import { useSearchParams } from "next/navigation";
import { useEffect, useState } from "react";
import {
  clearSqlOSAuthFlow,
  getExampleAuthServerUrl,
  getExampleClientId,
  getExampleRedirectUri,
  readSqlOSAuthFlow,
} from "@/lib/sqlos-auth";

type DecodedToken = {
  exp: number;
  sub?: string;
  email?: string;
  name?: string;
  org_id?: string;
  sid?: string;
};

type TokenResponse = {
  access_token?: string;
  refresh_token?: string;
  error?: string;
  error_description?: string;
};

export function SqlOSAuthCallbackPanel() {
  const searchParams = useSearchParams();
  const [message, setMessage] = useState("Completing the hosted SqlOS sign-in...");

  useEffect(() => {
    let cancelled = false;

    const complete = async () => {
      try {
        const code = searchParams.get("code");
        const state = searchParams.get("state");
        const error = searchParams.get("error");
        const errorDescription = searchParams.get("error_description");

        if (error) {
          throw new Error(errorDescription || error);
        }

        if (!code || !state) {
          throw new Error("The hosted auth callback is missing the required code or state.");
        }

        const flow = readSqlOSAuthFlow();
        if (!flow.state || !flow.verifier) {
          throw new Error("The local PKCE login state is missing or expired.");
        }

        if (flow.state !== state) {
          throw new Error("OAuth state validation failed.");
        }

        const tokenResponse = await fetch(`${getExampleAuthServerUrl()}/token`, {
          method: "POST",
          headers: {
            "Content-Type": "application/x-www-form-urlencoded",
          },
          body: new URLSearchParams({
            grant_type: "authorization_code",
            code,
            client_id: getExampleClientId(),
            redirect_uri: getExampleRedirectUri(),
            code_verifier: flow.verifier,
          }),
        });

        const tokenData = (await tokenResponse.json()) as TokenResponse;
        if (!tokenResponse.ok || !tokenData.access_token || !tokenData.refresh_token) {
          throw new Error(tokenData.error_description || tokenData.error || "Failed to exchange the authorization code.");
        }

        const decoded = jwtDecode<DecodedToken>(tokenData.access_token);
        const signInResult = await signIn("credentials", {
          redirect: false,
          accessToken: tokenData.access_token,
          refreshToken: tokenData.refresh_token,
          userId: decoded.sub ?? "",
          email: decoded.email ?? `${decoded.sub ?? "user"}@example.local`,
          displayName: decoded.name ?? decoded.email ?? decoded.sub ?? "SqlOS user",
          organizationId: decoded.org_id ?? null,
          sessionId: decoded.sid ?? "",
        });

        if (!signInResult || signInResult.error) {
          throw new Error(signInResult?.error || "The frontend session could not be created.");
        }

        clearSqlOSAuthFlow();

        if (!cancelled) {
          window.location.replace(flow.nextPath);
        }
      } catch (error) {
        clearSqlOSAuthFlow();
        if (!cancelled) {
          setMessage(error instanceof Error ? error.message : "Hosted SqlOS sign-in failed.");
        }
      }
    };

    void complete();

    return () => {
      cancelled = true;
    };
  }, [searchParams]);

  return <p>{message}</p>;
}
