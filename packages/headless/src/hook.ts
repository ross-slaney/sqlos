import { useEffect, useMemo, useState } from "react";
import { createHeadlessFlow } from "./flow.js";
import type { CreateHeadlessFlowOptions, HeadlessFlow } from "./types.js";

export function useHeadlessAuth(options: CreateHeadlessFlowOptions): HeadlessFlow {
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
  const [, setVersion] = useState(0);

  useEffect(() => {
    return flow.subscribe(() => {
      setVersion((value) => value + 1);
    });
  }, [flow]);

  return flow;
}
