import type { Metadata } from "next";
import Footer from "@/components/Footer";
import Header from "@/components/Header";

export const metadata: Metadata = {
  metadataBase: new URL("https://sqlos.dev"),
  title: "SqlOS | Auth, Social Login, SSO, and FGA for .NET",
  description:
    "Embedded OAuth server, branded login, enterprise SSO, and fine-grained authorization for .NET — one NuGet package in your process and your SQL Server or PostgreSQL.",
  openGraph: {
    title: "SqlOS | Enterprise auth for your .NET app",
    description:
      "OAuth, SAML SSO, social login, and FGA in one self-hosted NuGet package. See the dashboard, run the Todo sample, and ship in minutes.",
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
    title: "SqlOS | Enterprise auth for your .NET app",
    description:
      "OAuth, SAML SSO, social login, and FGA in one self-hosted NuGet package.",
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
