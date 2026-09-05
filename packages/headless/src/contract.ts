/**
 * Wire contract mirrored from the SqlOS server. Every array and map in this
 * file is verified against the C# source by scripts/verify-headless-contract.mjs
 * (run as part of `npm test`), and the TypeScript types in types.ts are
 * checked against the field lists at the bottom of this file at compile time.
 * Keep the literal layout: the verify script parses this file textually.
 */
import type {
  HeadlessActionResult,
  HeadlessConfigurationOwnership,
  HeadlessConsentScope,
  HeadlessDeviceAuthorization,
  HeadlessInvitation,
  HeadlessOrganizationOption,
  HeadlessPasswordResetRequestResult,
  HeadlessProvider,
  HeadlessSettings,
  HeadlessTotpEnrollment,
  HeadlessViewModel,
} from "./types.js";

export const HEADLESS_VIEWS = [
  "login",
  "signup",
  "password",
  "forgot-password",
  "forgot-password-sent",
  "password-reset",
  "email-otp",
  "email-otp-verify",
  "email-otp-signup-verify",
  "magic-link",
  "magic-link-sent",
  "phone-otp",
  "phone-otp-verify",
  "phone-otp-signup",
  "phone-otp-signup-verify",
  "invite",
  "invite-login",
  "invite-email-otp-verify",
  "invite-accepted",
  "device",
  "device-approve",
  "device-approved",
  "device-denied",
  "mfa",
  "mfa-enroll",
  "organization",
  "consent",
  "logged-out",
] as const;

export type HeadlessView = (typeof HEADLESS_VIEWS)[number];

export const HEADLESS_ACTION_PATHS = [
  "/start",
  "/invitations/resolve",
  "/device/resolve",
  "/device/approve",
  "/device/deny",
  "/consent/approve",
  "/consent/deny",
  "/identify",
  "/password/login",
  "/password/forgot",
  "/password/reset",
  "/email-otp/start",
  "/email-otp/verify",
  "/magic-link/start",
  "/magic-link/complete",
  "/signup/email-otp/start",
  "/signup/email-otp/verify",
  "/invitations/signup",
  "/phone-otp/start",
  "/phone-otp/verify",
  "/signup/phone-otp/start",
  "/signup/phone-otp/verify",
  "/signup",
  "/organization/select",
  "/mfa/verify",
  "/mfa/totp/enroll/start",
  "/mfa/totp/enroll/verify",
  "/provider/start",
] as const;

export type HeadlessActionPath = (typeof HEADLESS_ACTION_PATHS)[number];

export const HEADLESS_GET_PATHS = ["/requests/{requestId}"] as const;

export const HEADLESS_ACTION_RESULT_TYPES = ["view", "redirect"] as const;

/**
 * Request body fields per POST route, camelCased from the C# request record
 * bound by that route. flow.ts must only post keys listed here.
 */
