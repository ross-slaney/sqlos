import { useEffect, useState, useMemo } from "react";
import {
  View,
  Text,
  StyleSheet,
  ScrollView,
  Pressable,
  ActivityIndicator,
  RefreshControl,
} from "react-native";
import { useRouter } from "expo-router";
import { useAuth } from "../../services/AuthContext";
import { apiGet } from "../../services/api";
import { API_URL } from "../../services/config";
import { NorthyAssistant } from "../../components/NorthyAssistant";
import { StatCard } from "../../components/StatCard";
import { Badge } from "../../components/Badge";
import { Colors } from "../../services/theme";
import type {
  PagedResponse,
  StoreSummary,
  ChainDto,
  InventoryItemDto,
  StoreInventory,
  DemoSubject,
} from "../../services/types";

function inferRole(
  userName: string,
  demoUsers: DemoSubject[],
  email?: string | null,
) {
  const matched = demoUsers.find((u) => u.email === email);
  const role = matched?.role ?? "";
  if (/CompanyAdmin/i.test(role) || /org_admin/i.test(role))
    return { roleName: "Company Admin", roleLevel: "admin" as const };
  if (/ChainManager/i.test(role))
    return { roleName: "Chain Manager", roleLevel: "chain" as const };
  if (/StoreManager/i.test(role))
    return { roleName: "Store Manager", roleLevel: "store" as const };
  if (/StoreClerk/i.test(role))
    return { roleName: "Store Clerk", roleLevel: "clerk" as const };
  return { roleName: "Team Member", roleLevel: "none" as const };
}

function getGreeting() {
  const h = new Date().getHours();
  if (h < 12) return "Good morning";
  if (h < 17) return "Good afternoon";
  return "Good evening";
}

