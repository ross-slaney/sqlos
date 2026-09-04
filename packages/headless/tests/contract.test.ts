import { describe, expect, it, vi } from "vitest";
import {
  createHeadlessFlow,
  HEADLESS_ACTION_PATHS,
  HEADLESS_REQUEST_FIELDS,
  type HeadlessActionPath,
  type HeadlessFlow,
  type HeadlessViewModel,
} from "../src/index.js";

// Holds flow.ts to contract.ts: every typed action must post only the fields
// the server record for that route binds, and every route must have a typed
// action. scripts/verify-headless-contract.mjs holds contract.ts to the C#.

const issuer = "https://id.example.com/sqlos/auth";
const clientId = "acme-app";
const redirectUri = "https://app.example.com/auth/callback";

const totpEnrollment = {
  enrollmentToken: "enroll_1",
  authenticatorId: "auth_1",
  secret: "SECRET",
  provisioningUri: "otpauth://totp/x",
  qrCodeDataUrl: "data:image/png;base64,",
  expiresAt: "2030-01-01T00:00:00Z",
};

/** A view model carrying every opaque token so each action's preconditions pass. */
function loadedViewModel(): HeadlessViewModel {
  return {
    view: "login",
    authBasePath: "/sqlos/auth",
    headlessApiBasePath: "/sqlos/auth/headless",
    requestId: "req_1",
    email: "ada@example.com",
    phoneNumber: "+12025550148",
    fieldErrors: {},
    organizationSelection: [],
    providers: [],
    challengeToken: "challenge_1",
    signupToken: "signup_1",
    pendingToken: "pending_1",
    mfaToken: "mfa_1",
    consentToken: "consent_1",
    totpEnrollment,
  };
}

type Recorded = { path: HeadlessActionPath; keys: string[] };

function createRecordingFlow(): { flow: HeadlessFlow; posts: Recorded[] } {
  const posts: Recorded[] = [];
  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input));
    const path = url.pathname.replace("/sqlos/auth/headless", "") as HeadlessActionPath;
    if (init?.method === "POST") {
      const body = JSON.parse(String(init.body)) as Record<string, unknown>;
      posts.push({ path, keys: Object.keys(body).filter((key) => body[key] !== undefined) });
    }
    if (path === "/password/reset") {
      return new Response(null, { status: 204 });
    }
    if (path === "/password/forgot") {
      return Response.json({
        email: "ada@example.com",
        maskedEmail: "a***@example.com",
        message: "Sent.",
        expiresAt: "2030-01-01T00:00:00Z",
        nextAllowedSendAt: "2030-01-01T00:01:00Z",
      });
    }
    if (path === "/invitations/resolve" || path === "/device/resolve" || path.startsWith("/requests/")) {
      return Response.json(loadedViewModel());
    }
    return Response.json({ type: "view", viewModel: loadedViewModel() });
  });
  return {
    flow: createHeadlessFlow({ issuer, clientId, redirectUri, fetch }),
    posts,
  };
}

