import type { AuthOptions, User } from "next-auth";
import CredentialsProvider from "next-auth/providers/credentials";
import type { JWT } from "next-auth/jwt";
import { jwtDecode } from "jwt-decode";

interface DecodedToken {
  exp: number;
  iss?: string;
  sub?: string;
  email?: string;
  name?: string;
  org_id?: string;
  sid?: string;
}

type BackendUser = {
  id: string;
  email?: string | null;
  displayName: string;
};

type TokenResponse = {
  user: BackendUser;
  accessToken: string;
  refreshToken: string;
  sessionId: string;
  organizationId?: string | null;
};

const apiUrl = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5062";
const pendingRefreshes = new Map<string, Promise<JWT>>();

function normalizeOrganizationId(value: unknown): string | null {
  if (value == null) {
    return null;
  }

  if (typeof value !== "string") {
    return String(value);
  }

  const normalized = value.trim();
  if (!normalized) {
    return null;
  }

  const lowered = normalized.toLowerCase();
  if (lowered === "null" || lowered === "undefined") {
    return null;
  }

  return normalized;
}

async function refreshAccessToken(token: JWT): Promise<JWT> {
  const currentRefreshToken = typeof token.refreshToken === "string" ? token.refreshToken : "";
  const organizationId = normalizeOrganizationId(token.organizationId);
  if (!currentRefreshToken) {
    console.warn("[Auth] Refresh requested without a refresh token.");
    return {
      ...token,
      error: "RefreshAccessTokenError",
      accessToken: "",
      refreshToken: ""
    };
  }

  const inFlight = pendingRefreshes.get(currentRefreshToken);
  if (inFlight) {
    console.info("[Auth] Reusing in-flight refresh request.", {
      sessionId: token.sessionId ?? null
    });
    return await inFlight;
  }

  const refreshPromise = (async () => {
    try {
      const decoded = typeof token.accessToken === "string"
        ? jwtDecode<DecodedToken>(token.accessToken)
        : null;
      const usesHostedSqlOS = decoded?.iss?.includes("/sqlos/auth") ?? false;

      console.info("[Auth] Refreshing access token.", {
        sessionId: token.sessionId ?? null,
        organizationId,
        usesHostedSqlOS
      });

      const response = usesHostedSqlOS
        ? await fetch(`${apiUrl}/sqlos/auth/token`, {
            method: "POST",
            headers: { "Content-Type": "application/x-www-form-urlencoded" },
            body: new URLSearchParams({
              grant_type: "refresh_token",
              refresh_token: currentRefreshToken
            })
          })
        : await fetch(`${apiUrl}/api/v1/auth/refresh`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              refreshToken: currentRefreshToken,
              organizationId
            })
          });

      const data = await response.json();
      if (!response.ok) {
        console.warn("[Auth] Refresh failed.", {
          status: response.status,
          message: data?.message ?? null,
          sessionId: token.sessionId ?? null
        });

        return {
          ...token,
          error: "RefreshAccessTokenError",
          accessToken: "",
          refreshToken: ""
        };
      }

      const nextAccessToken = data.accessToken ?? data.access_token;
      const nextRefreshToken = data.refreshToken ?? data.refresh_token;
      if (!nextAccessToken || !nextRefreshToken) {
        throw new Error("Refresh response did not include new tokens.");
      }

      const refreshedDecoded = jwtDecode<DecodedToken>(nextAccessToken);
      console.info("[Auth] Refresh succeeded.", {
        sessionId: data.sessionId ?? token.sessionId ?? null,
        accessTokenExpiresAt: new Date(refreshedDecoded.exp * 1000).toISOString()
      });

      return {
        ...token,
        accessToken: nextAccessToken,
        refreshToken: nextRefreshToken,
        sessionId: data.sessionId ?? refreshedDecoded.sid ?? token.sessionId ?? null,
        organizationId: normalizeOrganizationId(data.organizationId ?? refreshedDecoded.org_id ?? token.organizationId ?? null),
        exp: refreshedDecoded.exp,
        error: undefined
      };
    } catch (error) {
      console.error("[Auth] Refresh threw unexpectedly.", {
        sessionId: token.sessionId ?? null,
        error
      });

      return {
        ...token,
        error: "RefreshAccessTokenError",
        accessToken: "",
        refreshToken: ""
      };
    }
  })();

  pendingRefreshes.set(currentRefreshToken, refreshPromise);
  try {
    return await refreshPromise;
  } finally {
    pendingRefreshes.delete(currentRefreshToken);
  }
}

