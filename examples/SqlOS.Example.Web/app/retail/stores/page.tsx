"use client";

import { useSession } from "next-auth/react";
import { useEffect, useState } from "react";
import Link from "next/link";
import { apiGet } from "@/lib/api";

type PagedResponse<T> = { data: T[]; totalCount: number; hasMore: boolean };
type LocationDto = { id: string; resourceId: string; chainId: string; chainName?: string; name: string; storeNumber?: string; city?: string; state?: string; createdAt: string };

export default function StoresPage() {
  const { data: session } = useSession();
  const [stores, setStores] = useState<LocationDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [search, setSearch] = useState("");

  useEffect(() => {
    if (!session?.accessToken) return;
    setLoading(true);
    const params = new URLSearchParams({ pageSize: "50" });
    if (search) params.set("search", search);
    apiGet<PagedResponse<LocationDto>>(`/api/locations?${params}`, session.accessToken)
      .then((r) => setStores(r.data))
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }, [session?.accessToken, search]);

  return (
    <div className="gap-20">
      <div className="page-header">
        <h1>Stores</h1>
        <p>All store locations visible to your account.</p>
      </div>

      {error && <p className="error">{error}</p>}

      <div className="card">
        <div className="toolbar">
          <input type="text" placeholder="Search stores..." value={search} onChange={(e) => setSearch(e.target.value)} className="toolbar-search" />
          <span className="badge badge-neutral">{loading ? "..." : `${stores.length} store${stores.length !== 1 ? "s" : ""}`}</span>
        </div>

        {loading ? (
          <p className="muted" style={{ marginTop: 16 }}>Loading...</p>
        ) : stores.length === 0 ? (
          <div className="empty-state">
            <strong>No stores found</strong>
            <p>{search ? "Try a different search term." : "No stores visible with your current permissions."}</p>
          </div>
        ) : (
          <table className="data-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Store #</th>
                <th>Chain</th>
                <th>City</th>
                <th>State</th>
              </tr>
            </thead>
            <tbody>
              {stores.map((s) => (
                <tr key={s.id}>
                  <td><Link href={`/retail/locations/${s.id}`}>{s.name}</Link></td>
                  <td className="mono">{s.storeNumber ?? "—"}</td>
                  <td><Link href={`/retail/chains/${s.chainId}`} className="muted">{s.chainName ?? s.chainId}</Link></td>
                  <td>{s.city ?? "—"}</td>
                  <td>{s.state ?? "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
