import { DefaultSession, DefaultUser } from "next-auth";
import { JWT as DefaultJwt } from "next-auth/jwt";

declare module "next-auth" {
  interface Session {
    accessToken: string;
    organizationId: string | null;
    sessionId: string | null;
    error?: string;
    user: DefaultSession["user"] & {
      id: string;
      name?: string | null;
    };
  }

  interface User extends DefaultUser {
    accessToken: string;
    refreshToken: string;
    organizationId: string | null;
    sessionId: string;
    exp: number;
  }
}

declare module "next-auth/jwt" {
  interface JWT extends DefaultJwt {
    accessToken?: string;
    refreshToken?: string;
    organizationId?: string | null;
    sessionId?: string | null;
    exp?: number;
    error?: string;
  }
}
