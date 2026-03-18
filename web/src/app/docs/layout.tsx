import Header from "@/components/Header";
import DocsShell from "@/components/docs/DocsShell";
import { getAllGuides } from "@/lib/docs";

export default function DocsLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  const guides = getAllGuides();

  return (
    <div className="flex min-h-screen flex-col bg-white">
      <Header />
      <DocsShell guides={guides}>{children}</DocsShell>
    </div>
  );
}
