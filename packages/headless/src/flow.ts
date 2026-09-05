import {
  HeadlessApiPathMismatchError,
  HeadlessError,
  HeadlessFlowBusyError,
  HeadlessFlowNotLoadedError,
  HeadlessProgrammerError,
} from "./errors.js";
import { createHeadlessHttp, joinUrl, pathnameOf, type HeadlessRequest } from "./http.js";
import { generatePkce as defaultGeneratePkce, randomState } from "./pkce.js";
import type { HeadlessView } from "./contract.js";
import type {
  CreateHeadlessFlowOptions,
  HeadlessActionResult,
  HeadlessAuthorization,
  HeadlessFlow,
  HeadlessFlowStatus,
  HeadlessIdentifyInput,
  HeadlessPasswordLoginInput,
  HeadlessPasswordResetRequestResult,
  HeadlessPkcePair,
  HeadlessSignupInput,
  HeadlessStartInput,
  HeadlessSubmitOptions,
  HeadlessViewModel,
  JsonObject,
  LocationLike,
} from "./types.js";

type TokenBag = {
  requestId: string | null;
  challengeToken: string | null;
  signupToken: string | null;
  pendingToken: string | null;
  mfaToken: string | null;
  consentToken: string | null;
  enrollmentToken: string | null;
  invitationToken: string | null;
};

function emptyTokens(): TokenBag {
  return {
    requestId: null,
    challengeToken: null,
    signupToken: null,
    pendingToken: null,
    mfaToken: null,
    consentToken: null,
    enrollmentToken: null,
    invitationToken: null,
  };
}

function normalizeViewModel(raw: HeadlessViewModel | null | undefined): HeadlessViewModel | null {
  if (!raw) {
    return null;
  }
  return {
    ...raw,
    fieldErrors: raw.fieldErrors ?? {},
    organizationSelection: raw.organizationSelection ?? [],
    providers: raw.providers ?? [],
  };
}

function searchParamsFrom(location: LocationLike | string): URLSearchParams {
  if (typeof location === "string") {
    try {
      return new URL(location, "https://sqlos.invalid").searchParams;
    } catch {
      return new URLSearchParams(location.startsWith("?") ? location : `?${location}`);
    }
  }
  if (typeof location.search === "string") {
    return new URLSearchParams(location.search);
  }
  if (typeof location.href === "string") {
    return new URL(location.href, "https://sqlos.invalid").searchParams;
  }
  if (typeof location.toString === "function") {
    return searchParamsFrom(location.toString());
  }
  return new URLSearchParams();
}

/** Parameters SqlOS appends to the registered redirect URI when issuing a code. */
const AUTHORIZATION_RESPONSE_PARAMS = ["code", "state", "scope", "iss", "session_state"];

/**
 * Recover the exact registered redirect URI from the code redirect. Registered
 * URIs may carry a query string (`/token` matches by ordinal equality), so the
 * configured value wins when the redirect was built from it; otherwise only
 * the authorization-response parameters are stripped.
 */
function registeredRedirectUri(url: URL, redirectUrl: string, configuredRedirectUri: string): string {
  const separator = configuredRedirectUri.includes("?") ? "&" : "?";
  if (redirectUrl === configuredRedirectUri || redirectUrl.startsWith(`${configuredRedirectUri}${separator}`)) {
    return configuredRedirectUri;
  }
  const stripped = new URL(url.toString());
  for (const name of AUTHORIZATION_RESPONSE_PARAMS) {
    stripped.searchParams.delete(name);
  }
  if ([...stripped.searchParams.keys()].length === 0) {
    stripped.search = "";
  }
  stripped.hash = "";
  return stripped.toString();
}

