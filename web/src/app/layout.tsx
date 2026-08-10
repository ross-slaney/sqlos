import type { Metadata } from "next";
import { Inter, JetBrains_Mono } from "next/font/google";
import "@emcy/docs/styles.css";
import "./globals.css";
import ThemeProvider from "@/components/ThemeProvider";

const inter = Inter({
  variable: "--font-sans-ui",
  subsets: ["latin"],
  weight: ["400", "500", "600", "700", "800"],
});

const jetbrainsMono = JetBrains_Mono({
  variable: "--font-mono-ui",
  subsets: ["latin"],
  weight: ["400", "500", "600"],
});

export const metadata: Metadata = {
  title: "SqlOS | Auth, Social Login, SSO, and FGA for .NET",
  description:
    "SqlOS is an embedded platform for auth, social login, SSO, and graph-shaped authorization in .NET. Ship an auth server and FGA runtime inside your application, with premium operator guides and a full example stack.",
};

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en" suppressHydrationWarning data-theme="violet">
      <body className={`${inter.variable} ${jetbrainsMono.variable} antialiased`}>
        <ThemeProvider>{children}</ThemeProvider>
      </body>
    </html>
  );
}
