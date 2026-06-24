import type { Metadata } from "next";
import Footer from "@/components/Footer";
import Header from "@/components/Header";

export const metadata: Metadata = {
  metadataBase: new URL("https://sqlos.dev"),
  title: "SqlOS | Sample Program Walkthrough",
  description:
    "Read the ASP.NET Program.cs file that wires SqlOS services, routes, AuthPage setup, FGA policy, and SQL-backed state.",
  openGraph: {
    title: "SqlOS | Sample Program Walkthrough",
    description:
      "A source-oriented walkthrough of the sample host program that integrates SqlOS with ASP.NET, EF Core, and SQL Server.",
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
    title: "SqlOS | Sample Program Walkthrough",
    description:
      "Read the sample ASP.NET Program.cs file that wires SqlOS configuration, policy, and routes.",
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
