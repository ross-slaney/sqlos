"use client";

import { signOutWithSqlOS } from "@/lib/sqlos-signout";

export function LogoutButton() {
  return (
    <button
      type="button"
      onClick={() => {
        void signOutWithSqlOS("/");
      }}
    >
      Sign out
    </button>
  );
}
