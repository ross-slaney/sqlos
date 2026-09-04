import { memo, useEffect, useRef } from "react";
import { act, render, renderHook, waitFor } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { useHeadlessAuth } from "../src/react.js";
import type { HeadlessFlow, HeadlessView } from "../src/index.js";

function loginResponse(view: HeadlessView = "login") {
  return new Response(
    JSON.stringify({
      view,
      authBasePath: "/sqlos/auth",
      headlessApiBasePath: "/sqlos/auth/headless",
      requestId: "req_1",
      fieldErrors: {},
      organizationSelection: [],
      providers: [],
    }),
    { status: 200, headers: { "Content-Type": "application/json" } },
  );
}

describe("useHeadlessAuth", () => {
  it("exposes snapshot fields that update with the flow", async () => {
    const fetch = vi.fn(async () => loginResponse("login"));
    const { result } = renderHook(() =>
      useHeadlessAuth({
        issuer: "https://id.example.com/sqlos/auth",
        clientId: "acme-app",
        redirectUri: "https://app.example.com/auth/callback",
        fetch,
        credentials: "include",
      }),
    );

    await act(async () => {
      await result.current.flow.resume("https://app.example.com/auth/authorize?request=req_1");
    });

    await waitFor(() => {
      expect(result.current.status).toBe("view");
      expect(result.current.view).toBe("login");
      expect(result.current.viewModel?.requestId).toBe("req_1");
    });
  });

  it("re-renders a memoized child when the view changes", async () => {
    let resolveIdentify: ((value: Response) => void) | undefined;
    const identifyGate = new Promise<Response>((resolve) => {
      resolveIdentify = resolve;
    });

    const fetch = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes("/identify")) {
        return identifyGate;
      }
      return loginResponse("login");
    });

    const renders: string[] = [];
    const MemoChild = memo(function MemoChild({ view }: { view: HeadlessView | null }) {
      renders.push(view ?? "null");
      return <div data-testid="view">{view}</div>;
    });

    let flowRef: HeadlessFlow | null = null;

    function Harness() {
      const { flow, view } = useHeadlessAuth({
        issuer: "https://id.example.com/sqlos/auth",
        clientId: "acme-app",
        redirectUri: "https://app.example.com/auth/callback",
        fetch,
      });
      const started = useRef(false);
      flowRef = flow;

      useEffect(() => {
        if (started.current) return;
        started.current = true;
        void flow.resume("https://app.example.com/auth/authorize?request=req_1");
      }, [flow]);

      return <MemoChild view={view} />;
    }

    render(<Harness />);

    await waitFor(() => {
      expect(renders).toContain("login");
    });
    const beforeIdentify = renders.filter((value) => value === "login").length;

    await act(async () => {
      const pending = flowRef!.identify({ email: "ada@example.com" });
      resolveIdentify?.(
        new Response(
          JSON.stringify({
            type: "view",
            viewModel: {
              view: "password",
              authBasePath: "/sqlos/auth",
              headlessApiBasePath: "/sqlos/auth/headless",
              requestId: "req_1",
              email: "ada@example.com",
              fieldErrors: {},
              organizationSelection: [],
              providers: [],
            },
          }),
          { status: 200, headers: { "Content-Type": "application/json" } },
        ),
      );
      await pending;
    });

    await waitFor(() => {
      expect(renders).toContain("password");
    });
    expect(renders.filter((value) => value === "password").length).toBeGreaterThan(0);
    expect(beforeIdentify).toBeGreaterThan(0);
  });
});

describe("useHeadlessAuth flow identity", () => {
  it("keeps one flow across renders with inline fetch and generatePkce options", async () => {
    const calls: string[] = [];
    let renders = 0;
    const { result, rerender } = renderHook(() => {
      renders += 1;
      const renderIndex = renders;
      return useHeadlessAuth({
        issuer: "https://id.example.com/sqlos/auth",
        clientId: "acme-app",
        redirectUri: "https://app.example.com/auth/callback",
        // Inline on purpose: new function identities every render.
        fetch: async (input) => {
          calls.push(`${renderIndex}:${String(input)}`);
          return loginResponse("login");
        },
        generatePkce: async () => ({ codeVerifier: "v".repeat(43), codeChallenge: "c", codeChallengeMethod: "S256" }),
      });
    });
    const first = result.current.flow;
    rerender();
    rerender();
    expect(result.current.flow).toBe(first);

    await act(async () => {
      await result.current.flow.resume("https://app.example.com/auth/authorize?request=req_1");
    });
    // A later render's fetch ran, not the closure captured when the flow was created.
    expect(calls).toHaveLength(1);
    expect(calls[0]?.startsWith("1:")).toBe(false);
    expect(result.current.view).toBe("login");
  });

  it("rebuilds the flow only when an identity option changes", () => {
    const { result, rerender } = renderHook(
      ({ clientId }: { clientId: string }) =>
        useHeadlessAuth({
          issuer: "https://id.example.com/sqlos/auth",
          clientId,
          redirectUri: "https://app.example.com/auth/callback",
        }),
      { initialProps: { clientId: "acme-app" } },
    );
    const first = result.current.flow;
    rerender({ clientId: "acme-app" });
    expect(result.current.flow).toBe(first);
    rerender({ clientId: "other-app" });
    expect(result.current.flow).not.toBe(first);
  });
});