export const HEADLESS_REQUEST_FIELDS = {
  "/start": ["responseType", "clientId", "redirectUri", "state", "scope", "codeChallenge", "codeChallengeMethod", "resource", "loginHint", "prompt", "nonce", "view", "uiContext", "invitationToken", "maxAge"],
  "/invitations/resolve": ["invitationToken"],
  "/device/resolve": ["userCode", "requestId"],
  "/device/approve": ["userCode", "organizationId", "requestId"],
  "/device/deny": ["userCode", "requestId"],
  "/consent/approve": ["requestId", "consentToken"],
  "/consent/deny": ["requestId", "consentToken"],
  "/identify": ["requestId", "email", "invitationToken"],
  "/password/login": ["requestId", "email", "password", "invitationToken"],
  "/password/forgot": ["email", "requestId"],
  "/password/reset": ["token", "newPassword"],
  "/email-otp/start": ["requestId", "email", "invitationToken"],
  "/email-otp/verify": ["requestId", "challengeToken", "code", "invitationToken"],
  "/magic-link/start": ["requestId", "email", "invitationToken"],
  "/magic-link/complete": ["token", "requestId", "invitationToken"],
  "/signup/email-otp/start": ["requestId", "displayName", "email", "organizationName", "customFields", "invitationToken"],
  "/signup/email-otp/verify": ["requestId", "signupToken", "challengeToken", "code", "invitationToken"],
  "/invitations/signup": ["requestId", "displayName", "email", "customFields", "invitationToken"],
  "/phone-otp/start": ["requestId", "phoneNumber", "invitationToken"],
  "/phone-otp/verify": ["requestId", "challengeToken", "code", "invitationToken"],
  "/signup/phone-otp/start": ["requestId", "displayName", "phoneNumber", "organizationName", "customFields", "invitationToken"],
  "/signup/phone-otp/verify": ["requestId", "signupToken", "challengeToken", "code", "invitationToken"],
  "/signup": ["requestId", "displayName", "email", "password", "organizationName", "customFields", "invitationToken"],
  "/organization/select": ["pendingToken", "organizationId"],
  "/mfa/verify": ["requestId", "mfaToken", "code"],
  "/mfa/totp/enroll/start": ["requestId", "mfaToken", "displayName"],
  "/mfa/totp/enroll/verify": ["requestId", "mfaToken", "enrollmentToken", "code"],
  "/provider/start": ["requestId", "connectionId", "email", "invitationToken"],
} as const satisfies Record<HeadlessActionPath, readonly string[]>;

export const HEADLESS_VIEW_MODEL_FIELDS = [
  "view",
  "authBasePath",
  "headlessApiBasePath",
  "settings",
  "requestId",
  "clientId",
  "clientName",
  "email",
  "displayName",
  "error",
  "info",
  "fieldErrors",
  "challengeToken",
  "signupToken",
  "pendingToken",
  "organizationSelection",
  "providers",
  "invitation",
  "uiContext",
  "deviceAuthorization",
  "phoneNumber",
  "mfaToken",
  "requiresMfaEnrollment",
  "mfaMethods",
  "totpEnrollment",
  "scope",
  "omittedOpenId",
  "consentToken",
  "consentScopes",
] as const;

export const HEADLESS_ACTION_RESULT_FIELDS = [
  "type",
  "redirectUrl",
  "viewModel",
] as const;

/** Nested response records, keyed by the C# record name. */
export const HEADLESS_DTO_FIELDS = {
  "SqlOSHeadlessProviderDto": ["connectionId", "providerType", "displayName", "logoDataUrl"],
  "SqlOSHeadlessDeviceAuthorizationDto": ["userCode", "clientId", "clientName", "scope", "resource", "expiresAt", "status"],
  "SqlOSOrganizationOption": ["id", "slug", "name", "role"],
  "SqlOSConsentScopeDisplay": ["scope", "displayName", "description"],
  "SqlOSTotpEnrollmentStartResult": ["enrollmentToken", "authenticatorId", "secret", "provisioningUri", "qrCodeDataUrl", "expiresAt"],
  "SqlOSEmailInvitationResult": ["id", "organizationId", "organizationName", "email", "role", "status", "inviteUrl", "createdAt", "expiresAt", "lastSentAt", "acceptedAt", "acceptedByUserId", "revokedAt", "revokedReason", "lastSendError", "customFields"],
  "SqlOSAuthPageSettingsDto": ["logoBase64", "primaryColor", "accentColor", "backgroundColor", "layout", "pageTitle", "pageSubtitle", "enablePasswordSignup", "enabledCredentialTypes", "updatedAt", "managedByStartupSeed", "headlessCapabilityRegistered", "localPasswordRuntimeEnabled", "emailOtpRuntimeConfigured", "magicLinkRuntimeConfigured", "phoneOtpRuntimeConfigured", "ownership"],
  "SqlOSConfigurationOwnershipDto": ["owner", "sourceKey", "lastReconciledAt", "configurationFingerprint", "isEditable", "canEmergencyDisable", "isOrphaned"],
  "SqlOSPasswordResetRequestResult": ["email", "maskedEmail", "message", "expiresAt", "nextAllowedSendAt"],
} as const;