function parseAuthorization(
  redirectUrl: string,
  configuredRedirectUri: string,
  state: string | null,
  codeVerifier: string | null,
): HeadlessAuthorization | null {
  try {
    const url = new URL(redirectUrl);
    const code = url.searchParams.get("code");
    if (!code) {
      return null;
    }
    return {
      code,
      redirectUri: registeredRedirectUri(url, redirectUrl, configuredRedirectUri),
      state: url.searchParams.get("state") ?? state,
      codeVerifier,
    };
  } catch {
    const match = /[?&]code=([^&#]+)/.exec(redirectUrl);
    if (!match?.[1]) {
      return null;
    }
    return {
      code: decodeURIComponent(match[1]),
      redirectUri: configuredRedirectUri,
      state,
      codeVerifier,
    };
  }
}

function isActionResult(value: unknown): value is HeadlessActionResult {
  return typeof value === "object" && value !== null && typeof (value as HeadlessActionResult).type === "string";
}

function isViewModel(value: unknown): value is HeadlessViewModel {
  return typeof value === "object" && value !== null && typeof (value as HeadlessViewModel).view === "string";
}

class HeadlessFlowImpl implements HeadlessFlow {
  status: HeadlessFlowStatus = "idle";
  viewModel: HeadlessViewModel | null = null;
  error: string | null = null;
  fieldErrors: Record<string, string> = {};
  authorization: HeadlessAuthorization | null = null;
  redirectUrl: string | null = null;
  passwordReset: HeadlessPasswordResetRequestResult | null = null;

  private readonly listeners = new Set<() => void>();
  private readonly request: HeadlessRequest;
  private readonly generatePkce: () => Promise<HeadlessPkcePair>;
  private readonly clientId: string;
  private readonly redirectUri: string;
  private readonly headlessBase: string;
  private readonly configuredHeadlessPath: string;
  private tokens = emptyTokens();
  private email: string | null = null;
  private phoneNumber: string | null = null;
  private codeVerifier: string | null = null;
  private oauthState: string | null = null;
  private inflight: { key: string | null; promise: Promise<HeadlessFlowStatus> } | null = null;
  private lastTotpEnrollment: HeadlessViewModel["totpEnrollment"] = null;

  constructor(options: CreateHeadlessFlowOptions) {
    const issuer = options.issuer.replace(/\/+$/, "");
    this.clientId = options.clientId;
    this.redirectUri = options.redirectUri;
    this.headlessBase = options.headlessApiBasePath
      ? /^https?:\/\//i.test(options.headlessApiBasePath)
        ? options.headlessApiBasePath.replace(/\/+$/, "")
        : joinUrl(new URL(issuer).origin, options.headlessApiBasePath)
      : `${issuer}/headless`;
    this.configuredHeadlessPath = pathnameOf(this.headlessBase);
    this.generatePkce = options.generatePkce ?? (() => defaultGeneratePkce());
    this.request = createHeadlessHttp({
      fetch: options.fetch,
      credentials: options.credentials,
    });
  }

  subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  resume(location: LocationLike | string): Promise<HeadlessFlowStatus> {
    const params = searchParamsFrom(location);
    const requestId = params.get("request")?.trim() || null;
    return this.run(async () => {
      if (!requestId) {
        throw new HeadlessFlowNotLoadedError("The authorization request ID is missing.");
      }

      this.tokens.invitationToken = params.get("invitationToken") || this.tokens.invitationToken;
      this.tokens.pendingToken = params.get("pendingToken") || this.tokens.pendingToken;
      this.tokens.mfaToken = params.get("mfaToken") || this.tokens.mfaToken;
      this.email = params.get("email") || this.email;

      const url = new URL(joinUrl(this.headlessBase, `/requests/${encodeURIComponent(requestId)}`));
      for (const name of ["view", "error", "pendingToken", "email", "displayName", "mfaToken"]) {
        const value = params.get(name);
        if (value) {
          url.searchParams.set(name, value);
        }
      }

      const model = normalizeViewModel(await this.request<HeadlessViewModel>(url.toString()));
      this.applyViewModel(model);
    }, `resume:${requestId ?? ""}`);
  }

  start(input: HeadlessStartInput = {}): Promise<HeadlessFlowStatus> {
    return this.run(async () => {
      const pkce = input.codeVerifier && input.codeChallenge
        ? {
            codeVerifier: input.codeVerifier,
            codeChallenge: input.codeChallenge,
            codeChallengeMethod: input.codeChallengeMethod ?? "S256",
          }
        : await this.generatePkce();
      this.codeVerifier = pkce.codeVerifier;
      this.oauthState = input.state ?? randomState();
      if (input.invitationToken) {
        this.tokens.invitationToken = input.invitationToken;
      }
      if (input.loginHint) {
        this.email = input.loginHint;
      }

      const result = await this.post<HeadlessActionResult>("/start", {
        responseType: input.responseType ?? "code",
        clientId: this.clientId,
        redirectUri: this.redirectUri,
        state: this.oauthState,
        scope: input.scope,
        codeChallenge: pkce.codeChallenge,
        codeChallengeMethod: pkce.codeChallengeMethod,
        resource: input.resource,
        loginHint: input.loginHint,
        prompt: input.prompt,
        nonce: input.nonce,
        view: input.view,
        uiContext: input.uiContext,
        invitationToken: input.invitationToken ?? this.tokens.invitationToken,
        maxAge: input.maxAge,
      });
      this.applyActionResult(result);
    }, `start:${JSON.stringify(input)}`);
  }

  async submit(path: string, body: JsonObject = {}, options: HeadlessSubmitOptions = {}): Promise<HeadlessFlowStatus> {
    return this.run(async () => {
      const requestId = this.tokens.requestId ?? this.viewModel?.requestId ?? undefined;
      const result = await this.post<unknown>(path, { requestId, ...body });
      const parse = options.parse ?? "auto";
      if (parse === "view" || (parse === "auto" && isViewModel(result))) {
        this.applyViewModel(normalizeViewModel(result as HeadlessViewModel));
        return;
      }
      if (parse === "action" || isActionResult(result)) {
        this.applyActionResult(result as HeadlessActionResult);
        return;
      }
      if (result === undefined) {
        // 204 / empty body: the server accepted the action without changing the view.
        this.status = this.viewModel ? "view" : "idle";
        this.error = null;
        this.fieldErrors = {};
        return;
      }
      throw new HeadlessError(`SqlOS returned an unrecognized response from ${path}.`);
    });
  }

  async identify(input: HeadlessIdentifyInput): Promise<HeadlessFlowStatus> {
    return this.run(async () => {
      this.email = input.email;
      this.applyActionResult(
        await this.post("/identify", {
          requestId: this.requireRequestId(),
          email: input.email,
          invitationToken: input.invitationToken ?? this.tokens.invitationToken,
        }),
      );
    });
  }

  readonly password = {
    login: async (input: HeadlessPasswordLoginInput): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        const email = this.requireEmail(input.email, "password login");
        this.applyActionResult(
          await this.post("/password/login", {
            requestId: this.requireRequestId(),
            email,
            password: input.password,
            invitationToken: input.invitationToken ?? this.tokens.invitationToken,
          }),
        );
      });
    },
    forgot: async (input?: { email?: string }): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        const email = this.requireEmail(input?.email, "a password reset");
        const result = await this.post<HeadlessPasswordResetRequestResult>("/password/forgot", {
          email,
          requestId: this.tokens.requestId,
        });
        this.status = "view";
        this.error = null;
        this.fieldErrors = {};
        this.passwordReset = result;
        this.viewModel = {
          ...(this.viewModel ?? emptyView("forgot-password-sent", this.configuredHeadlessPath)),
          view: "forgot-password-sent",
          email,
          info: result.message,
          error: null,
          fieldErrors: {},
        };
      });
    },
    reset: async (input: { token: string; newPassword: string }): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        await this.request(joinUrl(this.headlessBase, "/password/reset"), {
          method: "POST",
          body: JSON.stringify({ token: input.token, newPassword: input.newPassword }),
          parse: "void",
        });
        this.status = "view";
        this.error = null;
        this.fieldErrors = {};
      });
    },
  };

  readonly emailOtp = {
    start: async (input?: { email?: string; invitationToken?: string }): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        const email = this.requireEmail(input?.email, "email OTP");
        this.applyActionResult(
          await this.post("/email-otp/start", {
            requestId: this.requireRequestId(),
            email,
            invitationToken: input?.invitationToken ?? this.tokens.invitationToken,
          }),
        );
      });
    },
    verify: async (input: { code: string; invitationToken?: string }): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        this.applyActionResult(
          await this.post("/email-otp/verify", {
            requestId: this.requireRequestId(),
            challengeToken: this.requireToken("challengeToken"),
            code: input.code,
            invitationToken: input.invitationToken ?? this.tokens.invitationToken,
          }),
        );
      });
    },
    signupStart: async (input: {
      displayName: string;
      email?: string;
      organizationName?: string;
      customFields?: JsonObject;
      invitationToken?: string;
    }): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        const email = this.requireEmail(input.email, "email OTP signup");
        this.applyActionResult(
          await this.post("/signup/email-otp/start", {
            requestId: this.requireRequestId(),
            displayName: input.displayName,
            email,
            organizationName: input.organizationName,
            customFields: input.customFields ?? {},
            invitationToken: input.invitationToken ?? this.tokens.invitationToken,
          }),
        );
      });
    },
    signupVerify: async (input: { code: string; invitationToken?: string }): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        this.applyActionResult(
          await this.post("/signup/email-otp/verify", {
            requestId: this.requireRequestId(),
            signupToken: this.requireToken("signupToken"),
            challengeToken: this.requireToken("challengeToken"),
            code: input.code,
            invitationToken: input.invitationToken ?? this.tokens.invitationToken,
          }),
        );
      });
    },
  };

  readonly magicLink = {
    start: async (input?: { email?: string; invitationToken?: string }): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        const email = this.requireEmail(input?.email, "a magic link");
        this.applyActionResult(
          await this.post("/magic-link/start", {
            requestId: this.requireRequestId(),
            email,
            invitationToken: input?.invitationToken ?? this.tokens.invitationToken,
          }),
        );
      });
    },
    complete: async (input: { token: string; invitationToken?: string }): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        this.applyActionResult(
          await this.post("/magic-link/complete", {
            token: input.token,
            requestId: this.tokens.requestId,
            invitationToken: input.invitationToken ?? this.tokens.invitationToken,
          }),
        );
      });
    },
  };

  readonly phoneOtp = {
    start: async (input: { phoneNumber: string; invitationToken?: string }): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        this.phoneNumber = input.phoneNumber;
        this.applyActionResult(
          await this.post("/phone-otp/start", {
            requestId: this.requireRequestId(),
            phoneNumber: input.phoneNumber,
            invitationToken: input.invitationToken ?? this.tokens.invitationToken,
          }),
        );
      });
    },
    verify: async (input: { code: string; invitationToken?: string }): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        this.applyActionResult(
          await this.post("/phone-otp/verify", {
            requestId: this.requireRequestId(),
            challengeToken: this.requireToken("challengeToken"),
            code: input.code,
            invitationToken: input.invitationToken ?? this.tokens.invitationToken,
          }),
        );
      });
    },
    signupStart: async (input: {
      displayName: string;
      phoneNumber: string;
      organizationName?: string;
      customFields?: JsonObject;
      invitationToken?: string;
    }): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        this.phoneNumber = input.phoneNumber;
        this.applyActionResult(
          await this.post("/signup/phone-otp/start", {
            requestId: this.requireRequestId(),
            displayName: input.displayName,
            phoneNumber: input.phoneNumber,
            organizationName: input.organizationName,
            customFields: input.customFields ?? {},
            invitationToken: input.invitationToken ?? this.tokens.invitationToken,
          }),
        );
      });
    },
    signupVerify: async (input: { code: string; invitationToken?: string }): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        this.applyActionResult(
          await this.post("/signup/phone-otp/verify", {
            requestId: this.requireRequestId(),
            signupToken: this.requireToken("signupToken"),
            challengeToken: this.requireToken("challengeToken"),
            code: input.code,
            invitationToken: input.invitationToken ?? this.tokens.invitationToken,
          }),
        );
      });
    },
  };

  async signup(input: HeadlessSignupInput): Promise<HeadlessFlowStatus> {
    return this.run(async () => {
      const email = this.requireEmail(input.email, "signup");
      this.applyActionResult(
        await this.post("/signup", {
          requestId: this.requireRequestId(),
          displayName: input.displayName,
          email,
          password: input.password,
          organizationName: input.organizationName,
          customFields: input.customFields ?? {},
          invitationToken: input.invitationToken ?? this.tokens.invitationToken,
        }),
      );
    });
  }

  readonly organization = {
    select: async (input: { organizationId: string }): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        this.applyActionResult(
          await this.post("/organization/select", {
            pendingToken: this.requireToken("pendingToken"),
            organizationId: input.organizationId,
          }),
        );
      });
    },
  };

  readonly mfa = {
    verify: async (input: { code: string }): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        this.applyActionResult(
          await this.post("/mfa/verify", {
            requestId: this.requireRequestId(),
            mfaToken: this.requireToken("mfaToken"),
            code: input.code,
          }),
        );
      });
    },
    totp: {
      enrollStart: async (input?: { displayName?: string }): Promise<HeadlessFlowStatus> => {
        return this.run(async () => {
          this.applyActionResult(
            await this.post("/mfa/totp/enroll/start", {
              requestId: this.requireRequestId(),
              mfaToken: this.requireToken("mfaToken"),
              displayName: input?.displayName,
            }),
          );
        });
      },
      enrollVerify: async (input: { code: string }): Promise<HeadlessFlowStatus> => {
        return this.run(async () => {
          this.applyActionResult(
            await this.post("/mfa/totp/enroll/verify", {
              requestId: this.requireRequestId(),
              mfaToken: this.requireToken("mfaToken"),
              enrollmentToken: this.requireToken("enrollmentToken"),
              code: input.code,
            }),
          );
        });
      },
    },
  };

  readonly consent = {
    approve: async (): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        this.applyActionResult(
          await this.post("/consent/approve", {
            requestId: this.requireRequestId(),
            consentToken: this.requireToken("consentToken"),
          }),
        );
      });
    },
    deny: async (): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        this.applyActionResult(
          await this.post("/consent/deny", {
            requestId: this.requireRequestId(),
            consentToken: this.requireToken("consentToken"),
          }),
        );
      });
    },
  };

  readonly invitation = {
    resolve: async (input: { invitationToken: string }): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        this.tokens.invitationToken = input.invitationToken;
        const model = normalizeViewModel(
          await this.post<HeadlessViewModel>("/invitations/resolve", {
            invitationToken: input.invitationToken,
          }),
        );
        this.applyViewModel(model);
      });
    },
    signup: async (input: {
      displayName: string;
      email?: string;
      customFields?: JsonObject;
      invitationToken: string;
    }): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        this.tokens.invitationToken = input.invitationToken;
        const email = this.requireEmail(input.email, "invitation signup");
        this.applyActionResult(
          await this.post("/invitations/signup", {
            requestId: this.requireRequestId(),
            displayName: input.displayName,
            email,
            customFields: input.customFields ?? {},
            invitationToken: input.invitationToken,
          }),
        );
      });
    },
  };

  readonly device = {
    resolve: async (input?: { userCode?: string }): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        const model = normalizeViewModel(
          await this.post<HeadlessViewModel>("/device/resolve", {
            userCode: input?.userCode,
            requestId: this.tokens.requestId,
          }),
        );
        this.applyViewModel(model);
      });
    },
    approve: async (input?: { userCode?: string; organizationId?: string }): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        this.applyActionResult(
          await this.post("/device/approve", {
            userCode: input?.userCode,
            organizationId: input?.organizationId,
            requestId: this.tokens.requestId,
          }),
        );
      });
    },
    deny: async (input?: { userCode?: string }): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        this.applyActionResult(
          await this.post("/device/deny", {
            userCode: input?.userCode,
            requestId: this.tokens.requestId,
          }),
        );
      });
    },
  };

  readonly provider = {
    start: async (input: {
      connectionId: string;
      email?: string;
      invitationToken?: string;
    }): Promise<HeadlessFlowStatus> => {
      return this.run(async () => {
        this.applyActionResult(
          await this.post("/provider/start", {
            requestId: this.requireRequestId(),
            connectionId: input.connectionId,
            email: input.email ?? this.email ?? this.viewModel?.email,
            invitationToken: input.invitationToken ?? this.tokens.invitationToken,
          }),
        );
      });
    },
  };

  private async post<T = HeadlessActionResult>(path: string, body: unknown): Promise<T> {
    return this.request<T>(joinUrl(this.headlessBase, path), {
      method: "POST",
      body: JSON.stringify(body),
    });
  }

  /**
   * Serialize actions. A second user action while one is in flight is a
   * programmer error (disable the form while `status === "loading"`). Loads
   * (`resume`, `start`) pass a key so an identical concurrent call — React
   * StrictMode's double effect, for example — joins the in-flight promise
   * instead of rejecting.
   */
  private run(work: () => Promise<void>, coalesceKey: string | null = null): Promise<HeadlessFlowStatus> {
    if (this.inflight) {
      if (coalesceKey !== null && this.inflight.key === coalesceKey) {
        return this.inflight.promise;
      }
      return Promise.reject(new HeadlessFlowBusyError());
    }
    const promise = this.execute(work);
    this.inflight = { key: coalesceKey, promise };
    return promise;
  }

  private async execute(work: () => Promise<void>): Promise<HeadlessFlowStatus> {
    this.status = "loading";
    this.error = null;
    this.notify();
    try {
      await work();
      if (this.status === "loading") {
        this.status = this.viewModel ? "view" : "idle";
      }
      this.notify();
      return this.status;
    } catch (error) {
      const mapped = error instanceof HeadlessError
        ? error
        : new HeadlessError(error instanceof Error ? error.message : "Headless request failed.", {
            cause: error,
          });
      this.status = "error";
      this.error = mapped.message;
      if (mapped instanceof HeadlessProgrammerError) {
        // Surface it in state for the UI, then rethrow: the fix is in the integration.
        if (Object.keys(mapped.fieldErrors).length > 0) {
          this.fieldErrors = mapped.fieldErrors;
        }
        this.notify();
        throw mapped;
      }
      this.fieldErrors = mapped.fieldErrors;
      this.notify();
      return this.status;
    } finally {
      this.inflight = null;
    }
  }

  private applyActionResult(result: HeadlessActionResult): void {
    if (result.type === "redirect" && result.redirectUrl) {
      this.redirectUrl = result.redirectUrl;
      this.authorization = parseAuthorization(
        result.redirectUrl,
        this.redirectUri,
        this.oauthState,
        this.codeVerifier,
      );
      this.passwordReset = null;
      this.status = "redirect";
      this.error = null;
      this.fieldErrors = {};
      return;
    }

    if (result.type === "view" && result.viewModel) {
      this.applyViewModel(normalizeViewModel(result.viewModel));
      return;
    }

    throw new HeadlessError("SqlOS returned an incomplete headless action result.");
  }

  private applyViewModel(model: HeadlessViewModel | null): void {
    if (!model) {
      throw new HeadlessError("SqlOS returned an empty headless view model.");
    }

    // The server always sends headlessApiBasePath; when it does, fail closed
    // on a mismatch so actions never post to a path the host has moved.
    if (model.headlessApiBasePath) {
      const actualPath = pathnameOf(model.headlessApiBasePath);
      if (actualPath !== this.configuredHeadlessPath) {
        throw new HeadlessApiPathMismatchError(this.configuredHeadlessPath, actualPath);
      }
    }

    const totpEnrollment =
      model.view === "mfa-enroll" && !model.totpEnrollment && this.lastTotpEnrollment
        ? this.lastTotpEnrollment
        : model.totpEnrollment ?? null;
    if (totpEnrollment) {
      this.lastTotpEnrollment = totpEnrollment;
    }

    this.viewModel = { ...model, totpEnrollment };
    this.tokens.requestId = model.requestId ?? this.tokens.requestId;
    this.tokens.challengeToken = model.challengeToken ?? null;
    this.tokens.signupToken = model.signupToken ?? null;
    this.tokens.pendingToken = model.pendingToken ?? this.tokens.pendingToken;
    this.tokens.mfaToken = model.mfaToken ?? this.tokens.mfaToken;
    this.tokens.consentToken = model.consentToken ?? null;
    this.tokens.enrollmentToken = totpEnrollment?.enrollmentToken ?? null;
    this.email = model.email ?? this.email;
    this.phoneNumber = model.phoneNumber ?? this.phoneNumber;
    this.fieldErrors = model.fieldErrors ?? {};
    this.error = model.error ?? null;
    this.redirectUrl = null;
    this.authorization = null;
    this.passwordReset = null;
    this.status = "view";
  }

  private requireRequestId(): string {
    const requestId = this.tokens.requestId ?? this.viewModel?.requestId;
    if (!requestId) {
      throw new HeadlessFlowNotLoadedError();
    }
    return requestId;
  }

  private requireEmail(explicit: string | undefined, purpose: string): string {
    const email = explicit ?? this.email ?? this.viewModel?.email;
    if (!email) {
      throw new HeadlessFlowNotLoadedError(`Email is required for ${purpose}. Pass it or call identify first.`);
    }
    this.email = email;
    return email;
  }

  private requireToken(name: keyof TokenBag): string {
    const value = this.tokens[name];
    if (!value) {
      throw new HeadlessFlowNotLoadedError(`The current view does not include ${name}.`);
    }
    return value;
  }

  private notify(): void {
    for (const listener of this.listeners) {
      listener();
    }
  }
}

function emptyView(view: HeadlessView, headlessApiBasePath: string): HeadlessViewModel {
  return {
    view,
    authBasePath: "",
    headlessApiBasePath,
    fieldErrors: {},
    organizationSelection: [],
    providers: [],
  };
}

export function createHeadlessFlow(options: CreateHeadlessFlowOptions): HeadlessFlow {
  if (!options.issuer?.trim()) {
    throw new HeadlessProgrammerError("issuer is required.");
  }
  if (!options.clientId?.trim()) {
    throw new HeadlessProgrammerError("clientId is required.");
  }
  if (!options.redirectUri?.trim()) {
    throw new HeadlessProgrammerError("redirectUri is required.");
  }
  return new HeadlessFlowImpl(options);
}
