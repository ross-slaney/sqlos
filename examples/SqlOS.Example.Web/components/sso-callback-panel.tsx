"use client";

import { signIn } from "next-auth/react";
import { jwtDecode } from "jwt-decode";
import { useRouter, useSearchParams } from "next/navigation";
import { useEffect, useState } from "react";

const apiUrl = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5062";
const authServerUrl = `${apiUrl}/sqlos/auth`;
const authFlowStorageKey = "sqlos_example_auth_flow";
const oidcStateStorageKey = "sqlos_example_oidc_state";
const oidcVerifierStorageKey = "sqlos_example_oidc_verifier";
const oidcRedirectUriStorageKey = "sqlos_example_oidc_redirect_uri";
const oidcClientIdStorageKey = "sqlos_example_oidc_client_id";

type DecodedToken = {
  exp: number;
  sub?: string;
  email?: string;
  name?: string;
  org_id?: string;
};

type TokenResponse = {
  accessToken: string;
  refreshToken: string;
  sessionId: string;
  organizationId?: string | null;
};

type OrganizationOption = {
  id: string;
  slug: string;
  name: string;
  role: string;
};

type LoginResult = {
  requiresOrganizationSelection: boolean;
  pendingAuthToken?: string | null;
  organizations: OrganizationOption[];
  tokens?: TokenResponse | null;
};

function clearOidcFlowStorage() {
  sessionStorage.removeItem(authFlowStorageKey);
  sessionStorage.removeItem(oidcStateStorageKey);
  sessionStorage.removeItem(oidcVerifierStorageKey);
  sessionStorage.removeItem(oidcRedirectUriStorageKey);
  sessionStorage.removeItem(oidcClientIdStorageKey);
}

export function SsoCallbackPanel() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const [message, setMessage] = useState("Completing sign-in...");
  const [pendingSelection, setPendingSelection] = useState<{
    pendingAuthToken: string;
    organizations: OrganizationOption[];
  } | null>(null);

  useEffect(() => {
    const code = searchParams.get("code");
    const state = searchParams.get("state");
    const error = searchParams.get("error");
    const flow = sessionStorage.getItem(authFlowStorageKey);

    if (error) {
      clearOidcFlowStorage();
      setMessage(error);
      return;
    }

    if (!code || !state) {
      clearOidcFlowStorage();
      setMessage("The callback is missing the expected code/state values.");
      return;
    }

    let cancelled = false;

    async function createFrontendSession(tokens: TokenResponse) {
      const decoded = jwtDecode<DecodedToken>(tokens.accessToken);
      const result = await signIn("credentials", {
        redirect: false,
        accessToken: tokens.accessToken,
        refreshToken: tokens.refreshToken,
        userId: decoded.sub ?? "",
        email: decoded.email ?? "",
        displayName: decoded.name ?? decoded.email ?? decoded.sub ?? "SqlOS user",
        organizationId: tokens.organizationId ?? decoded.org_id ?? null,
        sessionId: tokens.sessionId
      });

      if (!result || result.error) {
        throw new Error(result?.error || "The frontend session could not be created.");
      }
    }

    async function completeSignIn() {
      try {
        if (flow === "oidc") {
          const storedState = sessionStorage.getItem(oidcStateStorageKey);
          const codeVerifier = sessionStorage.getItem(oidcVerifierStorageKey);
          const redirectUri = sessionStorage.getItem(oidcRedirectUriStorageKey);
          const clientId = sessionStorage.getItem(oidcClientIdStorageKey);

          if (!storedState || !codeVerifier || !redirectUri || !clientId) {
            throw new Error("The OIDC browser login state is missing or expired.");
          }

          if (storedState !== state) {
            throw new Error("OIDC state validation failed.");
          }

          const response = await fetch(`${authServerUrl}/oidc/exchange`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              code,
              clientId,
              redirectUri,
              codeVerifier
            })
          });

          const data = (await response.json()) as LoginResult & { message?: string };
          if (!response.ok) {
            throw new Error(data.message || "OIDC sign-in failed.");
          }

          clearOidcFlowStorage();

          if (data.requiresOrganizationSelection) {
            if (!data.pendingAuthToken) {
              throw new Error("The OIDC organization-selection state is missing.");
            }

            if (!cancelled) {
              setPendingSelection({
                pendingAuthToken: data.pendingAuthToken,
                organizations: data.organizations ?? []
              });
              setMessage("Select an organization to finish sign-in.");
            }
            return;
          }

          if (!data.tokens) {
            throw new Error("The OIDC exchange did not return session tokens.");
          }

          await createFrontendSession(data.tokens);
        } else {
          const response = await fetch(`${apiUrl}/api/v1/auth/sso/exchange`, {
            method: "POST",
            credentials: "include",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ code, state })
          });

          const data = await response.json();
          if (!response.ok) {
            throw new Error(data.message || "SSO exchange failed.");
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

  async function handleOrganizationSelect(organizationId: string) {
    if (!pendingSelection) {
      return;
    }

    setMessage("Completing organization selection...");

    try {
      const response = await fetch(`${authServerUrl}/select-organization`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          pendingAuthToken: pendingSelection.pendingAuthToken,
          organizationId
        })
      });
      const data = (await response.json()) as TokenResponse & { message?: string };
      if (!response.ok) {
        throw new Error(data.message || "Organization selection failed.");
      }

      const decoded = jwtDecode<DecodedToken>(data.accessToken);
      const result = await signIn("credentials", {
        redirect: false,
        accessToken: data.accessToken,
        refreshToken: data.refreshToken,
        userId: decoded.sub ?? "",
        email: decoded.email ?? "",
        displayName: decoded.name ?? decoded.email ?? decoded.sub ?? "SqlOS user",
        organizationId: data.organizationId ?? decoded.org_id ?? null,
        sessionId: data.sessionId
      });

      if (!result || result.error) {
        throw new Error(result?.error || "The frontend session could not be created.");
      }

      router.replace("/app");
      router.refresh();
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Organization selection failed.");
    }
  }

  if (pendingSelection) {
    return (
      <div className="stacked-actions">
        <p>{message}</p>
        {pendingSelection.organizations.map((organization) => (
          <button
            key={organization.id}
            className="secondary"
            type="button"
            onClick={() => void handleOrganizationSelect(organization.id)}
          >
            Continue with {organization.name}
          </button>
        ))}
      </div>
    );
  }

  return <p>{message}</p>;
}
