"use client";

import { getExampleAuthServerUrl } from "./sqlos-auth";

const headlessBase = () => `${getExampleAuthServerUrl()}/headless`;

export type HeadlessViewModel = {
  requestId: string;
  view: string;
  clientId: string;
  headlessApiBasePath: string;
  error?: string | null;
  info?: string | null;
  challengeToken?: string | null;
  signupToken?: string | null;
  pendingToken?: string | null;
  email?: string | null;
  phoneNumber?: string | null;
  displayName?: string | null;
  uiContext?: Record<string, unknown> | null;
  providers?: HeadlessProvider[];
  organizationSelection?: HeadlessOrganizationOption[];
  settings?: HeadlessSettings | null;
  fieldErrors?: Record<string, string>;
  mfaToken?: string | null;
  requiresMfaEnrollment?: boolean;
  mfaMethods?: string[] | null;
  totpEnrollment?: HeadlessTotpEnrollment | null;
};

export type HeadlessTotpEnrollment = {
  enrollmentToken: string;
  authenticatorId: string;
  secret: string;
  provisioningUri: string;
  qrCodeDataUrl: string;
  expiresAt: string;
};

export type HeadlessPasswordResetRequestResult = {
  email: string;
  maskedEmail: string;
  message: string;
  expiresAt: string;
  nextAllowedSendAt: string;
};

export type HeadlessProvider = {
  connectionId: string;
  providerType: string;
  displayName: string;
  logoDataUrl?: string | null;
};

export type HeadlessOrganizationOption = {
  id: string;
  name: string;
  primaryDomain?: string | null;
  role: string;
};

export type HeadlessSettings = {
  pageTitle?: string;
  pageSubtitle?: string;
  primaryColor?: string;
  accentColor?: string;
  backgroundColor?: string;
  enablePasswordSignup?: boolean;
  enabledCredentialTypes?: string[];
  localPasswordRuntimeEnabled?: boolean;
  emailOtpRuntimeConfigured?: boolean;
  magicLinkRuntimeConfigured?: boolean;
  phoneOtpRuntimeConfigured?: boolean;
};

export type HeadlessActionResult = {
  type: "redirect" | "view";
  redirectUrl?: string;
  viewModel?: HeadlessViewModel;
};

async function headlessPostJson<T>(path: string, body: unknown): Promise<T> {
  const res = await fetch(`${headlessBase()}${path}`, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });

  if (!res.ok) {
    const text = await res.text();
    throw new Error(text || `Headless API error: ${res.status}`);
  }

  if (res.status === 204) {
    return undefined as T;
  }

  return res.json() as Promise<T>;
}

async function headlessPost(path: string, body: unknown): Promise<HeadlessActionResult> {
  return headlessPostJson<HeadlessActionResult>(path, body);
}

export async function getHeadlessRequest(
  requestId: string,
  view?: string,
  error?: string | null,
  pendingToken?: string | null,
  email?: string | null,
  displayName?: string | null,
): Promise<HeadlessViewModel> {
  const url = new URL(`${headlessBase()}/requests/${requestId}`);
  if (view) url.searchParams.set("view", view);
  if (error) url.searchParams.set("error", error);
  if (pendingToken) url.searchParams.set("pendingToken", pendingToken);
  if (email) url.searchParams.set("email", email);
  if (displayName) url.searchParams.set("displayName", displayName);

  const res = await fetch(url.toString(), {
    credentials: "include",
  });
  if (!res.ok) {
    const text = await res.text();
    throw new Error(text || `Failed to load request: ${res.status}`);
  }

  return res.json();
}

export async function headlessIdentify(requestId: string, email: string): Promise<HeadlessActionResult> {
  return headlessPost("/identify", { requestId, email });
}

export async function headlessPasswordLogin(requestId: string, email: string, password: string): Promise<HeadlessActionResult> {
  return headlessPost("/password/login", { requestId, email, password });
}

export async function headlessRequestPasswordResetEmail(
  email: string,
  requestId?: string | null,
): Promise<HeadlessPasswordResetRequestResult> {
  return headlessPostJson<HeadlessPasswordResetRequestResult>("/password/forgot", { email, requestId });
}

export async function headlessResetPassword(token: string, newPassword: string): Promise<void> {
  await headlessPostJson<void>("/password/reset", { token, newPassword });
}

export async function headlessRequestEmailOtp(requestId: string, email: string): Promise<HeadlessActionResult> {
  return headlessPost("/email-otp/start", { requestId, email });
}

export async function headlessVerifyEmailOtp(requestId: string, challengeToken: string, code: string): Promise<HeadlessActionResult> {
  return headlessPost("/email-otp/verify", { requestId, challengeToken, code });
}

export async function headlessRequestMagicLink(requestId: string, email: string): Promise<HeadlessActionResult> {
  return headlessPost("/magic-link/start", { requestId, email });
}

export async function headlessCompleteMagicLink(token: string, requestId?: string | null): Promise<HeadlessActionResult> {
  return headlessPost("/magic-link/complete", { token, requestId });
}

export async function headlessRequestPhoneOtp(requestId: string, phoneNumber: string): Promise<HeadlessActionResult> {
  return headlessPost("/phone-otp/start", { requestId, phoneNumber });
}

export async function headlessVerifyPhoneOtp(requestId: string, challengeToken: string, code: string): Promise<HeadlessActionResult> {
  return headlessPost("/phone-otp/verify", { requestId, challengeToken, code });
}

export async function headlessSignup(
  requestId: string,
  displayName: string,
  email: string,
  password: string,
  organizationName: string,
  customFields?: Record<string, string>,
): Promise<HeadlessActionResult> {
  return headlessPost("/signup", {
    requestId,
    displayName,
    email,
    password,
    organizationName,
    customFields: customFields ?? {},
  });
}

export async function headlessRequestPhoneOtpSignup(
  requestId: string,
  displayName: string,
  phoneNumber: string,
  organizationName: string,
  customFields?: Record<string, string>,
): Promise<HeadlessActionResult> {
  return headlessPost("/signup/phone-otp/start", {
    requestId,
    displayName,
    phoneNumber,
    organizationName,
    customFields: customFields ?? {},
  });
}

export async function headlessVerifyPhoneOtpSignup(
  requestId: string,
  signupToken: string,
  challengeToken: string,
  code: string,
): Promise<HeadlessActionResult> {
  return headlessPost("/signup/phone-otp/verify", { requestId, signupToken, challengeToken, code });
}

export async function headlessSelectOrganization(pendingToken: string, organizationId: string): Promise<HeadlessActionResult> {
  return headlessPost("/organization/select", { pendingToken, organizationId });
}

export async function headlessStartProvider(requestId: string, connectionId: string, email?: string): Promise<HeadlessActionResult> {
  return headlessPost("/provider/start", { requestId, connectionId, email });
}

export async function headlessVerifyMfa(requestId: string, mfaToken: string, code: string): Promise<HeadlessActionResult> {
  return headlessPost("/mfa/verify", { requestId, mfaToken, code });
}

export async function headlessStartMfaTotpEnrollment(
  requestId: string,
  mfaToken: string,
  displayName?: string,
): Promise<HeadlessActionResult> {
  return headlessPost("/mfa/totp/enroll/start", { requestId, mfaToken, displayName });
}

export async function headlessVerifyMfaTotpEnrollment(
  requestId: string,
  mfaToken: string,
  enrollmentToken: string,
  code: string,
): Promise<HeadlessActionResult> {
  return headlessPost("/mfa/totp/enroll/verify", { requestId, mfaToken, enrollmentToken, code });
}