const actions: Record<HeadlessActionPath, (flow: HeadlessFlow) => Promise<unknown>> = {
  "/start": (flow) => flow.start({ scope: "openid", view: "login", prompt: "login", resource: "r", loginHint: "ada@example.com", nonce: "n", uiContext: { a: 1 }, invitationToken: "inv_1", maxAge: "60" }),
  "/invitations/resolve": (flow) => flow.invitation.resolve({ invitationToken: "inv_1" }),
  "/device/resolve": (flow) => flow.device.resolve({ userCode: "ABCD-EFGH" }),
  "/device/approve": (flow) => flow.device.approve({ userCode: "ABCD-EFGH", organizationId: "org_1" }),
  "/device/deny": (flow) => flow.device.deny({ userCode: "ABCD-EFGH" }),
  "/consent/approve": (flow) => flow.consent.approve(),
  "/consent/deny": (flow) => flow.consent.deny(),
  "/identify": (flow) => flow.identify({ email: "ada@example.com", invitationToken: "inv_1" }),
  "/password/login": (flow) => flow.password.login({ password: "secret", invitationToken: "inv_1" }),
  "/password/forgot": (flow) => flow.password.forgot(),
  "/password/reset": (flow) => flow.password.reset({ token: "t", newPassword: "new-secret" }),
  "/email-otp/start": (flow) => flow.emailOtp.start({ invitationToken: "inv_1" }),
  "/email-otp/verify": (flow) => flow.emailOtp.verify({ code: "123456", invitationToken: "inv_1" }),
  "/magic-link/start": (flow) => flow.magicLink.start({ invitationToken: "inv_1" }),
  "/magic-link/complete": (flow) => flow.magicLink.complete({ token: "ml_1", invitationToken: "inv_1" }),
  "/signup/email-otp/start": (flow) => flow.emailOtp.signupStart({ displayName: "Ada", organizationName: "Acme", customFields: { a: 1 }, invitationToken: "inv_1" }),
  "/signup/email-otp/verify": (flow) => flow.emailOtp.signupVerify({ code: "123456", invitationToken: "inv_1" }),
  "/invitations/signup": (flow) => flow.invitation.signup({ displayName: "Ada", customFields: { a: 1 }, invitationToken: "inv_1" }),
  "/phone-otp/start": (flow) => flow.phoneOtp.start({ phoneNumber: "+12025550148", invitationToken: "inv_1" }),
  "/phone-otp/verify": (flow) => flow.phoneOtp.verify({ code: "123456", invitationToken: "inv_1" }),
  "/signup/phone-otp/start": (flow) => flow.phoneOtp.signupStart({ displayName: "Ada", phoneNumber: "+12025550148", organizationName: "Acme", customFields: { a: 1 }, invitationToken: "inv_1" }),
  "/signup/phone-otp/verify": (flow) => flow.phoneOtp.signupVerify({ code: "123456", invitationToken: "inv_1" }),
  "/signup": (flow) => flow.signup({ displayName: "Ada", password: "secret", organizationName: "Acme", customFields: { a: 1 }, invitationToken: "inv_1" }),
  "/organization/select": (flow) => flow.organization.select({ organizationId: "org_1" }),
  "/mfa/verify": (flow) => flow.mfa.verify({ code: "123456" }),
  "/mfa/totp/enroll/start": (flow) => flow.mfa.totp.enrollStart({ displayName: "Authenticator" }),
  "/mfa/totp/enroll/verify": (flow) => flow.mfa.totp.enrollVerify({ code: "123456" }),
  "/provider/start": (flow) => flow.provider.start({ connectionId: "github", invitationToken: "inv_1" }),
};

describe("typed actions match HEADLESS_REQUEST_FIELDS", () => {
  it("covers every headless POST route with a typed action", () => {
    expect(Object.keys(actions).sort()).toEqual([...HEADLESS_ACTION_PATHS].sort());
    expect(Object.keys(HEADLESS_REQUEST_FIELDS).sort()).toEqual([...HEADLESS_ACTION_PATHS].sort());
  });

  for (const path of HEADLESS_ACTION_PATHS) {
    it(`${path} posts only contract fields`, async () => {
      const { flow, posts } = createRecordingFlow();
      if (path !== "/start" && path !== "/invitations/resolve") {
        await flow.resume("https://app.example.com/auth/authorize?request=req_1");
      }
      await actions[path](flow);

      const recorded = posts.find((post) => post.path === path);
      expect(recorded, `expected a POST to ${path}`).toBeDefined();
      const allowed = HEADLESS_REQUEST_FIELDS[path] as readonly string[];
      const unknown = recorded!.keys.filter((key) => !allowed.includes(key));
      expect(unknown, `${path} posted fields the server does not bind`).toEqual([]);
      // Every action exercises the full record it can (optional fields included).
      const unused = allowed.filter((key) => !recorded!.keys.includes(key));
      expect(unused, `${path} never sends these contract fields`).toEqual([]);
    });
  }
});
