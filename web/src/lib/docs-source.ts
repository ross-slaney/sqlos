import path from "node:path";
import { createDocsSource } from "@agenetix/docs";
import nav from "../../docs-nav-map.json";

export const docsSource = createDocsSource({
  contentDir: path.join(process.cwd(), "content/docs"),
  basePath: "/docs",
  siteTitle: "SqlOS Docs",
  titleSuffix: "SqlOS Docs",
  sectionLabels: nav.sections,
  groupLabels: nav.groups,
  sectionOrder: nav.order,
});
