import { describe, expect, it, vi } from "vitest";
import {
  createHeadlessFlow,
  HeadlessApiPathMismatchError,
  HeadlessFlowBusyError,
  HeadlessFlowNotLoadedError,
} from "../src/index.js";
import type { HeadlessViewModel } from "../src/types.js";

const issuer = "https://id.example.com/sqlos/auth";
const clientId = "acme-app";
const redirectUri = "https://app.example.com/auth/callback";

function viewModel(overrides: Partial<HeadlessViewModel> = {}): HeadlessViewModel {
  return {
    view: "login",
    authBasePath: "/sqlos/auth",
    headlessApiBasePath: "/sqlos/auth/headless",
    requestId: "req_1",
    clientId,
    fieldErrors: {},
    organizationSelection: [],
    providers: [],
    ...overrides,
  };
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function createFetch(handler: (url: string, init?: RequestInit) => unknown | Promise<unknown>) {
  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    const result = await handler(url, init);
    if (result instanceof Response) {
      return result;
    }
    return jsonResponse(result);
  });
}

function readBody(init?: RequestInit): Record<string, unknown> {
  return JSON.parse(String(init?.body ?? "{}")) as Record<string, unknown>;
}

describe("createHeadlessFlow", () => {
  it("resumes from ?request= and loads the view model", async () => {
    const fetch = createFetch((url) => {
      expect(url).toContain("/sqlos/auth/headless/requests/req_1");
      expect(url).toContain("view=login");
      return viewModel({ view: "password", email: "ada@example.com" });
    });
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri, fetch, credentials: "include" });
    await flow.resume("https://app.example.com/auth/authorize?request=req_1&view=login");
    expect(flow.status).toBe("view");
    expect(flow.viewModel?.view).toBe("password");
    expect(flow.viewModel?.email).toBe("ada@example.com");
    expect(fetch.mock.calls[0]?.[1]?.credentials).toBe("include");
  });

  it("rejects a missing request id", async () => {
    const flow = createHeadlessFlow({
      issuer,
      clientId,
      redirectUri,
      fetch: createFetch(() => viewModel()),
    });
    await expect(flow.resume("https://app.example.com/auth/authorize")).rejects.toBeInstanceOf(
      HeadlessFlowNotLoadedError,
    );
    expect(flow.status).toBe("error");
  });

  it("starts a native request with generated PKCE and never calls /token", async () => {
    const urls: string[] = [];
    const fetch = createFetch((url, init) => {
      urls.push(url);
      if (url.endsWith("/start")) {
        const body = readBody(init);
        expect(body.responseType).toBe("code");
        expect(body.clientId).toBe(clientId);
        expect(body.redirectUri).toBe("sqlos-expo://auth-callback");
        expect(body.codeChallenge).toEqual(expect.any(String));
        expect(body.codeChallengeMethod).toBe("S256");
        expect(body.scope).toBe("openid profile email");
        return {
          type: "view",
          viewModel: viewModel({ view: "login" }),
        };
      }
      throw new Error(`unexpected ${url}`);
    });
    const flow = createHeadlessFlow({
      issuer,
      clientId,
      redirectUri: "sqlos-expo://auth-callback",
      fetch,
    });
    await flow.start({ scope: "openid profile email", view: "login" });
    expect(flow.status).toBe("view");
    expect(urls.some((url) => url.includes("/token"))).toBe(false);
  });

  it("drives identify → password → authorization code without the caller threading tokens", async () => {
    const posts: Array<{ url: string; body: Record<string, unknown> }> = [];
    const fetch = createFetch((url, init) => {
      if (url.includes("/requests/req_1")) {
        return viewModel();
      }
      const body = readBody(init);
      posts.push({ url, body });
      if (url.endsWith("/identify")) {
        expect(body).toMatchObject({ requestId: "req_1", email: "ada@example.com" });
        return { type: "view", viewModel: viewModel({ view: "password", email: "ada@example.com" }) };
      }
      if (url.endsWith("/password/login")) {
        expect(body).toMatchObject({
          requestId: "req_1",
          email: "ada@example.com",
          password: "secret",
        });
        return {
          type: "redirect",
          redirectUrl: "https://app.example.com/auth/callback?code=abc&state=xyz",
        };
      }
      throw new Error(`unexpected ${url}`);
    });
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri, fetch, credentials: "include" });
    await flow.resume("https://app.example.com/auth/authorize?request=req_1");
    await flow.identify({ email: "ada@example.com" });
    await flow.password.login({ password: "secret" });
    expect(flow.status).toBe("redirect");
    expect(flow.authorization).toEqual({
      code: "abc",
      redirectUri: "https://app.example.com/auth/callback",
      state: "xyz",
      codeVerifier: null,
    });
    expect(posts[1]?.body.requestId).toBe("req_1");
  });

  it("keeps challenge tokens internal for OTP verify", async () => {
    const fetch = createFetch((url, init) => {
      if (url.includes("/requests/")) {
        return viewModel({ challengeToken: "challenge_1" });
      }
      if (url.endsWith("/email-otp/verify")) {
        expect(readBody(init)).toMatchObject({
          requestId: "req_1",
          challengeToken: "challenge_1",
          code: "123456",
        });
        return {
          type: "redirect",
          redirectUrl: `${redirectUri}?code=otp-code`,
        };
      }
      throw new Error(`unexpected ${url}`);
    });
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri, fetch });
    await flow.resume("https://app.example.com/auth/authorize?request=req_1");
    await flow.emailOtp.verify({ code: "123456" });
    expect(flow.authorization?.code).toBe("otp-code");
  });

  it("maps field errors from the view model", async () => {
    const fetch = createFetch((url) => {
      if (url.includes("/requests/")) {
        return viewModel({ view: "signup" });
      }
      return {
        type: "view",
        viewModel: viewModel({
          view: "signup",
          fieldErrors: { email: "Email is required." },
          error: "Fix the highlighted fields.",
        }),
      };
    });
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri, fetch });
    await flow.resume("https://app.example.com/auth/authorize?request=req_1");
    await flow.signup({
      displayName: "Ada",
      email: "ada@example.com",
      password: "secret",
      organizationName: "Acme",
    });
    expect(flow.fieldErrors.email).toBe("Email is required.");
    expect(flow.error).toBe("Fix the highlighted fields.");
  });

  it("maps HTTP error JSON onto the flow without rejecting", async () => {
    const fetch = createFetch(() =>
      jsonResponse({ error: "invalid_request", message: "The authorization request expired." }, 400),
    );
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri, fetch });
    await expect(flow.resume("https://app.example.com/auth/authorize?request=expired")).resolves.toBe("error");
    expect(flow.status).toBe("error");
    expect(flow.error).toBe("The authorization request expired.");
  });

  it("resolves a 400 with fieldErrors into status error", async () => {
    const fetch = createFetch((url) => {
      if (url.includes("/requests/")) {
        return viewModel({ view: "password", email: "ada@example.com" });
      }
      return jsonResponse(
        {
          error: "validation_failed",
          message: "Fix the highlighted fields.",
          fieldErrors: { password: "Password is incorrect." },
        },
        400,
      );
    });
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri, fetch });
    await flow.resume("https://app.example.com/auth/authorize?request=req_1");
    await expect(flow.password.login({ password: "wrong" })).resolves.toBe("error");
    expect(flow.status).toBe("error");
    expect(flow.error).toBe("Fix the highlighted fields.");
    expect(flow.fieldErrors.password).toBe("Password is incorrect.");
  });

  it("rejects a concurrent action", async () => {
    let release: (() => void) | undefined;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const fetch = createFetch(async (url) => {
      if (url.includes("/requests/")) {
        return viewModel();
      }
      await gate;
      return { type: "view", viewModel: viewModel({ view: "password" }) };
    });
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri, fetch });
    await flow.resume("https://app.example.com/auth/authorize?request=req_1");
    const first = flow.identify({ email: "ada@example.com" });
    await expect(flow.identify({ email: "ada@example.com" })).rejects.toBeInstanceOf(HeadlessFlowBusyError);
    release?.();
    await first;
  });

  it("binds invitation tokens on identify", async () => {
    const fetch = createFetch((url, init) => {
      if (url.includes("/requests/")) {
        return viewModel({ view: "invite" });
      }
      expect(readBody(init)).toMatchObject({
        requestId: "req_1",
        email: "invited@example.com",
        invitationToken: "invite_1",
      });
      return { type: "view", viewModel: viewModel({ view: "password", email: "invited@example.com" }) };
    });
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri, fetch });
    await flow.resume(
      "https://app.example.com/auth/authorize?request=req_1&invitationToken=invite_1&email=invited@example.com",
    );
    await flow.identify({ email: "invited@example.com" });
    expect(flow.viewModel?.view).toBe("password");
  });

  it("does not treat a provider redirect as an authorization code", async () => {
    const fetch = createFetch((url) => {
      if (url.includes("/requests/")) {
        return viewModel({
          providers: [{ connectionId: "github", providerType: "oidc", displayName: "GitHub" }],
        });
      }
      return {
        type: "redirect",
        redirectUrl: "https://github.com/login/oauth/authorize?client_id=abc",
      };
    });
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri, fetch });
    await flow.resume("https://app.example.com/auth/authorize?request=req_1");
    await flow.provider.start({ connectionId: "github" });
    expect(flow.status).toBe("redirect");
    expect(flow.authorization).toBeNull();
    expect(flow.redirectUrl).toBe("https://github.com/login/oauth/authorize?client_id=abc");
  });

  it("fails closed when the host moved the headless API", async () => {
    const fetch = createFetch(() => viewModel({ headlessApiBasePath: "/moved/headless" }));
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri, fetch });
    await expect(flow.resume("https://app.example.com/auth/authorize?request=req_1")).rejects.toBeInstanceOf(
      HeadlessApiPathMismatchError,
    );
  });

  it("rejects actions before a request is loaded", async () => {
    const flow = createHeadlessFlow({
      issuer,
      clientId,
      redirectUri,
      fetch: createFetch(() => {
        throw new Error("should not fetch");
      }),
    });
    await expect(flow.password.login({ password: "secret" })).rejects.toBeInstanceOf(HeadlessFlowNotLoadedError);
  });

  it("returns native authorization with the PKCE verifier after start", async () => {
    const fetch = createFetch((url) => {
      if (url.endsWith("/start")) {
        return { type: "view", viewModel: viewModel() };
      }
      return {
        type: "redirect",
        redirectUrl: "sqlos-expo://auth-callback?code=native-code&state=from-server",
      };
    });
    const flow = createHeadlessFlow({
      issuer,
      clientId,
      redirectUri: "sqlos-expo://auth-callback",
      fetch,
    });
    await flow.start({ scope: "openid" });
    await flow.identify({ email: "ada@example.com" });
    expect(flow.authorization?.code).toBe("native-code");
    expect(flow.authorization?.codeVerifier).toEqual(expect.any(String));
    expect(flow.authorization?.redirectUri).toBe("sqlos-expo://auth-callback");
  });

  it("selects an organization without the caller sending pendingToken", async () => {
    const fetch = createFetch((url, init) => {
      if (url.includes("/requests/")) {
        return viewModel();
      }
      if (url.endsWith("/identify")) {
        return { type: "view", viewModel: viewModel({ view: "password", email: "ada@example.com" }) };
      }
      if (url.endsWith("/password/login")) {
        return {
          type: "view",
          viewModel: viewModel({
            view: "organization",
            pendingToken: "pending_secret",
            organizationSelection: [{ id: "org_1", slug: "acme", name: "Acme", role: "admin" }],
          }),
        };
      }
      expect(url.endsWith("/organization/select")).toBe(true);
      expect(readBody(init)).toEqual({
        pendingToken: "pending_secret",
        organizationId: "org_1",
      });
      return {
        type: "redirect",
        redirectUrl: "https://app.example.com/auth/callback?code=org-code",
      };
    });
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri, fetch });
    await flow.resume("https://app.example.com/auth/authorize?request=req_1");
    await flow.identify({ email: "ada@example.com" });
    await flow.password.login({ password: "secret" });
    await flow.organization.select({ organizationId: "org_1" });
    expect(flow.status).toBe("redirect");
    expect(flow.authorization?.code).toBe("org-code");
  });

  it("never writes to localStorage", async () => {
    const setItem = vi.fn();
    vi.stubGlobal("localStorage", { setItem, getItem: vi.fn(), removeItem: vi.fn() });
    const fetch = createFetch(() => viewModel());
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri, fetch });
    await flow.resume("https://app.example.com/auth/authorize?request=req_1");
    expect(setItem).not.toHaveBeenCalled();
    vi.unstubAllGlobals();
  });
});

