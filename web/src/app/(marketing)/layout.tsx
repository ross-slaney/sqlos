import type { Metadata } from "next";
import Footer from "@/components/Footer";
import Header from "@/components/Header";

export const metadata: Metadata = {
  metadataBase: new URL("https://sqlos.dev"),
  title: "SqlOS | Auth Stack for .NET Builders",
  description:
    "OAuth, hosted login, social auth, SSO, FGA, and an admin dashboard in one self-hosted .NET package.",
  openGraph: {
    title: "SqlOS | Auth Stack for .NET Builders",
    description:
      "Put OAuth, hosted login, social auth, SSO, FGA, and an admin dashboard inside your ASP.NET app and SQL Server.",
    url: "https://sqlos.dev",
    siteName: "SqlOS",
    images: [
      {
        url: "/docs/dashboard-home.png",
        width: 1280,
        height: 800,
        alt: "SqlOS admin dashboard",
      },
    ],
    locale: "en_US",
    type: "website",
  },
  twitter: {
    card: "summary_large_image",
    title: "SqlOS | Auth Stack for .NET Builders",
    description:
      "OAuth, hosted login, social auth, SSO, FGA, and dashboard UI in one self-hosted .NET package.",
    images: ["/docs/dashboard-home.png"],
  },
};

export default function MarketingLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <>
      <Header />
      <main>{children}</main>
      <Footer />
    </>
  );
}
