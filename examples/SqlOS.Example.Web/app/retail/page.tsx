"use client";

import { useSession } from "next-auth/react";
import { useEffect, useState } from "react";
import Link from "next/link";
import { apiGet } from "@/lib/api";

type PagedResponse<T> = { data: T[]; totalCount: number; hasMore: boolean };
type StoreSummary = {
  id: string;
  chainId: string;
  chainName?: string;
  name: string;
  storeNumber?: string;
  city?: string;
  state?: string;
};

export default function RetailDashboard() {
  const { data: session } = useSession();
  const [stores, setStores] = useState<StoreSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!session?.accessToken) return;
    setLoading(true);
    apiGet<PagedResponse<StoreSummary>>("/api/locations?pageSize=250", session.accessToken)
      .then((r) => setStores(r.data))
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }, [session?.accessToken]);

  const storesByChain = new Map<string, { chainId: string; chainName: string; stores: StoreSummary[] }>();
  for (const store of stores) {
    const key = store.chainId;
    const existing = storesByChain.get(key);
    if (existing) {
      existing.stores.push(store);
      continue;
    }

    storesByChain.set(key, {
      chainId: store.chainId,
      chainName: store.chainName ?? store.chainId,
      stores: [store],
    });
  }

  const groupedChains = Array.from(storesByChain.values())
    .map((group) => ({
      ...group,
      stores: [...group.stores].sort((a, b) => a.name.localeCompare(b.name)),
    }))
    .sort((a, b) => a.chainName.localeCompare(b.chainName));

  const totalChains = groupedChains.length;
  const totalStores = stores.length;

  return (
    <div className="stack">
      <section className="hero">
        <h1>Retail Dashboard</h1>
        <p>
          Logged in as <strong>{session?.user?.name ?? session?.user?.email}</strong>.
          Data shown below is filtered by your authorization grants.
        </p>
      </section>

      {error && <p className="error">{error}</p>}

      {loading ? (
        <p className="muted">Loading...</p>
      ) : (
        <>
          <div className="grid two">
            <Link href="/retail/chains" className="stat-card card">
              <span className="stat-value">{totalChains}</span>
              <span className="stat-label">Chains</span>
            </Link>
            <Link href="/retail/stores" className="stat-card card">
              <span className="stat-value">{totalStores}</span>
              <span className="stat-label">Stores</span>
            </Link>
          </div>

          <section className="card">
            <h2>Your Stores (by Chain)</h2>
            {groupedChains.length === 0 ? (
              <p className="muted">No stores visible with your current permissions.</p>
            ) : (
              <div className="stack">
                {groupedChains.map((group) => (
                  <div key={group.chainId}>
                    <h3>
                      <Link href={`/retail/chains/${group.chainId}`}>{group.chainName}</Link>
                    </h3>
                    <table className="data-table">
                      <thead>
                        <tr>
                          <th>Store</th>
                          <th>Store #</th>
                          <th>City</th>
                          <th>State</th>
                        </tr>
                      </thead>
                      <tbody>
                        {group.stores.map((store) => (
                          <tr key={store.id}>
                            <td>
                              <Link href={`/retail/locations/${store.id}`}>{store.name}</Link>
                            </td>
                            <td>{store.storeNumber ?? "—"}</td>
                            <td>{store.city ?? "—"}</td>
                            <td>{store.state ?? "—"}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                ))}
              </div>
            )}
          </section>
        </>
      )}
    </div>
  );
}