describe("review follow-ups", () => {
  it("generates an RFC 7636 verifier of at least 43 characters by default", async () => {
    const { generatePkce } = await import("../src/index.js");
    const pkce = await generatePkce();
    expect(pkce.codeVerifier.length).toBeGreaterThanOrEqual(43);
    expect(pkce.codeVerifier.length).toBeLessThanOrEqual(128);
    expect(pkce.codeVerifier).toMatch(/^[A-Za-z0-9\-._~]+$/);
    expect(pkce.codeChallenge).toHaveLength(43);
    expect(pkce.codeChallengeMethod).toBe("S256");
  });

  it("accepts injected crypto primitives instead of a forked generator", async () => {
    const { createPkceGenerator, generatePkce } = await import("../src/index.js");
    const { createHash } = await import("node:crypto");
    const randomBytes = vi.fn(async (size: number) => new Uint8Array(size).fill(7));
    const sha256 = vi.fn(async (data: Uint8Array) => new Uint8Array(createHash("sha256").update(data).digest()));
    const injected = await generatePkce({ randomBytes, sha256 });
    const reference = await generatePkce();
    expect(randomBytes).toHaveBeenCalledWith(32);
    expect(sha256).toHaveBeenCalledOnce();
    expect(injected.codeVerifier).toHaveLength(reference.codeVerifier.length);
    // Same verifier through Web Crypto must yield the same challenge.
    const viaWebCrypto = await generatePkce({ randomBytes });
    expect(viaWebCrypto.codeChallenge).toBe(injected.codeChallenge);

    const bound = createPkceGenerator({ randomBytes, sha256 });
    expect((await bound()).codeVerifier).toBe(injected.codeVerifier);
  });

  it("keeps a registered redirect URI query string in authorization.redirectUri", async () => {
    const registered = "https://app.example.com/callback?tenant=acme";
    const fetch = createFetch((url) => {
      if (url.includes("/requests/")) {
        return viewModel({ view: "password", email: "ada@example.com" });
      }
      return {
        type: "redirect",
        redirectUrl: `${registered}&code=abc&state=xyz&scope=openid`,
      };
    });
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri: registered, fetch });
    await flow.resume("https://app.example.com/auth/authorize?request=req_1");
    await flow.password.login({ password: "secret" });
    expect(flow.authorization).toEqual({
      code: "abc",
      redirectUri: registered,
      state: "xyz",
      codeVerifier: null,
    });
  });

  it("strips only authorization-response params when the redirect does not match the configured URI", async () => {
    const fetch = createFetch((url) => {
      if (url.includes("/requests/")) {
        return viewModel({ view: "password", email: "ada@example.com" });
      }
      return {
        type: "redirect",
        redirectUrl: "https://other.example.com/cb?tenant=acme&code=abc&state=xyz&scope=openid&iss=https%3A%2F%2Fid",
      };
    });
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri, fetch });
    await flow.resume("https://app.example.com/auth/authorize?request=req_1");
    await flow.password.login({ password: "secret" });
    expect(flow.authorization?.redirectUri).toBe("https://other.example.com/cb?tenant=acme");
  });

  it("joins an identical in-flight resume instead of rejecting (StrictMode double effect)", async () => {
    let release: (() => void) | undefined;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const fetch = createFetch(async () => {
      await gate;
      return viewModel();
    });
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri, fetch });
    const location = "https://app.example.com/auth/authorize?request=req_1";
    const first = flow.resume(location);
    const second = flow.resume(location);
    expect(second).toBe(first);
    release?.();
    await expect(second).resolves.toBe("view");
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it("still rejects a different action while a load is in flight", async () => {
    let release: (() => void) | undefined;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const fetch = createFetch(async () => {
      await gate;
      return viewModel();
    });
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri, fetch });
    const pending = flow.resume("https://app.example.com/auth/authorize?request=req_1");
    await expect(flow.identify({ email: "ada@example.com" })).rejects.toBeInstanceOf(HeadlessFlowBusyError);
    release?.();
    await pending;
  });

  it("exposes the password reset result instead of discarding it", async () => {
    const fetch = createFetch((url, init) => {
      if (url.includes("/requests/")) {
        return viewModel({ view: "forgot-password", email: "ada@example.com" });
      }
      expect(url.endsWith("/password/forgot")).toBe(true);
      expect(readBody(init)).toEqual({ email: "ada@example.com", requestId: "req_1" });
      return {
        email: "ada@example.com",
        maskedEmail: "a***@example.com",
        message: "Check your inbox.",
        expiresAt: "2030-01-01T00:15:00Z",
        nextAllowedSendAt: "2030-01-01T00:01:00Z",
      };
    });
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri, fetch });
    await flow.resume("https://app.example.com/auth/authorize?request=req_1");
    await expect(flow.password.forgot()).resolves.toBe("view");
    expect(flow.viewModel?.view).toBe("forgot-password-sent");
    expect(flow.viewModel?.info).toBe("Check your inbox.");
    expect(flow.passwordReset).toEqual({
      email: "ada@example.com",
      maskedEmail: "a***@example.com",
      message: "Check your inbox.",
      expiresAt: "2030-01-01T00:15:00Z",
      nextAllowedSendAt: "2030-01-01T00:01:00Z",
    });
  });

  it("submit() drives an unnamed route through the same bookkeeping", async () => {
    const fetch = createFetch((url, init) => {
      if (url.includes("/requests/")) {
        return viewModel();
      }
      if (url.endsWith("/passkey/start")) {
        expect(readBody(init)).toEqual({ requestId: "req_1", credentialId: "pk_1" });
        return { type: "view", viewModel: viewModel({ view: "mfa", mfaToken: "mfa_from_passkey" }) };
      }
      if (url.endsWith("/mfa/verify")) {
        expect(readBody(init)).toMatchObject({ mfaToken: "mfa_from_passkey" });
        return { type: "redirect", redirectUrl: `${redirectUri}?code=ok` };
      }
      throw new Error(`unexpected ${url}`);
    });
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri, fetch });
    await flow.resume("https://app.example.com/auth/authorize?request=req_1");
    await expect(flow.submit("/passkey/start", { credentialId: "pk_1" })).resolves.toBe("view");
    expect(flow.viewModel?.view).toBe("mfa");
    // The token the unnamed route returned feeds the typed action that follows.
    await flow.mfa.verify({ code: "123456" });
    expect(flow.authorization?.code).toBe("ok");
  });

  it("submit() accepts a bare view model and a 204", async () => {
    const fetch = createFetch((url) => {
      if (url.includes("/requests/")) {
        return viewModel();
      }
      if (url.endsWith("/custom/view")) {
        return viewModel({ view: "organization" });
      }
      return new Response(null, { status: 204 });
    });
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri, fetch });
    await flow.resume("https://app.example.com/auth/authorize?request=req_1");
    await flow.submit("/custom/view");
    expect(flow.viewModel?.view).toBe("organization");
    await expect(flow.submit("/custom/noop", {})).resolves.toBe("view");
  });

  it("refuses the token endpoint with a dedicated error class", async () => {
    const { HeadlessTokenEndpointError, HeadlessProgrammerError } = await import("../src/index.js");
    const fetch = createFetch(() => viewModel());
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri, fetch });
    await flow.resume("https://app.example.com/auth/authorize?request=req_1");
    const rejection = flow.submit("https://id.example.com/sqlos/auth/token", { grant_type: "authorization_code" });
    await expect(rejection).rejects.toBeInstanceOf(HeadlessTokenEndpointError);
    await expect(rejection).rejects.toBeInstanceOf(HeadlessProgrammerError);
    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it("skips the path check when the view model omits headlessApiBasePath", async () => {
    const fetch = createFetch(() => ({ ...viewModel(), headlessApiBasePath: "" }));
    const flow = createHeadlessFlow({ issuer, clientId, redirectUri, fetch });
    await expect(flow.resume("https://app.example.com/auth/authorize?request=req_1")).resolves.toBe("view");
  });

  it("credentialEnabled applies the runtime flag and the enabled list", async () => {
    const { credentialEnabled } = await import("../src/index.js");
    const settings = {
      enabledCredentialTypes: ["password", "EMAIL_OTP"],
      localPasswordRuntimeEnabled: true,
      emailOtpRuntimeConfigured: false,
      magicLinkRuntimeConfigured: true,
    };
    expect(credentialEnabled(settings, "password")).toBe(true);
    expect(credentialEnabled(settings, "email_otp")).toBe(false);
    expect(credentialEnabled(settings, "magic_link")).toBe(false);
    expect(credentialEnabled(null, "password")).toBe(false);
  });
});
