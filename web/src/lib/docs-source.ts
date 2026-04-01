import path from "node:path";
import { createDocsSource } from "@emcy/docs";

export const docsSource = createDocsSource({
  contentDir: path.join(process.cwd(), "content/docs"),
  basePath: "/docs/guides",
  siteTitle: "SqlOS Docs",
  titleSuffix: "SqlOS Docs",
  sectionLabels: {
    "": "Getting Started",
    authserver: "AuthServer",
    fga: "Fine-Grained Auth",
    reference: "Reference",
  },
  sectionOrder: ["", "authserver", "fga", "reference"],
});
