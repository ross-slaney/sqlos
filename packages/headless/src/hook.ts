import { useCallback, useMemo, useRef, useSyncExternalStore } from "react";
import { createHeadlessFlow } from "./flow.js";
import type { HeadlessView } from "./contract.js";
import type {
  CreateHeadlessFlowOptions,
  HeadlessAuthorization,
  HeadlessFlow,
  HeadlessFlowStatus,
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
};

const idleSnapshot: Snapshot = {
  status: "idle",
  view: null,
  viewModel: null,
  error: null,
  fieldErrors: {},
  authorization: null,
  redirectUrl: null,
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
    left.redirectUrl === right.redirectUrl
  );
}

export function useHeadlessAuth(options: CreateHeadlessFlowOptions): UseHeadlessAuthResult {
  const flow = useMemo(
    () =>
      createHeadlessFlow({
        issuer: options.issuer,
        clientId: options.clientId,
        redirectUri: options.redirectUri,
        credentials: options.credentials,
        fetch: options.fetch,
        generatePkce: options.generatePkce,
        headlessApiBasePath: options.headlessApiBasePath,
      }),
    [
      options.issuer,
      options.clientId,
      options.redirectUri,
      options.credentials,
      options.fetch,
      options.generatePkce,
      options.headlessApiBasePath,
    ],
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
  };
}
