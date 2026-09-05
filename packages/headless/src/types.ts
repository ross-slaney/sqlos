import type { HeadlessActionPath, HeadlessCredentialType, HeadlessView } from "./contract.js";

export type { HeadlessActionPath, HeadlessCredentialType, HeadlessView };

export type JsonObject = Record<string, unknown>;

export type HeadlessProvider = {
  connectionId: string;
  providerType: string;
  displayName: string;
  logoDataUrl?: string | null;
};

export type HeadlessDeviceAuthorization = {
  userCode: string;
  clientId: string;
  clientName: string;
  scope: string;
  resource?: string | null;
  expiresAt: string;
  status: string;
};

export type HeadlessOrganizationOption = {
  id: string;
  slug: string;
  name: string;
  role: string;
};

export type HeadlessConsentScope = {
  scope: string;
  displayName: string;
  description?: string | null;
};

export type HeadlessTotpEnrollment = {
  enrollmentToken: string;
  authenticatorId: string;
  secret: string;
  provisioningUri: string;
  qrCodeDataUrl: string;
  expiresAt: string;
};

export type HeadlessInvitation = {
  id: string;
  organizationId: string;
  organizationName: string;
  email: string;
  role: string;
  status: string;
  inviteUrl?: string | null;
  createdAt: string;
  expiresAt: string;
  lastSentAt?: string | null;
  acceptedAt?: string | null;
  acceptedByUserId?: string | null;
  revokedAt?: string | null;
  revokedReason?: string | null;
  lastSendError?: string | null;
  customFields?: JsonObject | null;
};

export type HeadlessConfigurationOwnership = {
  owner: string;
  sourceKey?: string | null;
  lastReconciledAt?: string | null;
  configurationFingerprint?: string | null;
  isEditable: boolean;
  canEmergencyDisable: boolean;
  isOrphaned: boolean;
};

export type HeadlessSettings = {
  logoBase64?: string | null;
  primaryColor?: string;
  accentColor?: string;
  backgroundColor?: string;
  layout?: string;
  pageTitle?: string;
  pageSubtitle?: string;
  enablePasswordSignup?: boolean;
  enabledCredentialTypes?: string[];
  updatedAt?: string;
  managedByStartupSeed?: boolean;
  headlessCapabilityRegistered?: boolean;
  localPasswordRuntimeEnabled?: boolean;
  emailOtpRuntimeConfigured?: boolean;
  magicLinkRuntimeConfigured?: boolean;
  phoneOtpRuntimeConfigured?: boolean;
  ownership?: HeadlessConfigurationOwnership | null;
};

export type HeadlessViewModel = {
  view: HeadlessView;
  authBasePath: string;
  headlessApiBasePath: string;
  settings?: HeadlessSettings | null;
  requestId?: string | null;
  clientId?: string | null;
  clientName?: string | null;
  email?: string | null;
  displayName?: string | null;
  error?: string | null;
  info?: string | null;
  fieldErrors: Record<string, string>;
  challengeToken?: string | null;
  signupToken?: string | null;
  pendingToken?: string | null;
  organizationSelection: HeadlessOrganizationOption[];
  providers: HeadlessProvider[];
  invitation?: HeadlessInvitation | null;
  uiContext?: JsonObject | null;
  deviceAuthorization?: HeadlessDeviceAuthorization | null;
  phoneNumber?: string | null;
  mfaToken?: string | null;
  requiresMfaEnrollment?: boolean;
  mfaMethods?: string[] | null;
  totpEnrollment?: HeadlessTotpEnrollment | null;
  scope?: string;
  omittedOpenId?: boolean;
  consentToken?: string | null;
  consentScopes?: HeadlessConsentScope[] | null;
};

export type HeadlessActionResultType = "view" | "redirect";

export type HeadlessActionResult = {
  type: HeadlessActionResultType;
  redirectUrl?: string | null;
  viewModel?: HeadlessViewModel | null;
};

/** Response of `password.forgot()`; exposed as `flow.passwordReset`. */
export type HeadlessPasswordResetRequestResult = {
  email: string;
  maskedEmail: string;
  message: string;
  expiresAt: string;
  nextAllowedSendAt: string;
};

export type HeadlessFlowStatus = "idle" | "loading" | "view" | "redirect" | "error";

export type HeadlessAuthorization = {
  code: string;
  /** The exact registered redirect URI, including any registered query string. */
  redirectUri: string;
  state: string | null;
  codeVerifier: string | null;
};

export type HeadlessPkcePair = {
  codeVerifier: string;
  codeChallenge: string;
  codeChallengeMethod: "S256";
};

/** Crypto primitives for `generatePkce` on runtimes without Web Crypto. */
export type HeadlessPkcePrimitives = {
  randomBytes?: (size: number) => Uint8Array | Promise<Uint8Array>;
  sha256?: (data: Uint8Array) => Uint8Array | Promise<Uint8Array>;
};

export type LocationLike = {
  href?: string;
  search?: string;
  pathname?: string;
  toString?: () => string;
};

export type CreateHeadlessFlowOptions = {
  issuer: string;
  clientId: string;
  redirectUri: string;
  credentials?: RequestCredentials;
  fetch?: typeof fetch;
  generatePkce?: () => Promise<HeadlessPkcePair>;
  headlessApiBasePath?: string;
};

