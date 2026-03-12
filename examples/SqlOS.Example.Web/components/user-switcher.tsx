"use client";

import { useSession, signIn } from "next-auth/react";
import { useEffect, useState } from "react";
import { apiUrl, setAuthOverride, getAuthOverride } from "@/lib/api";
import { jwtDecode } from "jwt-decode";

type DemoSubject = {
  email: string | null;
  displayName: string;
  role?: string | null;
  description?: string | null;
  type: "user" | "agent" | "service_account";
  credential: string | null;
};

type SwitchResponse = {
  accessToken: string;
  refreshToken: string;
  sessionId: string;
  organizationId: string;
};

export function UserSwitcher() {
  const { data: session } = useSession();
  const [subjects, setSubjects] = useState<DemoSubject[]>([]);
  const [switching, setSwitching] = useState(false);

  useEffect(() => {
    fetch(`${apiUrl}/api/demo/users`)
      .then((r) => r.json())
      .then(setSubjects)
      .catch(() => {});
  }, []);

  function selectKey(subject: DemoSubject): string {
    if (subject.email) return subject.email;
    if (subject.credential) return `${subject.type}:${subject.credential}`;
    return `${subject.type}:${subject.displayName}`;
  }

  async function handleSwitch(key: string) {
    if (switching) return;
    const subject = subjects.find((s) => selectKey(s) === key);
    if (!subject) return;

    setSwitching(true);
    try {
      if (subject.type === "agent" && subject.credential) {
        setAuthOverride({
          type: "agent",
          header: "X-Agent-Token",
          value: subject.credential,
          displayName: subject.displayName,
        });
        window.location.reload();
        return;
      }

      if (subject.type === "service_account" && subject.credential) {
        setAuthOverride({
          type: "service_account",
          header: "X-Api-Key",
          value: subject.credential,
          displayName: subject.displayName,
        });
        window.location.reload();
        return;
      }

      if (subject.type === "user" && subject.email) {
        setAuthOverride(null);

        const res = await fetch(`${apiUrl}/api/v1/auth/demo/switch`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ email: subject.email }),
        });
        if (!res.ok) throw new Error("Switch failed");
        const data: SwitchResponse = await res.json();
        const decoded = jwtDecode<{ sub?: string; exp: number; org_id?: string }>(data.accessToken);

        await signIn("credentials", {
          redirect: false,
          accessToken: data.accessToken,
          refreshToken: data.refreshToken,
          userId: decoded.sub ?? "",
          displayName: subject.displayName,
          email: subject.email,
          organizationId: data.organizationId ?? decoded.org_id ?? "",
          sessionId: data.sessionId,
        });
        window.location.reload();
      }
    } catch {
      // Ignore - the user can try again
    } finally {
      setSwitching(false);
    }
  }

  const currentEmail = session?.user?.email;
  const activeOverride = getAuthOverride();
  const currentKey = activeOverride
    ? `${activeOverride.type}:${activeOverride.value}`
    : (currentEmail ?? "");
  const selectedKey = subjects.some((subject) => selectKey(subject) === currentKey) ? currentKey : "";

  return (
    <div className="user-switcher">
      <select
        value={selectedKey}
        onChange={(e) => void handleSwitch(e.target.value)}
        disabled={switching || subjects.length === 0}
      >
        {!selectedKey && <option value="">Select an identity...</option>}
        {subjects.map((s) => (
          <option key={selectKey(s)} value={selectKey(s)}>
            {s.displayName}
            {s.role ? ` (${s.role})` : ""}
            {s.type !== "user" ? ` [${s.type}]` : ""}
          </option>
        ))}
      </select>
      {switching && <span className="muted">Switching...</span>}
    </div>
  );
}
