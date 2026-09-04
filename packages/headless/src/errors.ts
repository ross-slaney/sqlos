export class HeadlessError extends Error {
  readonly status?: number;
  readonly code?: string;
  readonly fieldErrors: Record<string, string>;

  constructor(
    message: string,
    options?: { status?: number; code?: string; fieldErrors?: Record<string, string>; cause?: unknown },
  ) {
    super(message, options?.cause ? { cause: options.cause } : undefined);
    this.name = "HeadlessError";
    this.status = options?.status;
    this.code = options?.code;
    this.fieldErrors = options?.fieldErrors ?? {};
  }
}

/**
 * Programmer errors. These rethrow from actions instead of resolving to
 * `status === "error"`, because the fix is in the integration, not the UI.
 */
export class HeadlessProgrammerError extends HeadlessError {
  constructor(message: string) {
    super(message);
    this.name = "HeadlessProgrammerError";
  }
}

export class HeadlessFlowBusyError extends HeadlessProgrammerError {
  constructor() {
    super("A headless action is already in progress. Disable inputs while status is \"loading\".");
    this.name = "HeadlessFlowBusyError";
  }
}

export class HeadlessFlowNotLoadedError extends HeadlessProgrammerError {
  constructor(message = "No headless authorization request is loaded.") {
    super(message);
    this.name = "HeadlessFlowNotLoadedError";
  }
}

export class HeadlessApiPathMismatchError extends HeadlessProgrammerError {
  constructor(configured: string, actual: string) {
    super(
      `The configured headless API path (${configured}) does not match the server (${actual}).`,
    );
    this.name = "HeadlessApiPathMismatchError";
  }
}

/** Thrown when any request would target the OAuth token endpoint. */
export class HeadlessTokenEndpointError extends HeadlessProgrammerError {
  constructor(url: string) {
    super(`The headless package never calls /token (${url}). Hand the authorization code to your OIDC library.`);
    this.name = "HeadlessTokenEndpointError";
  }
}
