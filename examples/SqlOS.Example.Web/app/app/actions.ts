"use server";

import { getServerSession } from "next-auth";
import { authOptions } from "@/lib/auth";

const apiUrl = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5062";

export async function fetchBackendSessionFromServerAction() {
  const session = await getServerSession(authOptions);
  if (!session?.accessToken) {
    return {
      ok: false,
      message: "No authenticated session is available on the server."
    };
  }

  const response = await fetch(`${apiUrl}/api/v1/auth/session`, {
    headers: {
      Authorization: `Bearer ${session.accessToken}`
    },
    cache: "no-store"
  });

  const payload = await response.json().catch(() => null);

  return {
    ok: response.ok,
    status: response.status,
    usedSessionId: session.sessionId,
    usedOrganizationId: session.organizationId,
    payload
  };
}
