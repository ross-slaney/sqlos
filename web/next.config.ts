import path from "node:path";
import type { NextConfig } from "next";
import createMDX from "@next/mdx";

const nextConfig: NextConfig = {
  pageExtensions: ["js", "jsx", "md", "mdx", "ts", "tsx"],
  experimental: {
    externalDir: true,
  },
  transpilePackages: ["@emcy/docs"],
  turbopack: {
    root: path.resolve(__dirname, "../.."),
  },
};

const withMDX = createMDX({
  options: {
    rehypePlugins: [],
  },
});

export default withMDX(nextConfig);
