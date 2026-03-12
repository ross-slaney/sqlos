"use client";

import { useSession } from "next-auth/react";
import { useEffect, useState } from "react";
import Link from "next/link";
import { apiGet, apiPost } from "@/lib/api";

type PagedResponse<T> = { data: T[]; totalCount: number; hasMore: boolean; cursor?: string };
type ChainDto = {
  id: string;
  resourceId: string;
  name: string;
  description?: string;
  locationCount: number;
  createdAt: string;
};

export default function ChainsPage() {
  const { data: session } = useSession();
  const [chains, setChains] = useState<ChainDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [showCreate, setShowCreate] = useState(false);
  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState("");
  const [newDesc, setNewDesc] = useState("");
  const [newHq, setNewHq] = useState("");

  function loadChains() {
    if (!session?.accessToken) return;
    setLoading(true);
    const params = new URLSearchParams({ pageSize: "50" });
    if (search) params.set("search", search);
    apiGet<PagedResponse<ChainDto>>(`/api/chains?${params}`, session.accessToken)
      .then((r) => setChains(r.data))
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }

  useEffect(() => {
    loadChains();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session?.accessToken, search]);

  async function handleCreate(e: React.FormEvent) {
    e.preventDefault();
    if (!session?.accessToken || !newName.trim()) return;
    setCreating(true);
    setError(null);
    try {
      await apiPost("/api/chains", session.accessToken, {
        name: newName.trim(),
        description: newDesc.trim() || null,
        headquartersAddress: newHq.trim() || null,
      });
      setNewName("");
      setNewDesc("");
      setNewHq("");
      setShowCreate(false);
      loadChains();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Create failed");
    } finally {
      setCreating(false);
    }
  }

  return (
    <div className="stack">
      <section className="hero">
        <h1>Chains</h1>
        <p>Manage retail chains visible to your account.</p>
      </section>

      {error && <p className="error">{error}</p>}

      <section className="card">
        <div className="toolbar">
          <input
            type="text"
            placeholder="Search chains..."
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            className="toolbar-search"
          />
          <button type="button" onClick={() => setShowCreate(!showCreate)} className="secondary">
            {showCreate ? "Cancel" : "Create Chain"}
          </button>
        </div>

        {showCreate && (
          <form onSubmit={(e) => void handleCreate(e)} className="create-form">
            <input
              type="text"
              placeholder="Chain name"
              value={newName}
              onChange={(e) => setNewName(e.target.value)}
              required
            />
            <input
              type="text"
              placeholder="Description (optional)"
              value={newDesc}
              onChange={(e) => setNewDesc(e.target.value)}
            />
            <input
              type="text"
              placeholder="Headquarters address (optional)"
              value={newHq}
              onChange={(e) => setNewHq(e.target.value)}
            />
            <button type="submit" disabled={creating}>
              {creating ? "Creating..." : "Create"}
            </button>
          </form>
        )}

        {loading ? (
          <p className="muted">Loading...</p>
        ) : chains.length === 0 ? (
          <p className="muted">No chains found.</p>
        ) : (
          <table className="data-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Description</th>
                <th>Locations</th>
              </tr>
            </thead>
            <tbody>
              {chains.map((c) => (
                <tr key={c.id}>
                  <td>
                    <Link href={`/retail/chains/${c.id}`}>{c.name}</Link>
                  </td>
                  <td className="muted">{c.description ?? "—"}</td>
                  <td>{c.locationCount}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  );
}
