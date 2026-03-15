"use client";

import { signOut } from "next-auth/react";

const apiUrl = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5062";

export async function signOutWithSqlOS(callbackUrl: string) {
  const resolvedCallbackUrl = new URL(callbackUrl, window.location.origin).toString();

  await signOut({
    redirect: false,
    callbackUrl: resolvedCallbackUrl,
  });

  const logoutUrl = new URL("/sqlos/auth/logout", apiUrl);
  logoutUrl.searchParams.set("returnTo", resolvedCallbackUrl);
  window.location.assign(logoutUrl.toString());
}
