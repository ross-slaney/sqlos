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
            accentStrength: "balanced",
            surfaceStyle: "tinted",
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
            background: "220 20% 99%",
            foreground: "240 18% 10%",
            card: "230 41.6% 98.8%",
            cardForeground: "240 18% 10%",
            popover: "228 38.9% 99%",
            popoverForeground: "240 18% 10%",
            primary: "270 74% 42%",
            primaryForeground: "0 0% 100%",
            secondary: "225 24% 97%",
            secondaryForeground: "240 18% 10%",
            muted: "225 18% 96%",
            mutedForeground: "240 8% 43%",
            accent: "270 96% 95%",
            accentForeground: "270 66% 26%",
            border: "225 16% 88%",
            borderStrong: "225 21% 80%",
            input: "225 18% 86%",
            ring: "270 82% 50%",
            accentSoft: "270 96% 94% / 0.56",
            surface: "232 44.3% 98.6%",
            bg: "220 20% 99%",
            codeBg: "231.3 35.8% 94.9%",
            codeBorder: "225 20% 83%",
            info: "217 90% 56%",
            infoSoft: "217 92% 92% / 0.65",
            warning: "38 92% 50%",
            warningSoft: "38 94% 88% / 0.72",
            error: "0 82% 58%",
            errorSoft: "0 86% 92% / 0.68",
            success: "145 72% 36%",
            successSoft: "145 74% 90% / 0.64",
            shadowSm: "0px 1px 2px hsl(240 15% 10% / 0.08)",
            shadowLg: "0px 12px 42px hsl(240 15% 10% / 0.14)",
          },
        }}
      >
        {children}
      </DocsLayout>
      <Footer />
    </>
  );
}
