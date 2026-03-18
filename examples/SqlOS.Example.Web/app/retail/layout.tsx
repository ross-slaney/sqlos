import { Sidebar } from "@/components/sidebar";

export default function RetailLayout({ children }: { children: React.ReactNode }) {
  return (
    <>
      <Sidebar />
      <main className="app-shell">
        <div className="page-container">{children}</div>
      </main>
    </>
  );
}
