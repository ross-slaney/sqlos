import { HeadlessError } from "./errors.js";

export type HeadlessHttpOptions = {
  fetch?: typeof fetch;
  credentials?: RequestCredentials;
};

const TOKEN_PATH_PATTERN = /\/token(?:\?|$)/i;

export function assertNotTokenUrl(url: string): void {
  let pathname = url;
  try {
    pathname = new URL(url).pathname;
  } catch {
    /* keep raw */
  }
  if (TOKEN_PATH_PATTERN.test(pathname) || TOKEN_PATH_PATTERN.test(url)) {
    // Message must stay stable — flow.ts rethrows this as a programmer error.
    throw new HeadlessError("The headless package never calls /token.");
  }
}

export function createHeadlessHttp(options: HeadlessHttpOptions) {
  const fetchImpl = options.fetch ?? globalThis.fetch;
  if (typeof fetchImpl !== "function") {
    throw new HeadlessError("fetch is not available.");
  }

  return async function request<T>(
    url: string,
    init?: RequestInit & { parse?: "json" | "void" },
  ): Promise<T> {
    assertNotTokenUrl(url);

    const response = await fetchImpl(url, {
      ...init,
      credentials: options.credentials ?? init?.credentials,
      cache: "no-store",
      headers: {
        Accept: "application/json",
        ...(init?.body ? { "Content-Type": "application/json" } : {}),
        ...init?.headers,
      },
    });

    if (response.status === 204 || init?.parse === "void") {
      if (!response.ok) {
        throw await readError(response);
      }
      return undefined as T;
    }

    const text = await response.text();
    if (!response.ok) {
      throw parseError(text, response.status);
    }
    if (!text) {
      return undefined as T;
    }
    try {
      return JSON.parse(text) as T;
    } catch (cause) {
      throw new HeadlessError("SqlOS returned a non-JSON headless response.", {
        status: response.status,
        cause,
      });
    }
  };
}

async function readError(response: Response): Promise<HeadlessError> {
  const text = await response.text();
  return parseError(text, response.status);
}

function parseError(text: string, status: number): HeadlessError {
  if (!text) {
    return new HeadlessError(`Headless API error: ${status}`, { status });
  }
  try {
    const payload = JSON.parse(text) as {
      error?: string;
      message?: string;
      fieldErrors?: Record<string, string>;
    };
    return new HeadlessError(payload.message || payload.error || text, {
      status,
      code: payload.error,
      fieldErrors: payload.fieldErrors,
    });
  } catch {
    return new HeadlessError(text, { status });
  }
}

export function joinUrl(base: string, path: string): string {
  if (/^https?:\/\//i.test(path)) {
    return path;
  }
  const trimmedBase = base.replace(/\/+$/, "");
  const trimmedPath = path.startsWith("/") ? path : `/${path}`;
  return `${trimmedBase}${trimmedPath}`;
}

export function pathnameOf(url: string): string {
  try {
    return new URL(url).pathname.replace(/\/+$/, "") || "/";
  } catch {
    return url.replace(/\/+$/, "") || "/";
  }
}
