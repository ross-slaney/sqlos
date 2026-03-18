"use server";

import type { DocsSearchResponse } from "@/lib/docs";
import { searchDocs } from "@/lib/docs-search";

export async function searchDocsAction(query: string): Promise<DocsSearchResponse> {
  return searchDocs(query);
}
