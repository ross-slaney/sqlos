"use client";

import { signOut, useSession } from "next-auth/react";
import { useRouter } from "next/navigation";
import { useEffect } from "react";

export function SessionErrorHandler() {
  const { data: session } = useSession();
  const router = useRouter();

  useEffect(() => {
    if (session?.error === "RefreshAccessTokenError") {
      void signOut({ redirect: false }).then(() => {
        router.push("/login");
      });
    }
  }, [router, session]);

  return null;
}
