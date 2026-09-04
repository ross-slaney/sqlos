import { useCallback, useMemo, useRef, useSyncExternalStore } from "react";
import { HeadlessError } from "./errors.js";
import { createHeadlessFlow } from "./flow.js";
import { generatePkce as defaultGeneratePkce } from "./pkce.js";
import type { HeadlessView } from "./contract.js";
import type {
  CreateHeadlessFlowOptions,
  HeadlessAuthorization,
  HeadlessFlow,
  HeadlessFlowStatus,
  HeadlessPasswordResetRequestResult,
  HeadlessViewModel,
  UseHeadlessAuthResult,
} from "./types.js";

type Snapshot = {
  status: HeadlessFlowStatus;
  view: HeadlessView | null;
  viewModel: HeadlessViewModel | null;
  error: string | null;
  fieldErrors: Record<string, string>;
  authorization: HeadlessAuthorization | null;
  redirectUrl: string | null;
  passwordReset: HeadlessPasswordResetRequestResult | null;
};

const idleSnapshot: Snapshot = {
  status: "idle",
  view: null,
  viewModel: null,
  error: null,
  fieldErrors: {},
  authorization: null,
  redirectUrl: null,
  passwordReset: null,
};

function readSnapshot(flow: HeadlessFlow): Snapshot {
  return {
    status: flow.status,
    view: flow.viewModel?.view ?? null,
    viewModel: flow.viewModel,
    error: flow.error,
    fieldErrors: flow.fieldErrors,
    authorization: flow.authorization,
    redirectUrl: flow.redirectUrl,
    passwordReset: flow.passwordReset,
  };
}

function sameSnapshot(left: Snapshot, right: Snapshot): boolean {
  return (
    left.status === right.status &&
    left.view === right.view &&
    left.viewModel === right.viewModel &&
    left.error === right.error &&
    left.fieldErrors === right.fieldErrors &&
    left.authorization === right.authorization &&
    left.redirectUrl === right.redirectUrl &&
    left.passwordReset === right.passwordReset
  );
}

/**
 * One flow per authorization request. The flow is rebuilt only when an
 * identity option (issuer, clientId, redirectUri, headlessApiBasePath,
 * credentials) changes; `fetch` and `generatePkce` may be inline functions —
 * the latest values are read at call time.
 */
export function useHeadlessAuth(options: CreateHeadlessFlowOptions): UseHeadlessAuthResult {
  const latest = useRef(options);
  latest.current = options;

  const { issuer, clientId, redirectUri, headlessApiBasePath, credentials } = options;
  const flow = useMemo(
    () =>
      createHeadlessFlow({
        issuer,
        clientId,
        redirectUri,
        headlessApiBasePath,
        credentials,
        fetch: (input, init) => {
          const impl = latest.current.fetch ?? globalThis.fetch;
          if (typeof impl !== "function") {
            throw new HeadlessError("fetch is not available. Pass `fetch` to useHeadlessAuth.");
          }
          return impl(input, init);
        },
        generatePkce: () => (latest.current.generatePkce ?? defaultGeneratePkce)(),
      }),
    [issuer, clientId, redirectUri, headlessApiBasePath, credentials],
  );

  const cacheRef = useRef<Snapshot>(idleSnapshot);

  const subscribe = useCallback(
    (onStoreChange: () => void) => flow.subscribe(onStoreChange),
    [flow],
  );

  const getSnapshot = useCallback(() => {
    const next = readSnapshot(flow);
    if (sameSnapshot(cacheRef.current, next)) {
      return cacheRef.current;
    }
    cacheRef.current = next;
    return next;
  }, [flow]);

  const getServerSnapshot = useCallback(() => idleSnapshot, []);

  const snapshot = useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);

  return {
    flow,
    status: snapshot.status,
    view: snapshot.view,
    viewModel: snapshot.viewModel,
    error: snapshot.error,
    fieldErrors: snapshot.fieldErrors,
    authorization: snapshot.authorization,
    redirectUrl: snapshot.redirectUrl,
    passwordReset: snapshot.passwordReset,
  };
}
