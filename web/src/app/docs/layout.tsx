import { DocsLayout, DocsSearch } from "@agenetix/docs";
import { searchDocsAction } from "@/app/docs/actions";
import Header from "@/components/Header";
import Footer from "@/components/Footer";
import { docsTheme } from "@/lib/docs-layout";
import { docsSource } from "@/lib/docs-source";

export default function DocsRootLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <div className="sqlos-docs-site">
      <Header fullBleed />
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
        theme={docsTheme}
      >
        {children}
      </DocsLayout>
      <Footer />
    </div>
  );
}
