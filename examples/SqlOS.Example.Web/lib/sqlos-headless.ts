"use client";

import { getExampleAuthServerUrl } from "./sqlos-auth";

const headlessBase = () => `${getExampleAuthServerUrl()}/headless`;

export type HeadlessViewModel = {
  requestId: string;
  view: string;
  clientId: string;
  headlessApiBasePath: string;
  error?: string | null;
  pendingToken?: string | null;
  email?: string | null;
  displayName?: string | null;
  uiContext?: Record<string, unknown> | null;
  providers?: HeadlessProvider[];
  organizationSelection?: HeadlessOrganizationOption[];
  settings?: HeadlessSettings | null;
  fieldErrors?: Record<string, string>;
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
};

export type HeadlessActionResult = {
  type: "redirect" | "view";
  redirectUrl?: string;
  viewModel?: HeadlessViewModel;
};

async function headlessPost(path: string, body: unknown): Promise<HeadlessActionResult> {
  const res = await fetch(`${headlessBase()}${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });

  if (!res.ok) {
    const text = await res.text();
    throw new Error(text || `Headless API error: ${res.status}`);
  }

  return res.json();
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

  const res = await fetch(url.toString());
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

export async function headlessSelectOrganization(pendingToken: string, organizationId: string): Promise<HeadlessActionResult> {
  return headlessPost("/organization/select", { pendingToken, organizationId });
}

export async function headlessStartProvider(requestId: string, connectionId: string, email?: string): Promise<HeadlessActionResult> {
  return headlessPost("/provider/start", { requestId, connectionId, email });
}
