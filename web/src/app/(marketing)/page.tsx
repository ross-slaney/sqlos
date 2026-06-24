import OssCredibilityBand from "@/components/OssCredibilityBand";
import AuthSection from "@/components/marketing/AuthSection";
import AuthStackSection from "@/components/marketing/AuthStackSection";
import ConfigurationWalkthrough from "@/components/marketing/ConfigurationWalkthrough";
import CtaSection from "@/components/marketing/CtaSection";
import DeveloperExperienceSection from "@/components/marketing/DeveloperExperienceSection";
import ExampleAppsSection from "@/components/marketing/ExampleAppsSection";
import FeaturesSection from "@/components/marketing/FeaturesSection";
import FgaSection from "@/components/marketing/FgaSection";
import HeroSection from "@/components/marketing/HeroSection";
import HowItWorksSection from "@/components/marketing/HowItWorksSection";
import { fetchGitHubStars } from "@/lib/github";

export default async function Home() {
  const githubStars = await fetchGitHubStars();

  return (
    <div className="relative min-h-screen">
      <HeroSection />
      <ConfigurationWalkthrough />
      <OssCredibilityBand githubStars={githubStars} />
      <AuthSection />
      <AuthStackSection />
      <FgaSection />
      <DeveloperExperienceSection />
      <HowItWorksSection />
      <ExampleAppsSection />
      <FeaturesSection />
      <CtaSection />
    </div>
  );
}
