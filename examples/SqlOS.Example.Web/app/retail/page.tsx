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
type InventoryItemDto = {
  id: string;
  sku: string;
  name: string;
  price: number;
  quantityOnHand: number;
  locationId: string;
};

export default function RetailDashboard() {
  const { data: session } = useSession();
  const [stores, setStores] = useState<StoreSummary[]>([]);
  const [inventoryCount, setInventoryCount] = useState(0);
  const [lowStockCount, setLowStockCount] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!session?.accessToken) return;
    setLoading(true);
    apiGet<PagedResponse<StoreSummary>>("/api/locations?pageSize=250", session.accessToken)
      .then(async (r) => {
        setStores(r.data);
        let totalItems = 0;
        let lowItems = 0;
        const inventoryResults = await Promise.all(
          r.data.map((store) =>
            apiGet<PagedResponse<InventoryItemDto>>(
              `/api/locations/${store.id}/inventory?pageSize=250`,
              session.accessToken!
            ).catch(() => ({ data: [], totalCount: 0, hasMore: false }) as PagedResponse<InventoryItemDto>)
          )
        );
        for (const inv of inventoryResults) {
          totalItems += inv.data.length;
          lowItems += inv.data.filter((i) => i.quantityOnHand <= 10).length;
        }
        setInventoryCount(totalItems);
        setLowStockCount(lowItems);
      })
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
    storesByChain.set(key, { chainId: store.chainId, chainName: store.chainName ?? store.chainId, stores: [store] });
  }

  const groupedChains = Array.from(storesByChain.values())
    .map((g) => ({ ...g, stores: [...g.stores].sort((a, b) => a.name.localeCompare(b.name)) }))
    .sort((a, b) => a.chainName.localeCompare(b.chainName));

  const totalChains = groupedChains.length;
  const totalStores = stores.length;
  const userName = session?.user?.name ?? session?.user?.email ?? "User";

  return (
    <div className="gap-20">
      <div className="page-header">
        <h1>Dashboard</h1>
        <p>Welcome back, <strong>{userName}</strong></p>
      </div>

      {error && <p className="error">{error}</p>}

      <div className="stats-grid">
        <Link href="/retail/chains" className="stat-card stat-card--chains">
          <div className="stat-card-label">Chains</div>
          <div className="stat-card-value">{loading ? "—" : totalChains}</div>
          <div className="stat-card-sub">{loading ? "Loading..." : `${totalChains} visible to you`}</div>
        </Link>
        <Link href="/retail/stores" className="stat-card stat-card--stores">
          <div className="stat-card-label">Stores</div>
          <div className="stat-card-value">{loading ? "—" : totalStores}</div>
          <div className="stat-card-sub">{loading ? "Loading..." : `Across ${totalChains} chain${totalChains !== 1 ? "s" : ""}`}</div>
        </Link>
        <div className="stat-card stat-card--items">
          <div className="stat-card-label">Inventory</div>
          <div className="stat-card-value">{loading ? "—" : inventoryCount}</div>
          <div className="stat-card-sub">
            {loading ? "Loading..." : (
              lowStockCount > 0
                ? <><span className="warn">{lowStockCount} low stock</span> · {inventoryCount - lowStockCount} ok</>
                : "All items stocked"
            )}
          </div>
        </div>
      </div>

      {!loading && groupedChains.length === 0 ? (
        <div className="empty-state">
          <strong>No data visible</strong>
          <p>Your current identity has no authorization grants. Switch to a different user in the sidebar.</p>
        </div>
      ) : !loading && (
        <div className="card">
          <h2>Your Stores</h2>
          {groupedChains.map((group) => (
            <div key={group.chainId} className="chain-group">
              <div className="chain-group-header">
                <Link href={`/retail/chains/${group.chainId}`}>{group.chainName}</Link>
                <span className="badge badge-neutral">{group.stores.length}</span>
              </div>
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
                      <td><Link href={`/retail/locations/${store.id}`}>{store.name}</Link></td>
                      <td className="mono">{store.storeNumber ?? "—"}</td>
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
    </div>
  );
}
