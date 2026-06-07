"use client";

import { useSession } from "next-auth/react";
import { useEffect, useState } from "react";
import { apiGet, apiPost } from "@/lib/api";

type MfaStatus = {
  mfaEnabled: boolean;
  required: boolean;
  enrollmentRequired: boolean;
  userSelfEnrollmentEnabled: boolean;
  hasTotp: boolean;
  recoveryCodeCount: number;
  availableFactors: string[];
  policyReason?: string | null;
};

type TotpEnrollment = {
  enrollmentToken: string;
  authenticatorId: string;
  secret: string;
  provisioningUri: string;
  qrCodeDataUrl: string;
  expiresAt: string;
};

type TotpEnrollmentResult = {
  authenticatorId: string;
  recoveryCodes: string[];
};

export default function AccountPage() {
  const { data: session } = useSession();
  const [status, setStatus] = useState<MfaStatus | null>(null);
  const [enrollment, setEnrollment] = useState<TotpEnrollment | null>(null);
  const [code, setCode] = useState("");
  const [recoveryCodes, setRecoveryCodes] = useState<string[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!session?.accessToken) return;
    apiGet<MfaStatus>("/api/mfa/status", session.accessToken)
      .then(setStatus)
      .catch((e) => setError(e.message));
  }, [session?.accessToken]);

  async function startEnrollment() {
    if (!session?.accessToken) return;
    setLoading(true);
    setError(null);
    try {
      const result = await apiPost<TotpEnrollment>("/api/mfa/totp/enroll/start", session.accessToken, {
        displayName: "Authenticator app",
      });
      setEnrollment(result);
      setRecoveryCodes([]);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Unable to start enrollment.");
    } finally {
      setLoading(false);
    }
  }

  async function verifyEnrollment() {
    if (!session?.accessToken || !enrollment) return;
    setLoading(true);
    setError(null);
    try {
      const result = await apiPost<TotpEnrollmentResult>("/api/mfa/totp/enroll/verify", session.accessToken, {
        enrollmentToken: enrollment.enrollmentToken,
        code,
      });
      setRecoveryCodes(result.recoveryCodes ?? []);
      setEnrollment(null);
      setCode("");
      const nextStatus = await apiGet<MfaStatus>("/api/mfa/status", session.accessToken);
      setStatus(nextStatus);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Unable to verify enrollment.");
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="dashboard-page">
      <div className="page-header-row">
        <div>
          <h1>Account Security</h1>
          <p className="muted">Manage second-factor authentication for this account.</p>
        </div>
      </div>

      {error && <div className="error-banner">{error}</div>}

      <section className="data-card">
        <div className="card-header">
          <div>
            <h2>Two-step Verification</h2>
            <p className="muted">
              {status?.mfaEnabled ? "Authenticator apps are available for this account." : "MFA is disabled by this application."}
            </p>
          </div>
          <span className={`badge ${status?.hasTotp ? "badge-success" : status?.required ? "badge-warning" : ""}`}>
            {status?.hasTotp ? "Enabled" : status?.required ? "Required" : "Optional"}
          </span>
        </div>

        <div className="detail-grid">
          <div>
            <span className="detail-label">Policy</span>
            <strong>{status?.required ? `Required${status.policyReason ? ` (${status.policyReason})` : ""}` : "Optional"}</strong>
          </div>
          <div>
            <span className="detail-label">Recovery codes</span>
            <strong>{status?.recoveryCodeCount ?? 0} available</strong>
          </div>
          <div>
            <span className="detail-label">Enrollment</span>
            <strong>{status?.userSelfEnrollmentEnabled ? "Allowed" : "Admin disabled"}</strong>
          </div>
        </div>

        {!status?.hasTotp && status?.mfaEnabled && status?.userSelfEnrollmentEnabled && (
          <button type="button" className="primary-button" onClick={startEnrollment} disabled={loading}>
            {loading ? "Starting..." : "Add authenticator app"}
          </button>
        )}
      </section>

      {enrollment && (
        <section className="data-card">
          <div className="card-header">
            <div>
              <h2>Add Authenticator App</h2>
              <p className="muted">Scan the QR code with your authenticator app, then verify the 6-digit code.</p>
            </div>
          </div>
          <div className="totp-setup">
            <div className="totp-qr-frame">
              <img src={enrollment.qrCodeDataUrl} alt="Authenticator setup QR code" />
            </div>
            <div className="totp-setup-copy">
              <strong>Scan with an authenticator app</strong>
              <p className="muted">Use Google Authenticator, 1Password, Authy, or any app that supports TOTP codes.</p>
              <details>
                <summary>Use manual setup</summary>
                <div className="code-block">{enrollment.secret}</div>
                <div className="code-block code-block--small">{enrollment.provisioningUri}</div>
              </details>
            </div>
          </div>
          <div className="form-row">
            <input
              value={code}
              onChange={(event) => setCode(event.target.value)}
              inputMode="numeric"
              autoComplete="one-time-code"
              placeholder="123456"
            />
            <button type="button" className="primary-button" onClick={verifyEnrollment} disabled={loading || code.trim().length < 6}>
              {loading ? "Verifying..." : "Verify"}
            </button>
          </div>
        </section>
      )}

      {recoveryCodes.length > 0 && (
        <section className="data-card">
          <h2>Recovery Codes</h2>
          <p className="muted">Store these one-time codes before leaving this page.</p>
          <div className="recovery-grid">
            {recoveryCodes.map((item) => (
              <code key={item}>{item}</code>
            ))}
          </div>
        </section>
      )}
    </div>
  );
}