export const authOptions: AuthOptions = {
  pages: {
    signIn: "/login",
    error: "/login"
  },
  providers: [
    CredentialsProvider({
      name: "SqlOS credentials",
      credentials: {
        email: { label: "Email", type: "text" },
        password: { label: "Password", type: "password" },
        accessToken: { label: "Access Token", type: "text" },
        refreshToken: { label: "Refresh Token", type: "text" },
        userId: { label: "User ID", type: "text" },
        displayName: { label: "Display Name", type: "text" },
        organizationId: { label: "Organization ID", type: "text" },
        sessionId: { label: "Session ID", type: "text" }
      },
      async authorize(credentials): Promise<User | null> {
        if (credentials?.accessToken && credentials?.refreshToken && credentials?.userId) {
          const decoded = jwtDecode<DecodedToken>(credentials.accessToken);
          return {
            id: credentials.userId,
            email: credentials.email,
            name: credentials.displayName || decoded.name || decoded.email || decoded.sub,
            accessToken: credentials.accessToken,
            refreshToken: credentials.refreshToken,
            organizationId: normalizeOrganizationId(credentials.organizationId ?? decoded.org_id ?? null),
            sessionId: credentials.sessionId || decoded.sid || "",
            exp: decoded.exp
          } as User;
        }

        if (!credentials?.email || !credentials?.password) {
          throw new Error("Email and password are required.");
        }

        const response = await fetch(`${apiUrl}/api/v1/auth/login`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            email: credentials.email,
            password: credentials.password
          })
        });

        const data = await response.json();
        if (!response.ok) {
          throw new Error(data.message || "Login failed.");
        }

        if (data.requiresOrganizationSelection) {
          throw new Error("This demo frontend only supports users with one active organization membership.");
        }

        const typed = data as TokenResponse;
        const decoded = jwtDecode<DecodedToken>(typed.accessToken);

        return {
          id: typed.user.id,
          email: typed.user.email ?? credentials.email,
          name: typed.user.displayName,
          accessToken: typed.accessToken,
          refreshToken: typed.refreshToken,
          organizationId: normalizeOrganizationId(typed.organizationId ?? decoded.org_id ?? null),
          sessionId: typed.sessionId,
          exp: decoded.exp
        } as User;
      }
    })
  ],
  session: {
    strategy: "jwt",
    maxAge: 30 * 24 * 60 * 60
  },
  secret: process.env.NEXTAUTH_SECRET,
  callbacks: {
    async jwt({ token, user }) {
      if (user) {
        token.id = user.id;
        token.email = user.email;
        token.name = user.name;
        token.accessToken = user.accessToken;
        token.refreshToken = user.refreshToken;
        token.organizationId = normalizeOrganizationId(user.organizationId);
        token.sessionId = user.sessionId;
        token.exp = user.exp;
      }

      if (!token.accessToken) {
        return token;
      }

      try {
        const decoded = jwtDecode<DecodedToken>(token.accessToken as string);
        const currentTimeSeconds = Math.floor(Date.now() / 1000);
        if (decoded.exp && currentTimeSeconds >= decoded.exp) {
          return await refreshAccessToken(token);
        }
      } catch {
        return await refreshAccessToken(token);
      }

      return token;
    },
    async session({ session, token }) {
      session.user = {
        id: token.id,
        email: token.email,
        name: token.name
      } as User;
      session.accessToken = token.accessToken as string;
      session.organizationId = normalizeOrganizationId(token.organizationId);
      session.sessionId = token.sessionId as string | null;
      session.error = token.error as string | undefined;
      return session;
    }
  },
  events: {
    async signOut(message) {
      const token = "token" in message ? message.token : undefined;
      if (!token?.refreshToken && !token?.sessionId) {
        return;
      }

      await fetch(`${apiUrl}/api/v1/auth/logout`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          refreshToken: token.refreshToken ?? null,
          sessionId: token.sessionId ?? null
        })
      }).catch(() => undefined);
    }
  }
};
