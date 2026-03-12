import { Header } from "@/components/header";

export default function RetailLayout({ children }: { children: React.ReactNode }) {
  return (
    <>
      <Header />
      <main className="shell retail-shell">{children}</main>
    </>
  );
}
