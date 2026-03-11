"use client";

import { SessionProvider } from "next-auth/react";
import { SessionErrorHandler } from "@/components/session-error-handler";

export function AppSessionProvider({ children }: { children: React.ReactNode }) {
  return (
    <SessionProvider>
      <SessionErrorHandler />
      {children}
    </SessionProvider>
  );
}
