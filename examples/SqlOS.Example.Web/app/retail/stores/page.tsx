"use client";

import { useSession } from "next-auth/react";
import { useEffect, useState } from "react";
import Link from "next/link";
import { apiGet } from "@/lib/api";

type PagedResponse<T> = { data: T[]; totalCount: number; hasMore: boolean };
type LocationDto = {
  id: string;
  resourceId: string;
  chainId: string;
  chainName?: string;
  name: string;
  storeNumber?: string;
  city?: string;
  state?: string;
  createdAt: string;
};

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
    <div className="stack">
      <section className="hero">
        <h1>Stores</h1>
        <p>All store locations visible to your account, across all chains.</p>
      </section>

      {error && <p className="error">{error}</p>}

      <section className="card">
        <div className="toolbar">
          <input
            type="text"
            placeholder="Search stores..."
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            className="toolbar-search"
          />
        </div>

        {loading ? (
          <p className="muted">Loading...</p>
        ) : stores.length === 0 ? (
          <p className="muted">No stores found.</p>
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
                  <td>
                    <Link href={`/retail/locations/${s.id}`}>{s.name}</Link>
                  </td>
                  <td>{s.storeNumber ?? "—"}</td>
                  <td>
                    <Link href={`/retail/chains/${s.chainId}`}>{s.chainName ?? s.chainId}</Link>
                  </td>
                  <td>{s.city ?? "—"}</td>
                  <td>{s.state ?? "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  );
}
