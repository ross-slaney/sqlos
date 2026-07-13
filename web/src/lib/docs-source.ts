import path from "node:path";
import { createDocsSource } from "@emcy/docs";

export const docsSource = createDocsSource({
  contentDir: path.join(process.cwd(), "content/docs"),
  basePath: "/docs",
  siteTitle: "SqlOS Docs",
  titleSuffix: "SqlOS Docs",
  sectionLabels: {
    "": "Start here",
    quickstarts: "Quickstarts",
    guides: "Guides",
    authserver: "AuthServer",
    fga: "Fine-Grained Auth",
    reference: "Reference",
  },
  sectionOrder: ["", "quickstarts", "guides", "authserver", "fga", "reference"],
});