export type HeadlessStartInput = {
  responseType?: "code";
  scope?: string;
  prompt?: string;
  view?: string;
  state?: string;
  nonce?: string;
  resource?: string;
  loginHint?: string;
  invitationToken?: string;
  maxAge?: string;
  uiContext?: JsonObject;
  codeVerifier?: string;
  codeChallenge?: string;
  codeChallengeMethod?: "S256";
};

export type HeadlessIdentifyInput = {
  email: string;
  invitationToken?: string;
};

export type HeadlessPasswordLoginInput = {
  password: string;
  email?: string;
  invitationToken?: string;
};

export type HeadlessSignupInput = {
  displayName: string;
  password: string;
  email?: string;
  organizationName?: string;
  customFields?: JsonObject;
  invitationToken?: string;
};

export type HeadlessSubmitOptions = {
  /**
   * How to interpret the response body. `"auto"` (default) treats a body with
   * `type` as an action result and a body with `view` as a view model.
   */
  parse?: "auto" | "action" | "view";
};

export interface HeadlessFlow {
  readonly status: HeadlessFlowStatus;
  readonly viewModel: HeadlessViewModel | null;
  readonly error: string | null;
  readonly fieldErrors: Record<string, string>;
  readonly authorization: HeadlessAuthorization | null;
  readonly redirectUrl: string | null;
  /** Result of the last successful `password.forgot()` on this flow. */
  readonly passwordReset: HeadlessPasswordResetRequestResult | null;

  subscribe(listener: () => void): () => void;

  resume(location: LocationLike | string): Promise<HeadlessFlowStatus>;
  start(input?: HeadlessStartInput): Promise<HeadlessFlowStatus>;

  /**
   * Escape hatch: post to a headless route the SDK does not name yet, through
   * the same status / token bookkeeping as the typed actions. `requestId` is
   * filled in from the loaded request unless `body` provides it.
   */
  submit(
    path: HeadlessActionPath | (string & {}),
    body?: JsonObject,
    options?: HeadlessSubmitOptions,
  ): Promise<HeadlessFlowStatus>;

  identify(input: HeadlessIdentifyInput): Promise<HeadlessFlowStatus>;
  password: {
    login(input: HeadlessPasswordLoginInput): Promise<HeadlessFlowStatus>;
    forgot(input?: { email?: string }): Promise<HeadlessFlowStatus>;
    reset(input: { token: string; newPassword: string }): Promise<HeadlessFlowStatus>;
  };
  emailOtp: {
    start(input?: { email?: string; invitationToken?: string }): Promise<HeadlessFlowStatus>;
    verify(input: { code: string; invitationToken?: string }): Promise<HeadlessFlowStatus>;
    signupStart(input: {
      displayName: string;
      email?: string;
      organizationName?: string;
      customFields?: JsonObject;
      invitationToken?: string;
    }): Promise<HeadlessFlowStatus>;
    signupVerify(input: { code: string; invitationToken?: string }): Promise<HeadlessFlowStatus>;
  };
  magicLink: {
    start(input?: { email?: string; invitationToken?: string }): Promise<HeadlessFlowStatus>;
    complete(input: { token: string; invitationToken?: string }): Promise<HeadlessFlowStatus>;
  };
  phoneOtp: {
    start(input: { phoneNumber: string; invitationToken?: string }): Promise<HeadlessFlowStatus>;
    verify(input: { code: string; invitationToken?: string }): Promise<HeadlessFlowStatus>;
    signupStart(input: {
      displayName: string;
      phoneNumber: string;
      organizationName?: string;
      customFields?: JsonObject;
      invitationToken?: string;
    }): Promise<HeadlessFlowStatus>;
    signupVerify(input: { code: string; invitationToken?: string }): Promise<HeadlessFlowStatus>;
  };
  signup(input: HeadlessSignupInput): Promise<HeadlessFlowStatus>;
  organization: {
    select(input: { organizationId: string }): Promise<HeadlessFlowStatus>;
  };
  mfa: {
    verify(input: { code: string }): Promise<HeadlessFlowStatus>;
    totp: {
      enrollStart(input?: { displayName?: string }): Promise<HeadlessFlowStatus>;
      enrollVerify(input: { code: string }): Promise<HeadlessFlowStatus>;
    };
  };
  consent: {
    approve(): Promise<HeadlessFlowStatus>;
    deny(): Promise<HeadlessFlowStatus>;
  };
  invitation: {
    resolve(input: { invitationToken: string }): Promise<HeadlessFlowStatus>;
    signup(input: {
      displayName: string;
      email?: string;
      customFields?: JsonObject;
      invitationToken: string;
    }): Promise<HeadlessFlowStatus>;
  };
  device: {
    resolve(input?: { userCode?: string }): Promise<HeadlessFlowStatus>;
    approve(input?: { userCode?: string; organizationId?: string }): Promise<HeadlessFlowStatus>;
    deny(input?: { userCode?: string }): Promise<HeadlessFlowStatus>;
  };
  provider: {
    start(input: {
      connectionId: string;
      email?: string;
      invitationToken?: string;
    }): Promise<HeadlessFlowStatus>;
  };
}

/** Snapshot values returned by `useHeadlessAuth` for referentially-updating UI. */
export type UseHeadlessAuthResult = {
  flow: HeadlessFlow;
  status: HeadlessFlowStatus;
  view: HeadlessView | null;
  viewModel: HeadlessViewModel | null;
  error: string | null;
  fieldErrors: Record<string, string>;
  authorization: HeadlessAuthorization | null;
  redirectUrl: string | null;
  passwordReset: HeadlessPasswordResetRequestResult | null;
};
