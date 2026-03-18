"use client";

import { useSession } from "next-auth/react";
import { useEffect, useState } from "react";
import { useParams } from "next/navigation";
import Link from "next/link";
import { apiGet, apiPost, apiPut, apiDelete } from "@/lib/api";

type LocationDetail = { id: string; resourceId: string; chainId: string; chainName?: string; name: string; storeNumber?: string; address?: string; city?: string; state?: string; zipCode?: string; inventoryItemCount: number; createdAt: string; updatedAt: string };
type InventoryItemDto = { id: string; resourceId: string; locationId: string; sku: string; name: string; price: number; quantityOnHand: number; createdAt: string };
type PagedResponse<T> = { data: T[]; totalCount: number; hasMore: boolean };

function stockLevel(qty: number) {
  if (qty === 0) return "out";
  if (qty <= 10) return "low";
  return "ok";
}

function stockLabel(qty: number) {
  if (qty === 0) return "Out of stock";
  if (qty <= 10) return "Low stock";
  return "In stock";
}

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
    setSaving(true); setError(null);
    try {
      const updated = await apiPut<LocationDetail>(`/api/locations/${location.id}`, session.accessToken, { name: editName.trim(), storeNumber: editNumber.trim() || null, address: editAddr.trim() || null, city: editCity.trim() || null, state: editState.trim() || null, zipCode: editZip.trim() || null });
      setLocation(updated);
      setEditing(false);
    } catch (e) { setError(e instanceof Error ? e.message : "Update failed"); }
    finally { setSaving(false); }
  }

  async function handleDeleteLocation() {
    if (!session?.accessToken || !location) return;
    if (!confirm(`Delete ${location.name}? This cannot be undone.`)) return;
    try { await apiDelete(`/api/locations/${location.id}`, session.accessToken); window.location.href = `/retail/chains/${location.chainId}`; }
    catch (e) { setError(e instanceof Error ? e.message : "Delete failed"); }
  }

  async function handleAddItem(e: React.FormEvent) {
    e.preventDefault();
    if (!session?.accessToken) return;
    setAddingItem(true); setError(null);
    try {
      await apiPost(`/api/locations/${locationId}/inventory`, session.accessToken, { sku: newSku.trim(), name: newItemName.trim(), description: newItemDesc.trim() || null, price: parseFloat(newPrice) || 0, quantityOnHand: parseInt(newQty) || 0 });
      setNewSku(""); setNewItemName(""); setNewItemDesc(""); setNewPrice(""); setNewQty("");
      setShowAddItem(false);
      loadData();
    } catch (e) { setError(e instanceof Error ? e.message : "Failed to add item"); }
    finally { setAddingItem(false); }
  }

  function startEditItem(item: InventoryItemDto) {
    setEditingItemId(item.id);
    setEditItemName(item.name); setEditItemDesc(""); setEditItemPrice(item.price.toString()); setEditItemQty(item.quantityOnHand.toString());
  }

  async function handleRestock(item: InventoryItemDto) {
    if (!session?.accessToken) return;
    try { await apiPut(`/api/inventory/${item.id}`, session.accessToken, { name: item.name, price: item.price, quantityOnHand: item.quantityOnHand + 50 }); loadData(); }
    catch (e) { setError(e instanceof Error ? e.message : "Restock failed"); }
  }

  async function handleSaveItem(itemId: string) {
    if (!session?.accessToken) return;
    try { await apiPut(`/api/inventory/${itemId}`, session.accessToken, { name: editItemName.trim(), description: editItemDesc.trim() || null, price: parseFloat(editItemPrice) || 0, quantityOnHand: parseInt(editItemQty) || 0 }); setEditingItemId(null); loadData(); }
    catch (e) { setError(e instanceof Error ? e.message : "Update failed"); }
  }

  async function handleDeleteItem(itemId: string, name: string) {
    if (!session?.accessToken) return;
    if (!confirm(`Delete ${name}?`)) return;
    try { await apiDelete(`/api/inventory/${itemId}`, session.accessToken); loadData(); }
    catch (e) { setError(e instanceof Error ? e.message : "Delete failed"); }
  }

  if (loading) return <div className="gap-20"><p className="muted">Loading...</p></div>;
  if (error && !location) {
    const is403 = error.includes("403");
    return (
      <div className="gap-20">
        <div className="empty-state">
          <strong>{is403 ? "Access Denied" : "Error"}</strong>
          <p>{is403 ? "You don't have permission to view this location. Your current role may not include access to this store." : error}</p>
        </div>
      </div>
    );
  }
  if (!location) return <div className="gap-20"><p className="muted">Location not found.</p></div>;

  const addressParts = [location.address, location.city, location.state, location.zipCode].filter(Boolean);
  const totalValue = inventory.reduce((sum, i) => sum + i.price * i.quantityOnHand, 0);
  const maxQty = Math.max(...inventory.map((i) => i.quantityOnHand), 100);

  return (
    <div className="gap-20">
      <nav className="breadcrumb">
        <Link href="/retail/chains">Chains</Link>
        <span>/</span>
        <Link href={`/retail/chains/${location.chainId}`}>{location.chainName ?? "Chain"}</Link>
        <span>/</span>
        <span style={{ color: "var(--color-text)" }}>{location.name}</span>
      </nav>

      {error && <p className="error">{error}</p>}

      <div className="card">
        {editing ? (
          <form onSubmit={(e) => void handleSaveLocation(e)} className="create-form" style={{ margin: 0 }}>
            <h2>Edit Location</h2>
            <input type="text" placeholder="Store name" value={editName} onChange={(e) => setEditName(e.target.value)} required />
            <input type="text" placeholder="Store number" value={editNumber} onChange={(e) => setEditNumber(e.target.value)} />
            <input type="text" placeholder="Address" value={editAddr} onChange={(e) => setEditAddr(e.target.value)} />
            <div className="form-row">
              <input type="text" placeholder="City" value={editCity} onChange={(e) => setEditCity(e.target.value)} />
              <input type="text" placeholder="State" value={editState} onChange={(e) => setEditState(e.target.value)} />
              <input type="text" placeholder="Zip" value={editZip} onChange={(e) => setEditZip(e.target.value)} />
            </div>
            <div className="actions" style={{ marginTop: 4 }}>
              <button type="submit" disabled={saving}>{saving ? "Saving..." : "Save"}</button>
              <button type="button" className="secondary" onClick={() => setEditing(false)}>Cancel</button>
            </div>
          </form>
        ) : (
          <>
            <div className="card-header">
              <div>
                <h2 style={{ marginBottom: 0 }}>{location.name}</h2>
                <p className="muted" style={{ fontSize: 13, marginTop: 2 }}>
                  {location.storeNumber && <><span className="mono">#{location.storeNumber}</span> · </>}
                  {addressParts.length > 0 ? addressParts.join(", ") : "No address on file"}
                </p>
              </div>
              <div className="card-header-actions">
                <button type="button" className="secondary sm" onClick={() => setEditing(true)}>Edit</button>
                <button type="button" className="danger sm" onClick={() => void handleDeleteLocation()}>Delete</button>
              </div>
            </div>
            <div className="detail-grid">
              <div className="detail-item">
                <span>Items</span>
                <strong>{inventory.length}</strong>
              </div>
              <div className="detail-item">
                <span>Inventory Value</span>
                <strong>${totalValue.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}</strong>
              </div>
            </div>
          </>
        )}
      </div>

      <div className="card">
        <div className="toolbar">
          <h3>Inventory</h3>
          <button type="button" className="secondary sm" onClick={() => setShowAddItem(!showAddItem)}>
            {showAddItem ? "Cancel" : "+ Add Item"}
          </button>
        </div>

        {showAddItem && (
          <form onSubmit={(e) => void handleAddItem(e)} className="create-form">
            <div className="form-row">
              <input type="text" placeholder="SKU" value={newSku} onChange={(e) => setNewSku(e.target.value)} required />
              <input type="text" placeholder="Item name" value={newItemName} onChange={(e) => setNewItemName(e.target.value)} required />
            </div>
            <input type="text" placeholder="Description (optional)" value={newItemDesc} onChange={(e) => setNewItemDesc(e.target.value)} />
            <div className="form-row">
              <input type="number" step="0.01" placeholder="Price" value={newPrice} onChange={(e) => setNewPrice(e.target.value)} required />
              <input type="number" placeholder="Quantity" value={newQty} onChange={(e) => setNewQty(e.target.value)} required />
            </div>
            <button type="submit" disabled={addingItem}>{addingItem ? "Adding..." : "Add Item"}</button>
          </form>
        )}

        {inventory.length === 0 ? (
          <div className="empty-state">
            <strong>No inventory</strong>
            <p>Add items to start tracking inventory at this location.</p>
          </div>
        ) : (
          <table className="data-table">
            <thead>
              <tr>
                <th>SKU</th>
                <th>Name</th>
                <th className="text-right">Price</th>
                <th>Stock</th>
                <th>Status</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {inventory.map((item) => {
                const level = stockLevel(item.quantityOnHand);
                return (
                  <tr key={item.id}>
                    {editingItemId === item.id ? (
                      <>
                        <td className="mono">{item.sku}</td>
                        <td><input type="text" value={editItemName} onChange={(e) => setEditItemName(e.target.value)} className="inline-input" /></td>
                        <td><input type="number" step="0.01" value={editItemPrice} onChange={(e) => setEditItemPrice(e.target.value)} className="inline-input" /></td>
                        <td><input type="number" value={editItemQty} onChange={(e) => setEditItemQty(e.target.value)} className="inline-input" /></td>
                        <td></td>
                        <td className="row-actions">
                          <button type="button" className="sm" onClick={() => void handleSaveItem(item.id)}>Save</button>
                          <button type="button" className="secondary sm" onClick={() => setEditingItemId(null)}>Cancel</button>
                        </td>
                      </>
                    ) : (
                      <>
                        <td className="mono">{item.sku}</td>
                        <td style={{ fontWeight: 500 }}>{item.name}</td>
                        <td className="text-right" style={{ fontVariantNumeric: "tabular-nums" }}>${item.price.toFixed(2)}</td>
                        <td>
                          <div className="stock-bar-container">
                            <span className={`stock-qty ${level}`}>{item.quantityOnHand}</span>
                            <div className="stock-bar">
                              <div
                                className={`stock-bar-fill ${level}`}
                                style={{ width: `${Math.min((item.quantityOnHand / maxQty) * 100, 100)}%` }}
                              />
                            </div>
                          </div>
                        </td>
                        <td><span className={`badge badge-${level === "ok" ? "success" : level === "low" ? "warning" : "danger"}`}>{stockLabel(item.quantityOnHand)}</span></td>
                        <td className="row-actions">
                          {item.quantityOnHand <= 10 && <button type="button" className="secondary sm" onClick={() => void handleRestock(item)} title="Restock +50">Restock</button>}
                          <button type="button" className="secondary sm" onClick={() => startEditItem(item)}>Edit</button>
                          <button type="button" className="danger sm" onClick={() => void handleDeleteItem(item.id, item.name)}>Delete</button>
                        </td>
                      </>
                    )}
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
