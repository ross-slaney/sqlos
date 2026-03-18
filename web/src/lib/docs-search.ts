import {
  getAllGuides,
  getDocSectionLabel,
  type DocGuide,
  type DocsSearchResponse,
  type DocsSearchResult,
} from "@/lib/docs";

const RESULT_LIMIT = 8;

export function searchDocs(query: string): DocsSearchResponse {
  const trimmedQuery = query.trim();

  if (trimmedQuery.length < 2) {
    return {
      query: trimmedQuery,
      total: 0,
      results: [],
    };
  }

  const normalizedQuery = trimmedQuery.toLowerCase();
  const queryTerms = getQueryTerms(trimmedQuery);

  const rankedResults = getAllGuides()
    .map((guide) => scoreGuide(guide, normalizedQuery, queryTerms))
    .filter((result): result is RankedDocsSearchResult => result !== null)
    .sort((left, right) => {
      if (right.score !== left.score) {
        return right.score - left.score;
      }

      return left.title.localeCompare(right.title);
    });

  return {
    query: trimmedQuery,
    total: rankedResults.length,
    results: rankedResults.slice(0, RESULT_LIMIT).map((result) => ({
      slug: result.slug,
      href: result.href,
      title: result.title,
      description: result.description,
      section: result.section,
      sectionLabel: result.sectionLabel,
      snippet: result.snippet,
      matchedFields: result.matchedFields,
    })),
  };
}

interface RankedDocsSearchResult extends DocsSearchResult {
  score: number;
}

function scoreGuide(
  guide: DocGuide,
  normalizedQuery: string,
  queryTerms: string[]
): RankedDocsSearchResult | null {
  const title = guide.title.trim();
  const description = guide.description.trim();
  const contentText = stripMarkdown(guide.content);

  const normalizedTitle = title.toLowerCase();
  const normalizedDescription = description.toLowerCase();
  const normalizedContent = contentText.toLowerCase();

  let score = 0;
  const matchedFields = new Set<DocsSearchResult["matchedFields"][number]>();

  if (normalizedTitle.includes(normalizedQuery)) {
    score += 140;
    matchedFields.add("title");
  }

  if (normalizedDescription.includes(normalizedQuery)) {
    score += 60;
    matchedFields.add("description");
  }

  if (normalizedContent.includes(normalizedQuery)) {
    score += 36;
    matchedFields.add("content");
  }

  let matchedTermCount = 0;

  for (const term of queryTerms) {
    const titleMatches = countMatches(normalizedTitle, term);
    const descriptionMatches = countMatches(normalizedDescription, term);
    const contentMatches = Math.min(6, countMatches(normalizedContent, term));

    if (titleMatches || descriptionMatches || contentMatches) {
      matchedTermCount += 1;
    }

    if (titleMatches > 0) {
      score += 28 + (titleMatches - 1) * 8;
      matchedFields.add("title");
    }

    if (descriptionMatches > 0) {
      score += 14 + (descriptionMatches - 1) * 4;
      matchedFields.add("description");
    }

    if (contentMatches > 0) {
      score += 7 + (contentMatches - 1) * 2;
      matchedFields.add("content");
    }
  }

  if (matchedFields.size === 0) {
    return null;
  }

  if (matchedTermCount === queryTerms.length) {
    score += 20;
  }

  const snippet = createSnippet({
    contentText,
    description,
    normalizedQuery,
    queryTerms,
  });

  return {
    slug: guide.slug,
    href: `/docs/guides/${guide.slug}`,
    title,
    description,
    section: guide.section,
    sectionLabel: getDocSectionLabel(guide.section),
    snippet,
    matchedFields: Array.from(matchedFields),
    score,
  };
}

function createSnippet({
  contentText,
  description,
  normalizedQuery,
  queryTerms,
}: {
  contentText: string;
  description: string;
  normalizedQuery: string;
  queryTerms: string[];
}): string {
  const fallbackText = description || contentText;

  if (!contentText) {
    return fallbackText;
  }

  const normalizedContent = contentText.toLowerCase();
  const phraseIndex = normalizedContent.indexOf(normalizedQuery);
  const termIndexes = queryTerms
    .map((term) => normalizedContent.indexOf(term))
    .filter((index) => index >= 0);

  const matchIndex =
    phraseIndex >= 0
      ? phraseIndex
      : termIndexes.length > 0
        ? Math.min(...termIndexes)
        : -1;

  if (matchIndex < 0) {
    return trimSnippet(fallbackText, 180);
  }

  const preferredStart = Math.max(0, matchIndex - 92);
  const preferredEnd = Math.min(contentText.length, matchIndex + 156);

  const sentenceStart = findSnippetStart(contentText, preferredStart);
  const sentenceEnd = findSnippetEnd(contentText, preferredEnd);

  let snippet = contentText.slice(sentenceStart, sentenceEnd).trim();

  if (sentenceStart > 0) {
    snippet = `...${snippet}`;
  }

  if (sentenceEnd < contentText.length) {
    snippet = `${snippet}...`;
  }

  return trimSnippet(snippet, 220);
}

function findSnippetStart(text: string, preferredStart: number): number {
  const punctuationIndex = Math.max(
    text.lastIndexOf(". ", preferredStart),
    text.lastIndexOf(": ", preferredStart),
    text.lastIndexOf("? ", preferredStart),
    text.lastIndexOf("! ", preferredStart)
  );

  return punctuationIndex >= 0 ? punctuationIndex + 2 : preferredStart;
}

function findSnippetEnd(text: string, preferredEnd: number): number {
  const punctuationMatches = [
    text.indexOf(". ", preferredEnd),
    text.indexOf("? ", preferredEnd),
    text.indexOf("! ", preferredEnd),
  ].filter((index) => index >= 0);

  if (punctuationMatches.length === 0) {
    return preferredEnd;
  }

  return Math.min(...punctuationMatches) + 1;
}

function trimSnippet(text: string, maxLength: number): string {
  if (text.length <= maxLength) {
    return text;
  }

  return `${text.slice(0, maxLength).trimEnd()}...`;
}

function countMatches(text: string, term: string): number {
  if (!term) {
    return 0;
  }

  let count = 0;
  let searchIndex = 0;

  while (true) {
    const matchIndex = text.indexOf(term, searchIndex);

    if (matchIndex < 0) {
      return count;
    }

    count += 1;
    searchIndex = matchIndex + term.length;
  }
}

function getQueryTerms(query: string): string[] {
  const terms = query
    .toLowerCase()
    .split(/[^a-z0-9]+/i)
    .map((term) => term.trim())
    .filter((term) => term.length >= 2);

  return Array.from(new Set(terms.length > 0 ? terms : [query.toLowerCase()]));
}

function stripMarkdown(content: string): string {
  return content
    .replace(/```[\s\S]*?```/g, (block) =>
      block
        .replace(/```[a-zA-Z0-9-]*\n?/g, " ")
        .replace(/```/g, " ")
        .replace(/\n/g, " ")
    )
    .replace(/!\[[^\]]*]\([^)]+\)/g, " ")
    .replace(/\[([^\]]+)\]\([^)]+\)/g, "$1")
    .replace(/`([^`]+)`/g, "$1")
    .replace(/^>\s?/gm, "")
    .replace(/^#{1,6}\s+/gm, "")
    .replace(/^\s*[-*+]\s+/gm, "")
    .replace(/^\s*\d+\.\s+/gm, "")
    .replace(/\|/g, " ")
    .replace(/<[^>]+>/g, " ")
    .replace(/[*_~]/g, "")
    .replace(/\s+/g, " ")
    .trim();
}
