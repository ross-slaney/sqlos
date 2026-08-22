import { getServerSession } from "next-auth";
import { authOptions } from "@/lib/auth";
import { SignInWithX, SignOut } from "./auth-buttons";

export default async function Home() {
  const session = await getServerSession(authOptions);

  if (!session) {
    return (
      <main>
        <h1>App Y</h1>
        <p>
          App Y has no user database and no password forms. It signs users in
          with <strong>X</strong> — a SqlOS OpenID Provider — through Auth.js
          and the standard OIDC discovery document.
        </p>
        <SignInWithX />
        <p style={{ color: "#6b7280", fontSize: "0.9rem" }}>
          First sign-in shows X&apos;s consent screen because App Y is a
          third-party client; approval is remembered, so later sign-ins are
          silent.
        </p>
      </main>
    );
  }

  return (
    <main>
      <h1>Welcome to App Y</h1>
      <p>
        You are signed in with X as <strong>{session.user?.name}</strong>.
      </p>
      <table style={{ borderCollapse: "collapse" }}>
        <tbody>
          <tr>
            <td style={{ paddingRight: "1rem", color: "#6b7280" }}>Subject</td>
            <td>
              <code>{session.user?.sub}</code>
            </td>
          </tr>
          <tr>
            <td style={{ paddingRight: "1rem", color: "#6b7280" }}>Email</td>
            <td>
              {session.user?.email}{" "}
              {session.user?.emailVerified === true ? "(verified)" : session.user?.emailVerified === false ? "(unverified)" : ""}
            </td>
          </tr>
          <tr>
            <td style={{ paddingRight: "1rem", color: "#6b7280" }}>ID token</td>
            <td>{session.hasIdToken ? "validated" : "absent"}</td>
          </tr>
        </tbody>
      </table>
      <p>
        <SignOut />
      </p>
    </main>
  );
}
