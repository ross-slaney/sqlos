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

export class HeadlessFlowBusyError extends HeadlessError {
  constructor() {
    super("A headless action is already in progress.");
    this.name = "HeadlessFlowBusyError";
  }
}

export class HeadlessFlowNotLoadedError extends HeadlessError {
  constructor(message = "No headless authorization request is loaded.") {
    super(message);
    this.name = "HeadlessFlowNotLoadedError";
  }
}

export class HeadlessApiPathMismatchError extends HeadlessError {
  constructor(configured: string, actual: string) {
    super(
      `The configured headless API path (${configured}) does not match the server (${actual}).`,
    );
    this.name = "HeadlessApiPathMismatchError";
  }
}
