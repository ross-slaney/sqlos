import type { Metadata } from "next";
import { IBM_Plex_Mono, Manrope } from "next/font/google";
import "@emcy/docs/styles.css";
import "./globals.css";

const manrope = Manrope({
  variable: "--font-sans-ui",
  subsets: ["latin"],
});

const ibmPlexMono = IBM_Plex_Mono({
  variable: "--font-mono-ui",
  subsets: ["latin"],
  weight: ["400", "500", "600"],
});

export const metadata: Metadata = {
  title: "SqlOS | Sample Program Walkthrough",
  description:
    "A source-oriented walkthrough of the ASP.NET Program.cs file that wires SqlOS into a .NET application.",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en" className="dark" data-theme="dark" data-scrollbar="thin">
      <body
        className={`${manrope.variable} ${ibmPlexMono.variable} bg-background text-foreground antialiased`}
      >
        {children}
      </body>
    </html>
  );
}
