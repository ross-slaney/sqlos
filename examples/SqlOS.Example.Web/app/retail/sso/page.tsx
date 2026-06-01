"use client";

import { useState } from "react";
import { useSession } from "next-auth/react";
import { apiPost } from "@/lib/api";

type PortalSession = {
  id: string;
  organizationId: string;
  organizationName: string;
  primaryDomain?: string | null;
  status: string;
  provider?: string | null;
  setupUrl?: string | null;
  portalUrl: string;
  expiresAt: string;
};

const providers = [
  { value: "", label: "Let IT choose" },
  { value: "microsoft-entra", label: "Microsoft Entra" },
  { value: "okta", label: "Okta" },
  { value: "google-workspace", label: "Google Workspace" },
  { value: "generic-saml", label: "Generic SAML" },
];

export default function SsoPortalPage() {
  const { data: session } = useSession();
  const [provider, setProvider] = useState("");
  const [portalSession, setPortalSession] = useState<PortalSession | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function createLink() {
    if (!session?.accessToken) return;
    setBusy(true);
    setError(null);
    try {
      const result = await apiPost<PortalSession>("/api/sso-portal-links", session.accessToken, {
        provider: provider || null,
      });
      setPortalSession(result);
      if (result.setupUrl && navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(result.setupUrl);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unable to create setup link.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="gap-20">
      <div className="page-header">
        <h1>SSO Portal</h1>
        <p>Launch a delegated setup session for the current organization.</p>
      </div>

      <div className="card">
        <h2>Create Setup Link</h2>
        <p className="muted">The link is scoped to organization {session?.organizationId ?? "n/a"} and opens a one-time portal session.</p>
        <div className="form-row" style={{ marginTop: 12 }}>
          <select value={provider} onChange={(event) => setProvider(event.target.value)}>
            {providers.map((item) => (
              <option key={item.value} value={item.value}>{item.label}</option>
            ))}
          </select>
          <button type="button" onClick={() => void createLink()} disabled={busy || !session?.accessToken}>
            {busy ? "Creating..." : "Create link"}
          </button>
        </div>
        {error && <p className="error" style={{ marginTop: 12 }}>{error}</p>}
      </div>

      {portalSession?.setupUrl && (
        <div className="card">
          <h2>Latest Setup Link</h2>
          <div className="data-table-wrap" style={{ marginTop: 12 }}>
            <table className="data-table">
              <tbody>
                <tr><th>Session</th><td>{portalSession.id}</td></tr>
                <tr><th>Organization</th><td>{portalSession.organizationName}</td></tr>
                <tr><th>Status</th><td>{portalSession.status}</td></tr>
                <tr><th>Expires</th><td>{new Date(portalSession.expiresAt).toLocaleString()}</td></tr>
                <tr><th>Setup URL</th><td className="mono-cell">{portalSession.setupUrl}</td></tr>
              </tbody>
            </table>
          </div>
          <div className="form-row" style={{ marginTop: 12 }}>
            <button type="button" className="secondary" onClick={() => navigator.clipboard?.writeText(portalSession.setupUrl!)}>
              Copy link
            </button>
            <a className="button" href={portalSession.setupUrl} target="_blank" rel="noreferrer">
              Open portal
            </a>
          </div>
        </div>
      )}
    </div>
  );
}
