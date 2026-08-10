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
            accentHue: 244,
            accentStrength: "balanced",
            surfaceStyle: "flat",
          },
          layout: {
            density: "comfortable",
            layoutWidth: "1440px",
            contentWidth: "52rem",
            sidebarWidth: "264px",
            tocWidth: "220px",
          },
          shape: {
            radius: "lg",
          },
          tokens: {
            background: "0 0% 100%",
            foreground: "0 0% 4%",
            card: "0 0% 100%",
            cardForeground: "0 0% 4%",
            popover: "0 0% 100%",
            popoverForeground: "0 0% 4%",
            primary: "244 76% 59%",
            primaryForeground: "0 0% 100%",
            secondary: "0 0% 98%",
            secondaryForeground: "0 0% 4%",
            muted: "240 5% 96%",
            mutedForeground: "220 9% 46%",
            accent: "226 100% 97%",
            accentForeground: "245 58% 51%",
            border: "240 6% 91%",
            borderStrong: "240 5% 84%",
            input: "240 6% 91%",
            ring: "244 76% 59%",
            accentSoft: "226 100% 97% / 0.8",
            surface: "0 0% 99%",
            bg: "0 0% 100%",
            codeBg: "240 5% 96%",
            codeBorder: "240 6% 88%",
            info: "217 90% 56%",
            infoSoft: "217 92% 92% / 0.65",
            warning: "38 92% 50%",
            warningSoft: "38 94% 88% / 0.72",
            error: "0 82% 58%",
            errorSoft: "0 86% 92% / 0.68",
            success: "145 72% 36%",
            successSoft: "145 74% 90% / 0.64",
            shadowSm: "0px 1px 2px hsl(240 15% 10% / 0.05)",
            shadowLg: "0px 12px 40px hsl(240 15% 20% / 0.12)",
          },
        }}
      >
        {children}
      </DocsLayout>
      <Footer />
    </>
  );
}
