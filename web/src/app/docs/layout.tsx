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
            mode: "dark",
            accentHue: 188,
            accentStrength: "balanced",
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
            radius: "md",
          },
          tokens: {
            background: "222 58% 5%",
            foreground: "184 42% 94%",
            card: "221 47% 10%",
            cardForeground: "184 42% 94%",
            popover: "221 48% 8%",
            popoverForeground: "184 42% 94%",
            primary: "188 90% 62%",
            primaryForeground: "222 58% 5%",
            secondary: "211 48% 14%",
            secondaryForeground: "184 42% 94%",
            muted: "214 38% 15%",
            mutedForeground: "197 27% 70%",
            accent: "188 90% 62%",
            accentForeground: "222 58% 5%",
            border: "197 50% 28%",
            borderStrong: "188 80% 45%",
            input: "214 45% 18%",
            ring: "148 86% 62%",
            accentSoft: "188 90% 62% / 0.16",
            surface: "221 47% 9%",
            bg: "222 58% 5%",
            codeBg: "223 60% 4%",
            codeBorder: "188 70% 27%",
            info: "188 90% 62%",
            infoSoft: "188 90% 62% / 0.16",
            warning: "78 92% 59%",
            warningSoft: "78 92% 59% / 0.16",
            error: "15 90% 62%",
            errorSoft: "15 90% 62% / 0.16",
            success: "148 86% 62%",
            successSoft: "148 86% 62% / 0.16",
            shadowSm: "0px 1px 2px hsl(188 90% 62% / 0.08)",
            shadowLg: "0px 18px 70px hsl(0 0% 0% / 0.42)",
          },
        }}
      >
        {children}
      </DocsLayout>
      <Footer />
    </>
  );
}
