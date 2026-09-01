import type { AuthOptions, User } from "next-auth";
import CredentialsProvider from "next-auth/providers/credentials";
import type { JWT } from "next-auth/jwt";
import { jwtDecode } from "jwt-decode";
import { getExampleApiUrl, getExampleAuthServerUrl, getExampleClientId } from "@/lib/sqlos-config";

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

const apiUrl = getExampleApiUrl();
const issuer = getExampleAuthServerUrl();
const clientId = getExampleClientId();
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

function applyAccessToken(token: JWT, accessToken: string, refreshToken: string, extras?: {
  sessionId?: string | null;
  organizationId?: string | null;
}): JWT {
  const decoded = jwtDecode<DecodedToken>(accessToken);
  return {
    ...token,
    accessToken,
    refreshToken,
    sessionId: extras?.sessionId ?? decoded.sid ?? token.sessionId ?? null,
    organizationId: normalizeOrganizationId(extras?.organizationId ?? decoded.org_id ?? token.organizationId ?? null),
    exp: decoded.exp,
    error: undefined
  };
}

async function refreshOidcAccessToken(token: JWT): Promise<JWT> {
  const currentRefreshToken = typeof token.refreshToken === "string" ? token.refreshToken : "";
  if (!currentRefreshToken) {
    return {
      ...token,
      error: "RefreshAccessTokenError",
      accessToken: "",
      refreshToken: ""
    };
  }

  const inFlight = pendingRefreshes.get(currentRefreshToken);
  if (inFlight) {
    return await inFlight;
  }

  const refreshPromise = (async () => {
    try {
      const response = await fetch(`${issuer}/token`, {
        method: "POST",
        headers: { "Content-Type": "application/x-www-form-urlencoded" },
        body: new URLSearchParams({
          grant_type: "refresh_token",
          refresh_token: currentRefreshToken,
          client_id: clientId
        })
      });

      const data = await response.json();
      if (!response.ok) {
        return {
          ...token,
          error: "RefreshAccessTokenError",
          accessToken: "",
          refreshToken: ""
        };
      }

      const nextAccessToken = data.access_token ?? data.accessToken;
      const nextRefreshToken = data.refresh_token ?? data.refreshToken;
      if (!nextAccessToken || !nextRefreshToken) {
        throw new Error("Refresh response did not include new tokens.");
      }

      return applyAccessToken(token, nextAccessToken, nextRefreshToken, {
        sessionId: data.sessionId ?? token.sessionId,
        organizationId: data.organizationId ?? token.organizationId
      });
    } catch {
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
    signIn: "/",
    error: "/"
  },
  providers: [
    {
      id: "sqlos",
      name: "SqlOS",
      type: "oauth",
      wellKnown: `${issuer}/.well-known/openid-configuration`,
      clientId,
      // Public PKCE client: example-web is registered with
      // token_endpoint_auth_method "none", so there is no client secret.
      // This is the same Auth.js shape as Sign in with X App Y.
      client: { token_endpoint_auth_method: "none" },
      authorization: {
        params: { scope: "openid profile email offline_access" }
      },
      idToken: true,
      userinfo: {
        async request({ client, tokens }) {
          return await client.userinfo(tokens.access_token!);
        }
      },
      checks: ["pkce", "state"],
      profile(profile) {
        return {
          id: profile.sub,
          name: profile.name ?? profile.preferred_username ?? profile.sub,
          email: profile.email ?? null
        };
      }
    },
    CredentialsProvider({
      id: "example-api",
      name: "Example API demo",
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
    async jwt({ token, user, account, profile }) {
      if (account?.provider === "sqlos" && account.access_token && account.refresh_token) {
        const decoded = jwtDecode<DecodedToken>(account.access_token);
        token.id = typeof profile?.sub === "string" ? profile.sub : decoded.sub ?? token.id;
        token.email = typeof profile?.email === "string" ? profile.email : decoded.email ?? token.email;
        token.name = typeof profile?.name === "string"
          ? profile.name
          : decoded.name ?? decoded.email ?? decoded.sub ?? token.name;
        token.provider = "sqlos";
        return applyAccessToken(token, account.access_token, account.refresh_token, {
          sessionId: decoded.sid,
          organizationId: decoded.org_id
        });
      }

      if (user) {
        token.id = user.id;
        token.email = user.email;
        token.name = user.name;
        token.accessToken = user.accessToken;
        token.refreshToken = user.refreshToken;
        token.organizationId = normalizeOrganizationId(user.organizationId);
        token.sessionId = user.sessionId;
        token.exp = user.exp;
        token.provider = "example-api";
      }

      if (!token.accessToken) {
        return token;
      }

      try {
        const decoded = jwtDecode<DecodedToken>(token.accessToken as string);
        const currentTimeSeconds = Math.floor(Date.now() / 1000);
        if (decoded.exp && currentTimeSeconds >= decoded.exp) {
          return await refreshOidcAccessToken(token);
        }
      } catch {
        return await refreshOidcAccessToken(token);
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
