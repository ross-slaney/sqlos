"use client";

import { useSession } from "next-auth/react";
import { useEffect, useState } from "react";
import Link from "next/link";
import { apiGet } from "@/lib/api";

type PagedResponse<T> = { data: T[]; totalCount: number; hasMore: boolean };
type ChainSummary = { id: string; name: string; locationCount: number };

export default function RetailDashboard() {
  const { data: session } = useSession();
  const [chains, setChains] = useState<ChainSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!session?.accessToken) return;
    setLoading(true);
    apiGet<PagedResponse<ChainSummary>>("/api/chains?pageSize=50", session.accessToken)
      .then((r) => setChains(r.data))
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }, [session?.accessToken]);

  const totalLocations = chains.reduce((sum, c) => sum + c.locationCount, 0);

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
              <span className="stat-value">{chains.length}</span>
              <span className="stat-label">Chains</span>
            </Link>
            <Link href="/retail/stores" className="stat-card card">
              <span className="stat-value">{totalLocations}</span>
              <span className="stat-label">Stores</span>
            </Link>
          </div>

          <section className="card">
            <h2>Your Chains</h2>
            {chains.length === 0 ? (
              <p className="muted">No chains visible with your current permissions.</p>
            ) : (
              <table className="data-table">
                <thead>
                  <tr>
                    <th>Name</th>
                    <th>Locations</th>
                  </tr>
                </thead>
                <tbody>
                  {chains.map((c) => (
                    <tr key={c.id}>
                      <td>
                        <Link href={`/retail/chains/${c.id}`}>{c.name}</Link>
                      </td>
                      <td>{c.locationCount}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </section>
        </>
      )}
    </div>
  );
}
