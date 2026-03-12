"use client";

import { useSession } from "next-auth/react";
import { useEffect, useState } from "react";
import { useParams } from "next/navigation";
import Link from "next/link";
import { apiGet, apiPost, apiPut, apiDelete } from "@/lib/api";

type LocationDetail = {
  id: string;
  resourceId: string;
  chainId: string;
  chainName?: string;
  name: string;
  storeNumber?: string;
  address?: string;
  city?: string;
  state?: string;
  zipCode?: string;
  inventoryItemCount: number;
  createdAt: string;
  updatedAt: string;
};

type InventoryItemDto = {
  id: string;
  resourceId: string;
  locationId: string;
  sku: string;
  name: string;
  price: number;
  quantityOnHand: number;
  createdAt: string;
};

type PagedResponse<T> = { data: T[]; totalCount: number; hasMore: boolean };

export default function LocationDetailPage() {
  const { locationId } = useParams<{ locationId: string }>();
  const { data: session } = useSession();
  const [location, setLocation] = useState<LocationDetail | null>(null);
  const [inventory, setInventory] = useState<InventoryItemDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState(false);
  const [editName, setEditName] = useState("");
  const [editNumber, setEditNumber] = useState("");
  const [editAddr, setEditAddr] = useState("");
  const [editCity, setEditCity] = useState("");
  const [editState, setEditState] = useState("");
  const [editZip, setEditZip] = useState("");
  const [saving, setSaving] = useState(false);
  const [showAddItem, setShowAddItem] = useState(false);
  const [newSku, setNewSku] = useState("");
  const [newItemName, setNewItemName] = useState("");
  const [newItemDesc, setNewItemDesc] = useState("");
  const [newPrice, setNewPrice] = useState("");
  const [newQty, setNewQty] = useState("");
  const [addingItem, setAddingItem] = useState(false);
  const [editingItemId, setEditingItemId] = useState<string | null>(null);
  const [editItemName, setEditItemName] = useState("");
  const [editItemDesc, setEditItemDesc] = useState("");
  const [editItemPrice, setEditItemPrice] = useState("");
  const [editItemQty, setEditItemQty] = useState("");

  function loadData() {
    if (!session?.accessToken) return;
    setLoading(true);
    setError(null);
    Promise.all([
      apiGet<LocationDetail>(`/api/locations/${locationId}`, session.accessToken),
      apiGet<PagedResponse<InventoryItemDto>>(`/api/locations/${locationId}/inventory?pageSize=50`, session.accessToken),
    ])
      .then(([loc, inv]) => {
        setLocation(loc);
        setInventory(inv.data);
        setEditName(loc.name);
        setEditNumber(loc.storeNumber ?? "");
        setEditAddr(loc.address ?? "");
        setEditCity(loc.city ?? "");
        setEditState(loc.state ?? "");
        setEditZip(loc.zipCode ?? "");
      })
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }

  useEffect(() => {
    loadData();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session?.accessToken, locationId]);

  async function handleSaveLocation(e: React.FormEvent) {
    e.preventDefault();
    if (!session?.accessToken || !location) return;
    setSaving(true);
    setError(null);
    try {
      const updated = await apiPut<LocationDetail>(`/api/locations/${location.id}`, session.accessToken, {
        name: editName.trim(),
        storeNumber: editNumber.trim() || null,
        address: editAddr.trim() || null,
        city: editCity.trim() || null,
        state: editState.trim() || null,
        zipCode: editZip.trim() || null,
      });
      setLocation(updated);
      setEditing(false);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Update failed");
    } finally {
      setSaving(false);
    }
  }

  async function handleDeleteLocation() {
    if (!session?.accessToken || !location) return;
    if (!confirm(`Delete ${location.name}?`)) return;
    try {
      await apiDelete(`/api/locations/${location.id}`, session.accessToken);
      window.location.href = `/retail/chains/${location.chainId}`;
    } catch (e) {
      setError(e instanceof Error ? e.message : "Delete failed");
    }
  }

  async function handleAddItem(e: React.FormEvent) {
    e.preventDefault();
    if (!session?.accessToken) return;
    setAddingItem(true);
    setError(null);
    try {
      await apiPost(`/api/locations/${locationId}/inventory`, session.accessToken, {
        sku: newSku.trim(),
        name: newItemName.trim(),
        description: newItemDesc.trim() || null,
        price: parseFloat(newPrice) || 0,
        quantityOnHand: parseInt(newQty) || 0,
      });
      setNewSku("");
      setNewItemName("");
      setNewItemDesc("");
      setNewPrice("");
      setNewQty("");
      setShowAddItem(false);
      loadData();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to add item");
    } finally {
      setAddingItem(false);
    }
  }

  function startEditItem(item: InventoryItemDto) {
    setEditingItemId(item.id);
    setEditItemName(item.name);
    setEditItemDesc("");
    setEditItemPrice(item.price.toString());
    setEditItemQty(item.quantityOnHand.toString());
  }

  async function handleSaveItem(itemId: string) {
    if (!session?.accessToken) return;
    setError(null);
    try {
      await apiPut(`/api/inventory/${itemId}`, session.accessToken, {
        name: editItemName.trim(),
        description: editItemDesc.trim() || null,
        price: parseFloat(editItemPrice) || 0,
        quantityOnHand: parseInt(editItemQty) || 0,
      });
      setEditingItemId(null);
      loadData();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Update failed");
    }
  }

  async function handleDeleteItem(itemId: string, name: string) {
    if (!session?.accessToken) return;
    if (!confirm(`Delete ${name}?`)) return;
    try {
      await apiDelete(`/api/inventory/${itemId}`, session.accessToken);
      loadData();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Delete failed");
    }
  }

  if (loading) return <div className="stack"><p className="muted">Loading...</p></div>;
  if (error && !location) return <div className="stack"><p className="error">{error}</p></div>;
  if (!location) return <div className="stack"><p className="muted">Location not found.</p></div>;

  return (
    <div className="stack">
      <nav className="breadcrumb">
        <Link href="/retail/chains">Chains</Link>
        {" / "}
        <Link href={`/retail/chains/${location.chainId}`}>{location.chainName ?? "Chain"}</Link>
        {" / "}
        {location.name}
      </nav>

      {error && <p className="error">{error}</p>}

      <section className="card">
        {editing ? (
          <form onSubmit={(e) => void handleSaveLocation(e)} className="create-form">
            <h2>Edit Location</h2>
            <input type="text" value={editName} onChange={(e) => setEditName(e.target.value)} required />
            <input type="text" placeholder="Store #" value={editNumber} onChange={(e) => setEditNumber(e.target.value)} />
            <input type="text" placeholder="Address" value={editAddr} onChange={(e) => setEditAddr(e.target.value)} />
            <div className="form-row">
              <input type="text" placeholder="City" value={editCity} onChange={(e) => setEditCity(e.target.value)} />
              <input type="text" placeholder="State" value={editState} onChange={(e) => setEditState(e.target.value)} />
              <input type="text" placeholder="Zip" value={editZip} onChange={(e) => setEditZip(e.target.value)} />
            </div>
            <div className="actions">
              <button type="submit" disabled={saving}>{saving ? "Saving..." : "Save"}</button>
              <button type="button" className="secondary" onClick={() => setEditing(false)}>Cancel</button>
            </div>
          </form>
        ) : (
          <>
            <h2>{location.name}</h2>
            <p className="muted">
              Store #{location.storeNumber ?? "—"} &middot;{" "}
              {[location.address, location.city, location.state, location.zipCode].filter(Boolean).join(", ") || "No address"}
            </p>
            <p className="muted">
              Chain: {location.chainName ?? location.chainId} &middot;{" "}
              {location.inventoryItemCount} item{location.inventoryItemCount !== 1 ? "s" : ""}
            </p>
            <div className="actions">
              <button type="button" className="secondary" onClick={() => setEditing(true)}>Edit</button>
              <button type="button" className="secondary danger" onClick={() => void handleDeleteLocation()}>Delete</button>
            </div>
          </>
        )}
      </section>

      <section className="card">
        <div className="toolbar">
          <h3>Inventory</h3>
          <button type="button" className="secondary" onClick={() => setShowAddItem(!showAddItem)}>
            {showAddItem ? "Cancel" : "Add Item"}
          </button>
        </div>

        {showAddItem && (
          <form onSubmit={(e) => void handleAddItem(e)} className="create-form">
            <div className="form-row">
              <input type="text" placeholder="SKU" value={newSku} onChange={(e) => setNewSku(e.target.value)} required />
              <input type="text" placeholder="Name" value={newItemName} onChange={(e) => setNewItemName(e.target.value)} required />
            </div>
            <input type="text" placeholder="Description (optional)" value={newItemDesc} onChange={(e) => setNewItemDesc(e.target.value)} />
            <div className="form-row">
              <input type="number" step="0.01" placeholder="Price" value={newPrice} onChange={(e) => setNewPrice(e.target.value)} required />
              <input type="number" placeholder="Quantity" value={newQty} onChange={(e) => setNewQty(e.target.value)} required />
            </div>
            <button type="submit" disabled={addingItem}>{addingItem ? "Adding..." : "Add"}</button>
          </form>
        )}

        {inventory.length === 0 ? (
          <p className="muted">No inventory items.</p>
        ) : (
          <table className="data-table">
            <thead>
              <tr>
                <th>SKU</th>
                <th>Name</th>
                <th>Price</th>
                <th>Qty</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {inventory.map((item) => (
                <tr key={item.id}>
                  {editingItemId === item.id ? (
                    <>
                      <td>{item.sku}</td>
                      <td>
                        <input
                          type="text"
                          value={editItemName}
                          onChange={(e) => setEditItemName(e.target.value)}
                          className="inline-input"
                        />
                      </td>
                      <td>
                        <input
                          type="number"
                          step="0.01"
                          value={editItemPrice}
                          onChange={(e) => setEditItemPrice(e.target.value)}
                          className="inline-input"
                        />
                      </td>
                      <td>
                        <input
                          type="number"
                          value={editItemQty}
                          onChange={(e) => setEditItemQty(e.target.value)}
                          className="inline-input"
                        />
                      </td>
                      <td className="row-actions">
                        <button type="button" onClick={() => void handleSaveItem(item.id)}>Save</button>
                        <button type="button" className="secondary" onClick={() => setEditingItemId(null)}>Cancel</button>
                      </td>
                    </>
                  ) : (
                    <>
                      <td className="muted">{item.sku}</td>
                      <td>{item.name}</td>
                      <td>${item.price.toFixed(2)}</td>
                      <td>{item.quantityOnHand}</td>
                      <td className="row-actions">
                        <button type="button" className="secondary" onClick={() => startEditItem(item)}>Edit</button>
                        <button type="button" className="secondary danger" onClick={() => void handleDeleteItem(item.id, item.name)}>Del</button>
                      </td>
                    </>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  );
}
