import { renderHook, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { useHeadlessAuth } from "../src/react.js";

describe("useHeadlessAuth", () => {
  it("subscribes to flow updates", async () => {
    const fetch = vi.fn(async () =>
      new Response(
        JSON.stringify({
          view: "login",
          authBasePath: "/sqlos/auth",
          headlessApiBasePath: "/sqlos/auth/headless",
          requestId: "req_1",
          fieldErrors: {},
          organizationSelection: [],
          providers: [],
        }),
        { status: 200, headers: { "Content-Type": "application/json" } },
      ),
    );
    const { result } = renderHook(() =>
      useHeadlessAuth({
        issuer: "https://id.example.com/sqlos/auth",
        clientId: "acme-app",
        redirectUri: "https://app.example.com/auth/callback",
        fetch,
        credentials: "include",
      }),
    );

    await result.current.resume("https://app.example.com/auth/authorize?request=req_1");
    await waitFor(() => {
      expect(result.current.status).toBe("view");
      expect(result.current.viewModel?.requestId).toBe("req_1");
    });
  });
});
