import { DocsLayout, DocsSearch } from "@agenetix/docs";
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
    <div className="sqlos-docs-site">
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
            background: "0 0% 100%",
            foreground: "240 20% 8%",
            card: "0 0% 100%",
            cardForeground: "240 20% 8%",
            popover: "0 0% 100%",
            popoverForeground: "240 20% 8%",
            primary: "270 74% 42%",
            primaryForeground: "0 0% 100%",
            secondary: "240 8% 96%",
            secondaryForeground: "240 20% 8%",
            muted: "240 8% 96%",
            mutedForeground: "240 8% 32%",
            accent: "270 90% 95%",
            accentForeground: "270 70% 24%",
            border: "240 8% 86%",
            borderStrong: "240 8% 74%",
            input: "240 8% 80%",
            ring: "270 82% 46%",
            accentSoft: "270 90% 94% / 0.7",
            surface: "0 0% 100%",
            bg: "0 0% 100%",
            codeBg: "240 12% 10%",
            codeBorder: "240 10% 20%",
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
    </div>
  );
}
