import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Two copies of this app can run at once (the demo on :3020 and the e2e
  // test stack on :3030). Next.js corrupts builds when two dev servers share
  // one dist directory, so each stack must compile into its own.
  distDir: process.env.NEXT_DIST_DIR ?? ".next"
};

export default nextConfig;
