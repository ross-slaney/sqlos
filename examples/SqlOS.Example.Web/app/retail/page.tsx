"use client";

import { useSession } from "next-auth/react";
import { useEffect, useState, useMemo } from "react";
import Link from "next/link";
import { apiGet, apiUrl } from "@/lib/api";

type PagedResponse<T> = { data: T[]; totalCount: number; hasMore: boolean };
type StoreSummary = { id: string; chainId: string; chainName?: string; name: string; storeNumber?: string; city?: string; state?: string };
type InventoryItemDto = { id: string; sku: string; name: string; price: number; quantityOnHand: number; locationId: string };
type ChainDto = { id: string; name: string; description?: string; locationCount: number };
type DemoSubject = { email: string | null; displayName: string; role?: string | null; description?: string | null; type: string };

type StoreInventory = { store: StoreSummary; items: InventoryItemDto[] };

function inferRole(userName: string, demoUsers: DemoSubject[], email?: string | null): { roleName: string; roleLevel: "admin" | "chain" | "store" | "clerk" | "none" } {
  const matched = demoUsers.find((u) => u.email === email);
  const role = matched?.role ?? "";
  if (/CompanyAdmin/i.test(role) || /org_admin/i.test(role)) return { roleName: "Company Admin", roleLevel: "admin" };
  if (/ChainManager/i.test(role)) return { roleName: "Chain Manager", roleLevel: "chain" };
  if (/StoreManager/i.test(role)) return { roleName: "Store Manager", roleLevel: "store" };
  if (/StoreClerk/i.test(role)) return { roleName: "Store Clerk", roleLevel: "clerk" };
  if (matched && !role) return { roleName: "Member", roleLevel: "none" };
  if (/admin/i.test(userName)) return { roleName: "Admin", roleLevel: "admin" };
  if (/manager/i.test(userName)) return { roleName: "Manager", roleLevel: "store" };
  if (/clerk/i.test(userName)) return { roleName: "Clerk", roleLevel: "clerk" };
  if (/regional/i.test(userName)) return { roleName: "Regional", roleLevel: "chain" };
  return { roleName: "Team Member", roleLevel: "none" };
}

function getGreeting(): string {
  const h = new Date().getHours();
  if (h < 12) return "Good morning";
  if (h < 17) return "Good afternoon";
  return "Good evening";
}

function NorthyAssistant({ message, mood }: { message: string; mood: "happy" | "alert" | "wave" | "thinking" }) {
  const face = mood === "alert" ? "😯" : mood === "wave" ? "👋" : mood === "thinking" ? "🤔" : "😊";
  return (
    <div className="northy">
      <div className="northy-character">
        <div className="northy-bag">
          <div className="northy-face">{face}</div>
        </div>
      </div>
      <div className="northy-bubble">
        <p>{message}</p>
      </div>
    </div>
  );
}

