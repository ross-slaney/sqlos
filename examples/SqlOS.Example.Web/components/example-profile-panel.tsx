"use client";

import { useEffect, useMemo, useState } from "react";
import { useSession } from "next-auth/react";
import { apiGet } from "@/lib/api";

type ExampleProfileResponse = {
  userId: string;
  email?: string | null;
  displayName?: string | null;
  organizationId?: string | null;
  profile?: {
    referralSource: string;
    organizationName?: string | null;
    defaultEmail?: string | null;
    displayName?: string | null;
    createdAt: string;
    updatedAt: string;
  } | null;
};

const referralLabels: Record<string, string> = {
  docs: "SqlOS docs or examples",
  emcy: "Emcy or MCP integration work",
  friend: "Recommendation from a teammate",
  review: "Build vs. buy auth evaluation",
};

export function ExampleProfilePanel() {
  const { data: session, status } = useSession();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [profile, setProfile] = useState<ExampleProfileResponse | null>(null);

  useEffect(() => {
    if (!session?.accessToken) {
      return;
    }

    let cancelled = false;
    setLoading(true);
    setError(null);

    apiGet<ExampleProfileResponse>("/api/profile", session.accessToken)
      .then((response) => {
        if (!cancelled) {
          setProfile(response);
        }
      })
      .catch((loadError) => {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : "Failed to load example profile.");
        }
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [session?.accessToken]);

  const referralLabel = useMemo(() => {
    const raw = profile?.profile?.referralSource;
    if (!raw) return null;
    return referralLabels[raw] ?? raw;
  }, [profile?.profile?.referralSource]);

  return (
    <section className="card">
      <h2>Headless custom field demo</h2>
      <p className="muted">
        This card reads app-owned profile data created during headless signup. SqlOS handled the
        OAuth flow, but the referral source belongs to the example app.
      </p>
      {loading ? <p className="muted">Loading profile...</p> : null}
      {error ? <p className="error">{error}</p> : null}

      {!loading && !error && profile?.profile ? (
        <div className="detail-list">
          <div className="detail-row">
            <span>Referral source</span>
            <strong>{referralLabel}</strong>
          </div>
          <div className="detail-row">
            <span>Organization</span>
            <strong>{profile.profile.organizationName ?? profile.organizationId ?? "n/a"}</strong>
          </div>
          <div className="detail-row">
            <span>Signed-in email</span>
            <strong>{profile.profile.defaultEmail ?? profile.email ?? "n/a"}</strong>
          </div>
          <div className="detail-row">
            <span>Stored by</span>
            <strong>OnHeadlessSignupAsync</strong>
          </div>
        </div>
      ) : null}

      {!loading && !error && profile && !profile.profile ? (
        <div className="empty-state">
          <strong>No app-owned headless signup profile yet.</strong>
          <p className="muted">
            Use the headless signup flow to create a profile with a referral source, then come back
            here to see the saved data.
          </p>
        </div>
      ) : null}

      {status === "unauthenticated" ? (
        <div className="empty-state">
          <strong>Sign in first.</strong>
          <p className="muted">This card only loads after NextAuth has a valid SqlOS access token.</p>
        </div>
      ) : null}
    </section>
  );
}
