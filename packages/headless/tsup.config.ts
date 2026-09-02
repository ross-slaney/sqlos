import fs from "node:fs";
import { defineConfig } from "tsup";

export default defineConfig({
  entry: {
    index: "src/index.ts",
    react: "src/react.ts",
    "react-native": "src/react-native.ts",
  },
  format: ["esm", "cjs"],
  dts: true,
  splitting: false,
  sourcemap: true,
  clean: true,
  treeshake: true,
  external: ["react", "react-native"],
  target: "es2022",
  async onSuccess() {
    for (const file of ["dist/react.js", "dist/react.cjs"]) {
      const content = fs.readFileSync(file, "utf8");
      if (!content.startsWith('"use client"')) {
        fs.writeFileSync(file, `"use client";\n${content}`);
      }
    }
  },
});
