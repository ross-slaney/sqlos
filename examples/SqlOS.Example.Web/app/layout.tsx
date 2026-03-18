import type { Metadata } from "next";
import { AppSessionProvider } from "@/components/session-provider";
import "./globals.css";

export const metadata: Metadata = {
  title: "Northwind Retail — SqlOS",
  description: "Manage chains, stores, and inventory with SqlOS authentication."
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en">
      <body>
        <AppSessionProvider>{children}</AppSessionProvider>
      </body>
    </html>
  );
}
