"use client";

import { signOut } from "next-auth/react";
import { getExampleClientId } from "@/lib/sqlos-config";

const apiUrl = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5062";

export async function signOutWithSqlOS(callbackUrl: string) {
  const resolvedCallbackUrl = new URL(callbackUrl, window.location.origin).toString();

  await signOut({
    redirect: false,
    callbackUrl: resolvedCallbackUrl,
  });

  const logoutUrl = new URL("/sqlos/auth/logout", apiUrl);
  logoutUrl.searchParams.set("returnTo", resolvedCallbackUrl);
  logoutUrl.searchParams.set("client_id", getExampleClientId());
  window.location.assign(logoutUrl.toString());
}
