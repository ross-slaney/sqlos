"use server";

import type { DocsSearchResponse } from "@emcy/docs";
import { docsSource } from "@/lib/docs-source";

export async function searchDocsAction(query: string): Promise<DocsSearchResponse> {
  return docsSource.search(query);
}
