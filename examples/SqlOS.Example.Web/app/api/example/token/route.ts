import { NextRequest, NextResponse } from "next/server";
import { getServerSession } from "next-auth";
import { getToken } from "next-auth/jwt";
import { jwtDecode } from "jwt-decode";
import { authOptions } from "@/lib/auth";

type DecodedToken = {
  exp?: number;
  sub?: string;
  sid?: string;
  email?: string;
  org_id?: string;
  client_id?: string;
  amr?: string;
  [key: string]: unknown;
};

export const dynamic = "force-dynamic";

function toIso(exp?: number): string | null {
  return exp ? new Date(exp * 1000).toISOString() : null;
}

export async function GET(request: NextRequest) {
  const rawToken = await getToken({ req: request, secret: process.env.NEXTAUTH_SECRET });
  if (!rawToken?.accessToken) {
    return NextResponse.json({ message: "Not authenticated." }, { status: 401 });
  }

  let refreshRequired = false;
  let previousExpiresAt: string | null = null;
  let previousSecondsRemaining: number | null = null;

  try {
    const decoded = jwtDecode<DecodedToken>(rawToken.accessToken as string);
    const now = Math.floor(Date.now() / 1000);
    const exp = decoded.exp;

    previousExpiresAt = toIso(exp);
    previousSecondsRemaining = exp ? exp - now : null;
    refreshRequired = !!exp && now >= exp - 60;

    if (refreshRequired) {
      console.info("[TokenDebug] Access token refresh required.", {
        previousExpiresAt,
        previousSecondsRemaining
      });
    } else {
      console.info("[TokenDebug] Access token still valid.", {
        previousExpiresAt,
        previousSecondsRemaining
      });
    }
  } catch {
    refreshRequired = true;
    console.info("[TokenDebug] Access token could not be decoded. Refresh will be attempted.");
  }

  const session = await getServerSession(authOptions);
  if (!session?.accessToken) {
    return NextResponse.json({ message: "Session is unavailable." }, { status: 401 });
  }

  const currentDecoded = jwtDecode<DecodedToken>(session.accessToken);
  const currentExpiresAt = toIso(currentDecoded.exp);

  console.info("[TokenDebug] Returning current access token.", {
    refreshRequired,
    currentExpiresAt,
    sessionId: session.sessionId,
    organizationId: session.organizationId
  });

  return NextResponse.json({
    refreshRequired,
    previousExpiresAt,
    previousSecondsRemaining,
    currentExpiresAt,
    sessionId: session.sessionId,
    organizationId: session.organizationId,
    accessToken: session.accessToken,
    decodedAccessToken: currentDecoded,
    refreshTokenExposed: false,
    sessionError: session.error ?? null
  });
}
