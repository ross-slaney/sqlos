"use client";

import { useSession } from "next-auth/react";
import { useEffect } from "react";
import { signOutWithSqlOS } from "@/lib/sqlos-signout";

export function SessionErrorHandler() {
  const { data: session } = useSession();

  useEffect(() => {
    if (session?.error === "RefreshAccessTokenError") {
      void signOutWithSqlOS("/login");
    }
  }, [session]);

  return null;
}