/** `settings.enabledCredentialTypes` values, as the server spells them. */
export const HEADLESS_CREDENTIAL_TYPES = ["password", "email_otp", "magic_link", "phone_otp"] as const;

export type HeadlessCredentialType = (typeof HEADLESS_CREDENTIAL_TYPES)[number];

/**
 * The runtime flag that must also be true for a credential type to be usable.
 * Mirrors the hosted AuthPage renderer's rule.
 */
export const HEADLESS_CREDENTIAL_RUNTIME_FLAGS = {
  "password": "localPasswordRuntimeEnabled",
  "email_otp": "emailOtpRuntimeConfigured",
  "magic_link": "magicLinkRuntimeConfigured",
  "phone_otp": "phoneOtpRuntimeConfigured",
} as const satisfies Record<HeadlessCredentialType, keyof HeadlessSettings>;

// ---------------------------------------------------------------------------
// Compile-time checks: the TypeScript types in types.ts carry exactly the
// fields listed above. A drift here fails `tsc` (run by `npm test` and by the
// tsup dts build), so a server rename cannot ship with stale types.
// ---------------------------------------------------------------------------

type SameKeys<Keys extends string, Fields extends readonly string[]> =
  [Exclude<Keys, Fields[number]>] extends [never]
    ? [Exclude<Fields[number], Keys>] extends [never]
      ? true
      : { missingFromType: Exclude<Fields[number], Keys> }
    : { missingFromContract: Exclude<Keys, Fields[number]> };

const typesMatchContract: {
  viewModel: SameKeys<keyof HeadlessViewModel, typeof HEADLESS_VIEW_MODEL_FIELDS>;
  actionResult: SameKeys<keyof HeadlessActionResult, typeof HEADLESS_ACTION_RESULT_FIELDS>;
  provider: SameKeys<keyof HeadlessProvider, (typeof HEADLESS_DTO_FIELDS)["SqlOSHeadlessProviderDto"]>;
  deviceAuthorization: SameKeys<keyof HeadlessDeviceAuthorization, (typeof HEADLESS_DTO_FIELDS)["SqlOSHeadlessDeviceAuthorizationDto"]>;
  organizationOption: SameKeys<keyof HeadlessOrganizationOption, (typeof HEADLESS_DTO_FIELDS)["SqlOSOrganizationOption"]>;
  consentScope: SameKeys<keyof HeadlessConsentScope, (typeof HEADLESS_DTO_FIELDS)["SqlOSConsentScopeDisplay"]>;
  totpEnrollment: SameKeys<keyof HeadlessTotpEnrollment, (typeof HEADLESS_DTO_FIELDS)["SqlOSTotpEnrollmentStartResult"]>;
  invitation: SameKeys<keyof HeadlessInvitation, (typeof HEADLESS_DTO_FIELDS)["SqlOSEmailInvitationResult"]>;
  settings: SameKeys<keyof HeadlessSettings, (typeof HEADLESS_DTO_FIELDS)["SqlOSAuthPageSettingsDto"]>;
  ownership: SameKeys<keyof HeadlessConfigurationOwnership, (typeof HEADLESS_DTO_FIELDS)["SqlOSConfigurationOwnershipDto"]>;
  passwordReset: SameKeys<keyof HeadlessPasswordResetRequestResult, (typeof HEADLESS_DTO_FIELDS)["SqlOSPasswordResetRequestResult"]>;
} = {
  viewModel: true,
  actionResult: true,
  provider: true,
  deviceAuthorization: true,
  organizationOption: true,
  consentScope: true,
  totpEnrollment: true,
  invitation: true,
  settings: true,
  ownership: true,
  passwordReset: true,
};
void typesMatchContract;
