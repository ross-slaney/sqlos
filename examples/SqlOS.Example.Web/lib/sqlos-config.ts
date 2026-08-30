export function getExampleApiUrl(): string {
  const configured = (process.env.NEXT_PUBLIC_API_URL ?? "").trim().replace(/\/$/, "");
  return configured || "http://localhost:5062";
}

export function getExampleAuthServerUrl(): string {
  return `${getExampleApiUrl()}/sqlos/auth`;
}

export function getExampleClientId(): string {
  const configured = (process.env.NEXT_PUBLIC_SQL_OS_CLIENT_ID ?? "").trim();
  return configured || "example-web";
}

export function normalizeNextPath(nextPath: string | null | undefined): string {
  if (!nextPath) {
    return "/retail";
  }

  const trimmed = nextPath.trim();
  if (!trimmed.startsWith("/") || trimmed.startsWith("//")) {
    return "/retail";
  }

  return trimmed;
}
