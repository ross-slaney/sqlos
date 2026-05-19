const GITHUB_REPO = "ross-slaney/sqlos";

export async function fetchGitHubStars(): Promise<string | null> {
  try {
    const response = await fetch(`https://api.github.com/repos/${GITHUB_REPO}`, {
      next: { revalidate: 3600 },
    });
    if (!response.ok) {
      return null;
    }
    const data = (await response.json()) as { stargazers_count?: number };
    if (typeof data.stargazers_count !== "number") {
      return null;
    }
    return data.stargazers_count.toLocaleString();
  } catch {
    return null;
  }
}
