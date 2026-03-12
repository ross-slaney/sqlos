"use client";

import { useSession } from "next-auth/react";
import { useEffect, useState } from "react";
import { useParams } from "next/navigation";
import Link from "next/link";
import { apiGet, apiPost, apiPut, apiDelete } from "@/lib/api";

type ChainDetail = {
  id: string;
  resourceId: string;
  name: string;
  description?: string;
  headquartersAddress?: string;
  locationCount: number;
  createdAt: string;
  updatedAt: string;
};

type LocationDto = {
  id: string;
  resourceId: string;
  chainId: string;
  name: string;
  storeNumber?: string;
  city?: string;
  state?: string;
  createdAt: string;
};

type PagedResponse<T> = { data: T[]; totalCount: number; hasMore: boolean };

export default function ChainDetailPage() {
  const { chainId } = useParams<{ chainId: string }>();
  const { data: session } = useSession();
  const [chain, setChain] = useState<ChainDetail | null>(null);
  const [locations, setLocations] = useState<LocationDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState(false);
  const [editName, setEditName] = useState("");
  const [editDesc, setEditDesc] = useState("");
  const [editHq, setEditHq] = useState("");
  const [saving, setSaving] = useState(false);
  const [showAddLoc, setShowAddLoc] = useState(false);
  const [newLocName, setNewLocName] = useState("");
  const [newLocNumber, setNewLocNumber] = useState("");
  const [newLocAddress, setNewLocAddress] = useState("");
  const [newLocCity, setNewLocCity] = useState("");
  const [newLocState, setNewLocState] = useState("");
  const [newLocZip, setNewLocZip] = useState("");
  const [addingLoc, setAddingLoc] = useState(false);

  function loadData() {
    if (!session?.accessToken) return;
    setLoading(true);
    setError(null);
    Promise.all([
      apiGet<ChainDetail>(`/api/chains/${chainId}`, session.accessToken),
      apiGet<PagedResponse<LocationDto>>(`/api/chains/${chainId}/locations?pageSize=50`, session.accessToken),
    ])
      .then(([c, locs]) => {
        setChain(c);
        setLocations(locs.data);
        setEditName(c.name);
        setEditDesc(c.description ?? "");
        setEditHq(c.headquartersAddress ?? "");
      })
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }

  useEffect(() => {
    loadData();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session?.accessToken, chainId]);

  async function handleSave(e: React.FormEvent) {
    e.preventDefault();
    if (!session?.accessToken || !chain) return;
    setSaving(true);
    setError(null);
    try {
      const updated = await apiPut<ChainDetail>(`/api/chains/${chain.id}`, session.accessToken, {
        name: editName.trim(),
        description: editDesc.trim() || null,
        headquartersAddress: editHq.trim() || null,
      });
      setChain(updated);
      setEditing(false);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Update failed");
    } finally {
      setSaving(false);
    }
  }

  async function handleDelete() {
    if (!session?.accessToken || !chain) return;
    if (!confirm(`Delete ${chain.name}? This cannot be undone.`)) return;
    try {
      await apiDelete(`/api/chains/${chain.id}`, session.accessToken);
      window.location.href = "/retail/chains";
    } catch (e) {
      setError(e instanceof Error ? e.message : "Delete failed");
    }
  }

  async function handleAddLocation(e: React.FormEvent) {
    e.preventDefault();
    if (!session?.accessToken || !newLocName.trim()) return;
    setAddingLoc(true);
    setError(null);
    try {
      await apiPost(`/api/chains/${chainId}/locations`, session.accessToken, {
        name: newLocName.trim(),
        storeNumber: newLocNumber.trim() || null,
        address: newLocAddress.trim() || null,
        city: newLocCity.trim() || null,
        state: newLocState.trim() || null,
        zipCode: newLocZip.trim() || null,
      });
      setNewLocName("");
      setNewLocNumber("");
      setNewLocAddress("");
      setNewLocCity("");
      setNewLocState("");
      setNewLocZip("");
      setShowAddLoc(false);
      loadData();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to add location");
    } finally {
      setAddingLoc(false);
    }
  }

  if (loading) return <div className="stack"><p className="muted">Loading...</p></div>;
  if (error && !chain) return <div className="stack"><p className="error">{error}</p></div>;
  if (!chain) return <div className="stack"><p className="muted">Chain not found.</p></div>;

  return (
    <div className="stack">
      <nav className="breadcrumb">
        <Link href="/retail/chains">Chains</Link> / {chain.name}
      </nav>

      {error && <p className="error">{error}</p>}

      <section className="card">
        {editing ? (
          <form onSubmit={(e) => void handleSave(e)} className="create-form">
            <h2>Edit Chain</h2>
            <input type="text" value={editName} onChange={(e) => setEditName(e.target.value)} required />
            <input type="text" placeholder="Description" value={editDesc} onChange={(e) => setEditDesc(e.target.value)} />
            <input type="text" placeholder="HQ Address" value={editHq} onChange={(e) => setEditHq(e.target.value)} />
            <div className="actions">
              <button type="submit" disabled={saving}>{saving ? "Saving..." : "Save"}</button>
              <button type="button" className="secondary" onClick={() => setEditing(false)}>Cancel</button>
            </div>
          </form>
        ) : (
          <>
            <h2>{chain.name}</h2>
            {chain.description && <p className="muted">{chain.description}</p>}
            {chain.headquartersAddress && <p className="muted">HQ: {chain.headquartersAddress}</p>}
            <p className="muted">
              {chain.locationCount} location{chain.locationCount !== 1 ? "s" : ""}
              {" "}&middot; Created {new Date(chain.createdAt).toLocaleDateString()}
            </p>
            <div className="actions">
              <button type="button" className="secondary" onClick={() => setEditing(true)}>Edit</button>
              <button type="button" className="secondary danger" onClick={() => void handleDelete()}>Delete</button>
            </div>
          </>
        )}
      </section>

      <section className="card">
        <div className="toolbar">
          <h3>Locations</h3>
          <button type="button" className="secondary" onClick={() => setShowAddLoc(!showAddLoc)}>
            {showAddLoc ? "Cancel" : "Add Location"}
          </button>
        </div>

        {showAddLoc && (
          <form onSubmit={(e) => void handleAddLocation(e)} className="create-form">
            <input type="text" placeholder="Location name" value={newLocName} onChange={(e) => setNewLocName(e.target.value)} required />
            <input type="text" placeholder="Store number" value={newLocNumber} onChange={(e) => setNewLocNumber(e.target.value)} />
            <input type="text" placeholder="Address" value={newLocAddress} onChange={(e) => setNewLocAddress(e.target.value)} />
            <div className="form-row">
              <input type="text" placeholder="City" value={newLocCity} onChange={(e) => setNewLocCity(e.target.value)} />
              <input type="text" placeholder="State" value={newLocState} onChange={(e) => setNewLocState(e.target.value)} />
              <input type="text" placeholder="Zip" value={newLocZip} onChange={(e) => setNewLocZip(e.target.value)} />
            </div>
            <button type="submit" disabled={addingLoc}>{addingLoc ? "Adding..." : "Add"}</button>
          </form>
        )}

        {locations.length === 0 ? (
          <p className="muted">No locations in this chain.</p>
        ) : (
          <table className="data-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Store #</th>
                <th>City</th>
                <th>State</th>
              </tr>
            </thead>
            <tbody>
              {locations.map((loc) => (
                <tr key={loc.id}>
                  <td>
                    <Link href={`/retail/locations/${loc.id}`}>{loc.name}</Link>
                  </td>
                  <td>{loc.storeNumber ?? "—"}</td>
                  <td>{loc.city ?? "—"}</td>
                  <td>{loc.state ?? "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  );
}