export default function RetailDashboard() {
  const { data: session } = useSession();
  const [stores, setStores] = useState<StoreSummary[]>([]);
  const [chains, setChains] = useState<ChainDto[]>([]);
  const [storeInventories, setStoreInventories] = useState<StoreInventory[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [demoUsers, setDemoUsers] = useState<DemoSubject[]>([]);

  useEffect(() => {
    fetch(`${apiUrl}/api/demo/users`).then((r) => r.json()).then(setDemoUsers).catch(() => {});
  }, []);

  useEffect(() => {
    if (!session?.accessToken) return;
    setLoading(true);
    Promise.all([
      apiGet<PagedResponse<StoreSummary>>("/api/locations?pageSize=250", session.accessToken),
      apiGet<PagedResponse<ChainDto>>("/api/chains?pageSize=50", session.accessToken),
    ])
      .then(async ([locRes, chainRes]) => {
        setStores(locRes.data);
        setChains(chainRes.data);
        const invResults = await Promise.all(
          locRes.data.map(async (store) => {
            const inv = await apiGet<PagedResponse<InventoryItemDto>>(
              `/api/locations/${store.id}/inventory?pageSize=250`,
              session.accessToken!
            ).catch(() => ({ data: [] as InventoryItemDto[], totalCount: 0, hasMore: false }));
            return { store, items: inv.data };
          })
        );
        setStoreInventories(invResults);
      })
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }, [session?.accessToken]);

  const userName = session?.user?.name ?? session?.user?.email ?? "User";
  const { roleName, roleLevel } = inferRole(userName, demoUsers, session?.user?.email);

  const allItems = useMemo(() => storeInventories.flatMap((si) => si.items), [storeInventories]);
  const lowStockItems = useMemo(() =>
    storeInventories.flatMap((si) =>
      si.items.filter((i) => i.quantityOnHand <= 10).map((i) => ({ ...i, storeName: si.store.name, storeId: si.store.id }))
    ), [storeInventories]);
  const totalValue = useMemo(() => allItems.reduce((s, i) => s + i.price * i.quantityOnHand, 0), [allItems]);

  const storesByChain = new Map<string, { chainId: string; chainName: string; stores: StoreSummary[] }>();
  for (const store of stores) {
    const existing = storesByChain.get(store.chainId);
    if (existing) { existing.stores.push(store); continue; }
    storesByChain.set(store.chainId, { chainId: store.chainId, chainName: store.chainName ?? store.chainId, stores: [store] });
  }
  const groupedChains = Array.from(storesByChain.values())
    .map((g) => ({ ...g, stores: [...g.stores].sort((a, b) => a.name.localeCompare(b.name)) }))
    .sort((a, b) => a.chainName.localeCompare(b.chainName));

  const northyMessage = useMemo(() => {
    if (loading) return "Hang on, I'm loading your data...";
    if (stores.length === 0) return "I can't see any data right now — that's FGA in action! Try switching to a different identity in the sidebar.";
    if (lowStockItems.length > 0) return `Heads up! ${lowStockItems.length} item${lowStockItems.length > 1 ? "s are" : " is"} running low on stock. You might want to restock before they sell out.`;
    if (roleLevel === "admin") return `Everything looks great across your ${chains.length} chain${chains.length !== 1 ? "s" : ""} and ${stores.length} store${stores.length !== 1 ? "s" : ""}. All ${allItems.length} items are well-stocked!`;
    if (roleLevel === "chain") return `Your ${stores.length} store${stores.length !== 1 ? "s are" : " is"} looking good. ${allItems.length} item${allItems.length !== 1 ? "s" : ""} all stocked and ready to go!`;
    if (roleLevel === "store" || roleLevel === "clerk") return `Your store has ${allItems.length} item${allItems.length !== 1 ? "s" : ""} tracked. Everything is in stock — nice work keeping the shelves full!`;
    return `You've got ${stores.length} store${stores.length !== 1 ? "s" : ""} with ${allItems.length} item${allItems.length !== 1 ? "s" : ""} visible. Looking good!`;
  }, [loading, stores, allItems, lowStockItems, chains, roleLevel]);

  const northyMood = loading ? "thinking" as const : stores.length === 0 ? "wave" as const : lowStockItems.length > 0 ? "alert" as const : "happy" as const;

  const quickActions = useMemo(() => {
    const actions: { label: string; href: string; icon: string }[] = [];
    if (roleLevel === "admin") {
      actions.push({ label: "Add Chain", href: "/retail/chains", icon: "🏢" });
      actions.push({ label: "Add Store", href: "/retail/stores", icon: "📍" });
    }
    if (roleLevel === "admin" || roleLevel === "chain") {
      actions.push({ label: "View All Stores", href: "/retail/stores", icon: "🗺️" });
    }
    if (stores.length === 1) {
      actions.push({ label: "View Inventory", href: `/retail/locations/${stores[0].id}`, icon: "📦" });
    }
    if (stores.length > 0) {
      actions.push({ label: "Browse Chains", href: "/retail/chains", icon: "🔍" });
    }
    return actions;
  }, [roleLevel, stores]);

  return (
    <div className="gap-20">
      <div className="dash-greeting">
        <div className="dash-greeting-text">
          <div className="dash-greeting-top">
            <h1>{getGreeting()}, {userName.split(" ")[0].length > 2 ? userName.split(" ")[0] : userName}</h1>
            <span className="badge badge-primary">{roleName}</span>
          </div>
          <p className="muted">Here&apos;s what&apos;s happening across your retail operation.</p>
        </div>
      </div>

      <NorthyAssistant message={northyMessage} mood={northyMood} />

      {error && <p className="error">{error}</p>}

      <div className="stats-grid">
        <Link href="/retail/chains" className="stat-card stat-card--chains">
          <div className="stat-card-label">Chains</div>
          <div className="stat-card-value">{loading ? "—" : chains.length}</div>
          <div className="stat-card-sub">{loading ? "Loading..." : `${chains.length} visible to you`}</div>
        </Link>
        <Link href="/retail/stores" className="stat-card stat-card--stores">
          <div className="stat-card-label">Stores</div>
          <div className="stat-card-value">{loading ? "—" : stores.length}</div>
          <div className="stat-card-sub">{loading ? "Loading..." : `Across ${groupedChains.length} chain${groupedChains.length !== 1 ? "s" : ""}`}</div>
        </Link>
        <div className="stat-card stat-card--items">
          <div className="stat-card-label">Inventory</div>
          <div className="stat-card-value">{loading ? "—" : allItems.length}</div>
          <div className="stat-card-sub">
            {loading ? "Loading..." : (
              lowStockItems.length > 0
                ? <><span className="warn">{lowStockItems.length} low</span> · ${totalValue.toLocaleString(undefined, { maximumFractionDigits: 0 })} value</>
                : `$${totalValue.toLocaleString(undefined, { maximumFractionDigits: 0 })} total value`
            )}
          </div>
        </div>
      </div>

      {!loading && quickActions.length > 0 && (
        <div className="quick-actions">
          {quickActions.map((a) => (
            <Link key={a.label} href={a.href} className="quick-action">
              <span className="quick-action-icon">{a.icon}</span>
              <span>{a.label}</span>
            </Link>
          ))}
        </div>
      )}

      {!loading && lowStockItems.length > 0 && (
        <div className="card alert-card">
          <div className="alert-card-header">
            <span className="alert-card-icon">⚠️</span>
            <div>
              <h3>Low Stock Alert</h3>
              <p className="muted">{lowStockItems.length} item{lowStockItems.length !== 1 ? "s need" : " needs"} attention</p>
            </div>
          </div>
          <div className="alert-items">
            {lowStockItems.map((item) => (
              <Link key={item.id} href={`/retail/locations/${item.storeId}`} className="alert-item">
                <div className="alert-item-info">
                  <strong>{item.name}</strong>
                  <span className="muted">{item.storeName} · {item.sku}</span>
                </div>
                <div className="alert-item-right">
                  <span className={`stock-qty ${item.quantityOnHand === 0 ? "out" : "low"}`}>
                    {item.quantityOnHand} left
                  </span>
                </div>
              </Link>
            ))}
          </div>
        </div>
      )}

      {!loading && storeInventories.length > 0 && storeInventories.some((si) => si.items.length > 0) && (
        <div className="card">
          <h2>Inventory by Store</h2>
          <div className="store-inv-grid">
            {storeInventories.filter((si) => si.items.length > 0).map((si) => {
              const storeValue = si.items.reduce((s, i) => s + i.price * i.quantityOnHand, 0);
              const storeLow = si.items.filter((i) => i.quantityOnHand <= 10).length;
              return (
                <Link key={si.store.id} href={`/retail/locations/${si.store.id}`} className="store-inv-card">
                  <div className="store-inv-card-header">
                    <strong>{si.store.name}</strong>
                    {storeLow > 0 && <span className="badge badge-warning">{storeLow} low</span>}
                  </div>
                  <div className="store-inv-stats">
                    <div><span className="store-inv-num">{si.items.length}</span><span className="store-inv-label">items</span></div>
                    <div><span className="store-inv-num">${(storeValue / 1000).toFixed(1)}k</span><span className="store-inv-label">value</span></div>
                  </div>
                  <div className="store-inv-bar">
                    {si.items.map((item) => {
                      const level = item.quantityOnHand === 0 ? "out" : item.quantityOnHand <= 10 ? "low" : "ok";
                      return <div key={item.id} className={`store-inv-bar-seg ${level}`} style={{ flex: item.price * item.quantityOnHand }} title={`${item.name}: ${item.quantityOnHand} units`} />;
                    })}
                  </div>
                </Link>
              );
            })}
          </div>
        </div>
      )}

      {!loading && groupedChains.length === 0 ? (
        <div className="empty-state">
          <strong>No data visible</strong>
          <p>Your current identity has no authorization grants. Switch to a different user in the sidebar to see data filtered by FGA.</p>
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
                <thead><tr><th>Store</th><th>Store #</th><th>City</th><th>State</th></tr></thead>
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
