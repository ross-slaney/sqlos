import path from "node:path";
import type { NextConfig } from "next";
import createMDX from "@next/mdx";
import docsRedirects from "./docs-redirects.json";

const nextConfig: NextConfig = {
  pageExtensions: ["js", "jsx", "md", "mdx", "ts", "tsx"],
  experimental: {
    externalDir: true,
  },
  transpilePackages: ["@agenetix/docs"],
  turbopack: {
    root: path.resolve(__dirname, "../.."),
  },
  async redirects() {
    return docsRedirects;
  },
};

const withMDX = createMDX({
  options: {
    rehypePlugins: [],
  },
});

export default withMDX(nextConfig);
