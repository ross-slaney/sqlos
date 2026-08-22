import type { ReactNode } from "react";

export const metadata = {
  title: "App Y — Sign in with X",
  description: "A Next.js + Auth.js relying party federating against a SqlOS OpenID Provider."
};

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="en">
      <body
        style={{
          fontFamily: "system-ui, sans-serif",
          maxWidth: "40rem",
          margin: "4rem auto",
          lineHeight: 1.6,
          padding: "0 1rem"
        }}
      >
        {children}
      </body>
    </html>
  );
}
