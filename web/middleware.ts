import type { NextRequest } from "next/server";
import { NextResponse } from "next/server";

const apexHost = "sqlos.dev";
const wwwHost = "www.sqlos.dev";

export function middleware(request: NextRequest) {
  const forwardedHost = request.headers.get("x-forwarded-host");
  const hostHeader = forwardedHost ?? request.headers.get("host");

  if (!hostHeader) {
    return NextResponse.next();
  }

  const host = hostHeader.split(":")[0].toLowerCase();

  if (host !== wwwHost) {
    return NextResponse.next();
  }

  const url = request.nextUrl.clone();
  url.protocol = "https";
  url.hostname = apexHost;
  url.port = "";

  return NextResponse.redirect(url, 308);
}

export const config = {
  matcher: "/:path*",
};
