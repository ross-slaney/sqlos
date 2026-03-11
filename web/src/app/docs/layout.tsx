import DocsHeader from "@/components/docs/DocsHeader";

export default function DocsLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <div className="flex min-h-screen flex-col bg-white">
      <DocsHeader />
      <div className="flex-1">{children}</div>
    </div>
  );
}
