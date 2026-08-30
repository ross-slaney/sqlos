import type { AuthOptions } from "next-auth";

// "Sign in with X" — a standard OpenID Connect provider block. App Y knows
// nothing about SqlOS: it discovers X's endpoints from the OIDC discovery
// document, runs authorization code + PKCE as a public client (no secret),
// validates the ID token, and reads profile claims from UserInfo.
// First-party hosted JS examples use this same Auth.js shape (see
// examples/SqlOS.Example.Web/lib/auth.ts) plus offline_access for API tokens.
const issuer = process.env.SQLOS_ISSUER ?? "http://localhost:5100/sqlos/auth";
const clientId = process.env.SQLOS_CLIENT_ID ?? "app-y";

export const authOptions: AuthOptions = {
  providers: [
    {
      id: "sqlos",
      name: "X",
      type: "oauth",
      wellKnown: `${issuer}/.well-known/openid-configuration`,
      clientId,
      // Public PKCE client: X registers app-y with
      // token_endpoint_auth_method "none", so there is no client secret.
      client: { token_endpoint_auth_method: "none" },
      authorization: { params: { scope: "openid profile email" } },
      // idToken: true keeps openid-client's full OIDC callback (ID-token
      // signature/iss/aud validation); the custom userinfo request sources
      // profile claims from UserInfo, where OIDC Core §5.4 releases them.
      idToken: true,
      userinfo: {
        async request({ client, tokens }) {
          return await client.userinfo(tokens);
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
    }
  ],
  callbacks: {
    async jwt({ token, account, profile }) {
      if (account && profile) {
        token.sub = typeof profile.sub === "string" ? profile.sub : token.sub;
        token.emailVerified =
          typeof (profile as Record<string, unknown>).email_verified === "boolean"
            ? ((profile as Record<string, unknown>).email_verified as boolean)
            : null;
        token.orgId =
          typeof (profile as Record<string, unknown>).org_id === "string"
            ? ((profile as Record<string, unknown>).org_id as string)
            : null;
        token.hasIdToken = Boolean(account.id_token);
      }
      return token;
    },
    async session({ session, token }) {
      return {
        ...session,
        user: {
          ...session.user,
          sub: token.sub ?? null,
          emailVerified: (token.emailVerified as boolean | null) ?? null,
          orgId: (token.orgId as string | null) ?? null
        },
        hasIdToken: (token.hasIdToken as boolean | undefined) ?? false
      };
    }
  }
};
