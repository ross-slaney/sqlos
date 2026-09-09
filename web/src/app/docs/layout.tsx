import { DocsLayout, DocsSearch } from "@emcy/docs";
import { searchDocsAction } from "@/app/docs/actions";
import Header from "@/components/Header";
import Footer from "@/components/Footer";
import { docsSource } from "@/lib/docs-source";

export default function DocsRootLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <>
      <Header />
      <DocsLayout
        navigation={docsSource.getNavigation()}
        searchAction={searchDocsAction}
        variant="embedded"
        className="sqlos-docs-shell"
        sidebarHeader={
          <DocsSearch
            searchAction={searchDocsAction}
            placeholder="Search docs..."
          />
        }
        theme={{
          color: {
            preset: "neutral",
            mode: "light",
            accentHue: 270,
            accentStrength: "bold",
            surfaceStyle: "elevated",
          },
          layout: {
            density: "comfortable",
            layoutWidth: "1440px",
            contentWidth: "48rem",
            sidebarWidth: "260px",
            tocWidth: "220px",
          },
          shape: {
            radius: "lg",
          },
          tokens: {
            background: "40 20% 99%",
            foreground: "240 22% 8%",
            card: "0 0% 100%",
            cardForeground: "240 22% 8%",
            popover: "0 0% 100%",
            popoverForeground: "240 22% 8%",
            primary: "270 74% 42%",
            primaryForeground: "0 0% 100%",
            secondary: "228 22% 96%",
            secondaryForeground: "240 22% 8%",
            muted: "228 18% 95%",
            mutedForeground: "240 12% 28%",
            accent: "270 90% 95%",
            accentForeground: "270 70% 24%",
            border: "228 16% 80%",
            borderStrong: "228 16% 68%",
            input: "228 16% 80%",
            ring: "270 82% 46%",
            accentSoft: "270 90% 94% / 0.7",
            surface: "0 0% 100%",
            bg: "40 20% 99%",
            codeBg: "228 26% 93%",
            codeBorder: "228 16% 72%",
            info: "217 90% 44%",
            infoSoft: "217 92% 92% / 0.7",
            warning: "32 90% 42%",
            warningSoft: "38 94% 88% / 0.75",
            error: "0 78% 46%",
            errorSoft: "0 86% 92% / 0.7",
            success: "145 72% 28%",
            successSoft: "145 74% 90% / 0.68",
            shadowSm: "0px 1px 2px hsl(240 20% 8% / 0.08)",
            shadowLg: "0px 16px 40px hsl(240 20% 8% / 0.1)",
          },
        }}
      >
        {children}
      </DocsLayout>
      <Footer />
    </>
  );
}