export default function DashboardScreen() {
  const { session } = useAuth();
  const router = useRouter();
  const [stores, setStores] = useState<StoreSummary[]>([]);
  const [chains, setChains] = useState<ChainDto[]>([]);
  const [storeInventories, setStoreInventories] = useState<StoreInventory[]>(
    [],
  );
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [demoUsers, setDemoUsers] = useState<DemoSubject[]>([]);

  const userName = session?.displayName ?? session?.email ?? "User";
  const firstName = userName.split(" ")[0];
  const displayName = firstName.length > 2 ? firstName : userName;
  const { roleName, roleLevel } = inferRole(
    userName,
    demoUsers,
    session?.email,
  );

  const allItems = useMemo(
    () => storeInventories.flatMap((si) => si.items),
    [storeInventories],
  );
  const lowStockItems = useMemo(
    () =>
      storeInventories.flatMap((si) =>
        si.items
          .filter((i) => i.quantityOnHand <= 10)
          .map((i) => ({
            ...i,
            storeName: si.store.name,
            storeId: si.store.id,
          })),
      ),
    [storeInventories],
  );
  const totalValue = useMemo(
    () => allItems.reduce((s, i) => s + i.price * i.quantityOnHand, 0),
    [allItems],
  );

  const northyMessage = useMemo(() => {
    if (loading) return "Hang on, I'm loading your data...";
    if (stores.length === 0)
      return "I can't see any data right now — that's FGA in action! Try switching to a different identity in Settings.";
    if (lowStockItems.length > 0)
      return `Heads up! ${lowStockItems.length} item${lowStockItems.length > 1 ? "s are" : " is"} running low on stock.`;
    return `Looking good! ${allItems.length} items tracked across ${stores.length} store${stores.length !== 1 ? "s" : ""}.`;
  }, [loading, stores, allItems, lowStockItems]);

  const northyMood = loading
    ? ("thinking" as const)
    : stores.length === 0
      ? ("wave" as const)
      : lowStockItems.length > 0
        ? ("alert" as const)
        : ("happy" as const);

  async function loadData() {
    try {
      const [locRes, chainRes] = await Promise.all([
        apiGet<PagedResponse<StoreSummary>>("/api/locations?pageSize=250"),
        apiGet<PagedResponse<ChainDto>>("/api/chains?pageSize=50"),
      ]);
      setStores(locRes.data);
      setChains(chainRes.data);
      const invResults = await Promise.all(
        locRes.data.map(async (store) => {
          const inv = await apiGet<PagedResponse<InventoryItemDto>>(
            `/api/locations/${store.id}/inventory?pageSize=250`,
          ).catch(() => ({
            data: [] as InventoryItemDto[],
            totalCount: 0,
            hasMore: false,
          }));
          return { store, items: inv.data };
        }),
      );
      setStoreInventories(invResults);
    } catch (e: any) {
      setError(e.message);
    }
  }

  useEffect(() => {
    fetch(`${API_URL}/api/demo/users`)
      .then((r) => r.json())
      .then(setDemoUsers)
      .catch(() => {});
  }, []);

  useEffect(() => {
    setLoading(true);
    loadData().finally(() => setLoading(false));
  }, [session?.accessToken]);

  async function onRefresh() {
    setRefreshing(true);
    await loadData();
    setRefreshing(false);
  }

  return (
    <ScrollView
      style={styles.container}
      contentContainerStyle={styles.content}
      refreshControl={
        <RefreshControl refreshing={refreshing} onRefresh={onRefresh} />
      }
    >
      {/* Greeting */}
      <View style={styles.greetingRow}>
        <View style={{ flex: 1 }}>
          <Text style={styles.greeting}>
            {getGreeting()}, {displayName}
          </Text>
          <Text style={styles.greetingSub}>
            Here's what's happening across your retail operation.
          </Text>
        </View>
        <Badge variant="primary">{roleName}</Badge>
      </View>

      <NorthyAssistant message={northyMessage} mood={northyMood} />

      {error && <Text style={styles.error}>{error}</Text>}

      {/* Stats */}
      {loading ? (
        <ActivityIndicator
          style={{ marginVertical: 24 }}
          color={Colors.primary}
        />
      ) : (
        <>
          <View style={styles.statsRow}>
            <StatCard
              label="Chains"
              value={chains.length}
              sub={`${chains.length} visible`}
              type="chains"
              onPress={() => router.push("/(app)/chains")}
            />
            <StatCard
              label="Stores"
              value={stores.length}
              sub={`${chains.length} chain${chains.length !== 1 ? "s" : ""}`}
              type="stores"
              onPress={() => router.push("/(app)/stores")}
            />
          </View>
          <View style={styles.statsRow}>
            <StatCard
              label="Inventory"
              value={allItems.length}
              sub={
                lowStockItems.length > 0
                  ? `${lowStockItems.length} low · $${totalValue.toLocaleString(undefined, { maximumFractionDigits: 0 })}`
                  : `$${totalValue.toLocaleString(undefined, { maximumFractionDigits: 0 })} value`
              }
              type="items"
            />
          </View>

          {/* Low stock alerts */}
          {lowStockItems.length > 0 && (
            <View style={styles.alertCard}>
              <Text style={styles.alertTitle}>
                ⚠️ {lowStockItems.length} Low Stock Item
                {lowStockItems.length !== 1 ? "s" : ""}
              </Text>
              {lowStockItems.slice(0, 5).map((item) => (
                <Pressable
                  key={item.id}
                  style={styles.alertItem}
                  onPress={() =>
                    router.push(`/(app)/stores/${item.storeId}`)
                  }
                >
                  <View style={{ flex: 1 }}>
                    <Text style={styles.alertItemName}>{item.name}</Text>
                    <Text style={styles.alertItemSub}>
                      {item.storeName} · {item.sku}
                    </Text>
                  </View>
                  <Text
                    style={[
                      styles.alertQty,
                      {
                        color:
                          item.quantityOnHand === 0
                            ? Colors.danger
                            : Colors.warning,
                      },
                    ]}
                  >
                    {item.quantityOnHand} left
                  </Text>
                </Pressable>
              ))}
            </View>
          )}

          {/* Empty state */}
          {stores.length === 0 && (
            <View style={styles.emptyState}>
              <Text style={styles.emptyTitle}>No data visible</Text>
              <Text style={styles.emptyDesc}>
                Your current identity has no grants. Switch identities in
                Settings.
              </Text>
            </View>
          )}
        </>
      )}
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: Colors.bg },
  content: { padding: 20, gap: 16, paddingBottom: 40 },
  greetingRow: {
    flexDirection: "row",
    alignItems: "flex-start",
    gap: 12,
  },
  greeting: {
    fontSize: 22,
    fontWeight: "800",
    letterSpacing: -0.3,
  },
  greetingSub: {
    fontSize: 13,
    color: Colors.textSecondary,
    marginTop: 2,
  },
  error: { color: Colors.danger, fontSize: 13 },
  statsRow: {
    flexDirection: "row",
    gap: 12,
  },
  alertCard: {
    backgroundColor: "#fffef5",
    borderRadius: 12,
    borderWidth: 1,
    borderColor: "#fde68a",
    padding: 16,
    gap: 8,
  },
  alertTitle: {
    fontSize: 15,
    fontWeight: "700",
    color: "#92400e",
  },
  alertItem: {
    flexDirection: "row",
    alignItems: "center",
    backgroundColor: "#fff",
    borderRadius: 8,
    borderWidth: 1,
    borderColor: "#fde68a",
    padding: 12,
    gap: 12,
  },
  alertItemName: { fontSize: 14, fontWeight: "600" },
  alertItemSub: { fontSize: 12, color: Colors.textSecondary, marginTop: 1 },
  alertQty: { fontSize: 13, fontWeight: "700" },
  emptyState: {
    padding: 32,
    borderRadius: 12,
    backgroundColor: Colors.bg,
    borderWidth: 1,
    borderStyle: "dashed",
    borderColor: Colors.border,
    alignItems: "center",
  },
  emptyTitle: { fontSize: 15, fontWeight: "700", marginBottom: 4 },
  emptyDesc: {
    fontSize: 13,
    color: Colors.textSecondary,
    textAlign: "center",
  },
});
