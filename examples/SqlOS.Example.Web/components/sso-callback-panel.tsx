"use client";

import { signIn } from "next-auth/react";
import { useRouter, useSearchParams } from "next/navigation";
import { useEffect, useState } from "react";

const apiUrl = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5062";

export function SsoCallbackPanel() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const [message, setMessage] = useState("Completing sign-in...");

  useEffect(() => {
    const handoff = searchParams.get("handoff");
    const code = searchParams.get("code");
    const state = searchParams.get("state");
    const error = searchParams.get("error");

    if (error) {
      setMessage(error);
      return;
    }

    if (!handoff && (!code || !state)) {
      setMessage("The callback is missing the expected handoff or code/state values.");
      return;
    }

    let cancelled = false;

    async function completeSignIn() {
      try {
        const response = handoff
          ? await fetch(`${apiUrl}/api/v1/auth/oidc/complete`, {
              method: "POST",
              credentials: "include",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({ handoff })
            })
          : await fetch(`${apiUrl}/api/v1/auth/sso/exchange`, {
              method: "POST",
              credentials: "include",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({ code, state })
            });

        const data = await response.json();
        if (!response.ok) {
          throw new Error(data.message || (handoff ? "OIDC sign-in failed." : "SSO exchange failed."));
        }

        const result = await signIn("credentials", {
          redirect: false,
          accessToken: data.accessToken,
          refreshToken: data.refreshToken,
          userId: data.user.id,
          email: data.user.email,
          displayName: data.user.displayName,
          organizationId: data.organizationId,
          sessionId: data.sessionId
        });

        if (!result || result.error) {
          throw new Error(result?.error || "The frontend session could not be created.");
        }

        if (!cancelled) {
          router.replace("/app");
          router.refresh();
        }
      } catch (error) {
        if (!cancelled) {
          setMessage(error instanceof Error ? error.message : "SSO sign-in failed.");
        }
      }
    }

    void completeSignIn();

    return () => {
      cancelled = true;
    };
  }, [router, searchParams]);

  return <p>{message}</p>;
}
