import type { DefaultSession } from "next-auth";

declare module "next-auth" {
  interface Session {
    hasIdToken: boolean;
    user: DefaultSession["user"] & {
      sub?: string | null;
      emailVerified?: boolean | null;
      orgId?: string | null;
    };
  }
}
