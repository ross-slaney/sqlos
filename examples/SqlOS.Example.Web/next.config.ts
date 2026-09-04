import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  typedRoutes: true,
  transpilePackages: ["@sqlos/headless"],
  // Two copies of this app can run at once (the demo on :3010 and the e2e
  // test stack). Next.js corrupts builds when two dev servers share one dist
  // directory, so each stack compiles into its own.
  distDir: process.env.NEXT_DIST_DIR ?? ".next",
};

export default nextConfig;
