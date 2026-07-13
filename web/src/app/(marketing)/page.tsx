import ClosingSection from "@/components/marketing/ClosingSection";
import GallerySection from "@/components/marketing/GallerySection";
import HeroCollage from "@/components/marketing/HeroCollage";
import StatementSection from "@/components/marketing/StatementSection";
import TrioSection from "@/components/marketing/TrioSection";
import { fetchGitHubStars } from "@/lib/github";

export default async function Home() {
  const githubStars = await fetchGitHubStars();

  return (
    <div className="relative min-h-screen">
      <HeroCollage />
      <TrioSection />
      <StatementSection />
      <GallerySection />
      <ClosingSection githubStars={githubStars} />
    </div>
  );
}
