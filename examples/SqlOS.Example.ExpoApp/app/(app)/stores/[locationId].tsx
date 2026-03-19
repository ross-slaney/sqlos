import { useEffect, useState, useMemo } from "react";
import {
  View,
  Text,
  StyleSheet,
  ScrollView,
  Pressable,
  TextInput,
  ActivityIndicator,
  Alert,
} from "react-native";
import { useLocalSearchParams, useRouter } from "expo-router";
import { apiGet, apiPost, apiPut, apiDelete } from "../../../services/api";
import { Badge } from "../../../components/Badge";
import { Colors } from "../../../services/theme";
import type {
  LocationDetail,
  InventoryItemDto,
  PagedResponse,
} from "../../../services/types";

function stockLevel(qty: number) {
  if (qty === 0) return "out" as const;
  if (qty <= 10) return "low" as const;
  return "ok" as const;
}

function stockLabel(qty: number) {
  if (qty === 0) return "Out";
  if (qty <= 10) return "Low";
  return "In stock";
}

const levelColor = { ok: Colors.success, low: Colors.warning, out: Colors.danger };
const levelVariant = { ok: "success" as const, low: "warning" as const, out: "danger" as const };

export default function LocationDetailScreen() {
  const { locationId } = useLocalSearchParams<{ locationId: string }>();
  const router = useRouter();
  const [location, setLocation] = useState<LocationDetail | null>(null);
  const [inventory, setInventory] = useState<InventoryItemDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [showAddItem, setShowAddItem] = useState(false);
  const [newSku, setNewSku] = useState("");
  const [newItemName, setNewItemName] = useState("");
  const [newPrice, setNewPrice] = useState("");
  const [newQty, setNewQty] = useState("");
  const [addingItem, setAddingItem] = useState(false);

  const totalValue = useMemo(
    () => inventory.reduce((s, i) => s + i.price * i.quantityOnHand, 0),
    [inventory],
  );
  const maxQty = useMemo(
    () => Math.max(...inventory.map((i) => i.quantityOnHand), 100),
    [inventory],
  );

  async function loadData() {
    setError(null);
    try {
      const [loc, inv] = await Promise.all([
        apiGet<LocationDetail>(`/api/locations/${locationId}`),
        apiGet<PagedResponse<InventoryItemDto>>(
          `/api/locations/${locationId}/inventory?pageSize=50`,
        ),
      ]);
      setLocation(loc);
      setInventory(inv.data);
    } catch (e: any) {
      setError(e.message);
    }
  }

  useEffect(() => {
    setLoading(true);
    loadData().finally(() => setLoading(false));
  }, [locationId]);

  async function handleRestock(item: InventoryItemDto) {
    try {
      await apiPut(`/api/inventory/${item.id}`, {
        name: item.name,
        price: item.price,
        quantityOnHand: item.quantityOnHand + 50,
      });
      await loadData();
    } catch (e: any) {
      Alert.alert("Error", e.message);
    }
  }

  async function handleDeleteItem(item: InventoryItemDto) {
    Alert.alert("Delete Item", `Delete "${item.name}"?`, [
      { text: "Cancel", style: "cancel" },
      {
        text: "Delete",
        style: "destructive",
        onPress: async () => {
          try {
            await apiDelete(`/api/inventory/${item.id}`);
            await loadData();
          } catch (e: any) {
            Alert.alert("Error", e.message);
          }
        },
      },
    ]);
  }

  async function handleAddItem() {
    if (!newSku.trim() || !newItemName.trim()) return;
    setAddingItem(true);
    try {
      await apiPost(`/api/locations/${locationId}/inventory`, {
        sku: newSku.trim(),
        name: newItemName.trim(),
        price: parseFloat(newPrice) || 0,
        quantityOnHand: parseInt(newQty) || 0,
      });
      setNewSku("");
      setNewItemName("");
      setNewPrice("");
      setNewQty("");
      setShowAddItem(false);
      await loadData();
    } catch (e: any) {
      Alert.alert("Error", e.message);
    } finally {
      setAddingItem(false);
    }
  }

  async function handleDeleteLocation() {
    if (!location) return;
    Alert.alert(
      "Delete Location",
      `Delete "${location.name}"? This cannot be undone.`,
      [
        { text: "Cancel", style: "cancel" },
        {
          text: "Delete",
          style: "destructive",
          onPress: async () => {
            try {
              await apiDelete(`/api/locations/${location.id}`);
              router.back();
            } catch (e: any) {
              Alert.alert("Error", e.message);
            }
          },
        },
      ],
    );
  }

  if (loading) {
    return (
      <View style={styles.center}>
        <ActivityIndicator size="large" color={Colors.primary} />
      </View>
    );
  }

  if (error && !location) {
    const is403 = error.includes("403");
    return (
      <View style={styles.center}>
        <Text style={styles.errorTitle}>
          {is403 ? "Access Denied" : "Error"}
        </Text>
        <Text style={styles.errorDesc}>
          {is403
            ? "You don't have permission to view this store."
            : error}
        </Text>
      </View>
    );
  }

  if (!location) return null;

  const addressParts = [
    location.address,
    location.city,
    location.state,
    location.zipCode,
  ].filter(Boolean);

  return (
    <ScrollView
      style={styles.container}
      contentContainerStyle={{ padding: 16, gap: 16, paddingBottom: 40 }}
    >
      {/* Store info */}
      <View style={styles.card}>
        <Text style={styles.storeName}>{location.name}</Text>
        <Text style={styles.storeMeta}>
          {location.storeNumber && `#${location.storeNumber} · `}
          {addressParts.length > 0
            ? addressParts.join(", ")
            : "No address on file"}
        </Text>
        <View style={styles.detailRow}>
          <View style={styles.detailItem}>
            <Text style={styles.detailLabel}>Items</Text>
            <Text style={styles.detailValue}>{inventory.length}</Text>
          </View>
          <View style={styles.detailItem}>
            <Text style={styles.detailLabel}>Value</Text>
            <Text style={styles.detailValue}>
              ${totalValue.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}
            </Text>
          </View>
        </View>
        <Pressable style={styles.dangerBtn} onPress={handleDeleteLocation}>
          <Text style={styles.dangerBtnText}>Delete Location</Text>
        </Pressable>
      </View>

      {/* Inventory */}
      <View style={styles.card}>
        <View style={styles.sectionHeader}>
          <Text style={styles.sectionTitle}>Inventory</Text>
          <Pressable onPress={() => setShowAddItem(!showAddItem)}>
            <Text style={styles.addText}>
              {showAddItem ? "Cancel" : "+ Add"}
            </Text>
          </Pressable>
        </View>

        {showAddItem && (
          <View style={styles.form}>
            <View style={styles.formRow}>
              <TextInput
                style={[styles.input, { flex: 1 }]}
                placeholder="SKU"
                placeholderTextColor={Colors.textTertiary}
                value={newSku}
                onChangeText={setNewSku}
                autoFocus
              />
              <TextInput
                style={[styles.input, { flex: 2 }]}
                placeholder="Item name"
                placeholderTextColor={Colors.textTertiary}
                value={newItemName}
                onChangeText={setNewItemName}
              />
            </View>
            <View style={styles.formRow}>
              <TextInput
                style={[styles.input, { flex: 1 }]}
                placeholder="Price"
                placeholderTextColor={Colors.textTertiary}
                keyboardType="decimal-pad"
                value={newPrice}
                onChangeText={setNewPrice}
              />
              <TextInput
                style={[styles.input, { flex: 1 }]}
                placeholder="Quantity"
                placeholderTextColor={Colors.textTertiary}
                keyboardType="number-pad"
                value={newQty}
                onChangeText={setNewQty}
              />
            </View>
            <Pressable
              style={[styles.submitBtn, addingItem && { opacity: 0.5 }]}
              onPress={handleAddItem}
              disabled={addingItem}
            >
              <Text style={styles.submitBtnText}>
                {addingItem ? "Adding..." : "Add Item"}
              </Text>
            </Pressable>
          </View>
        )}

        {inventory.length === 0 ? (
          <Text style={styles.emptyText}>No inventory items yet.</Text>
        ) : (
          inventory.map((item) => {
            const level = stockLevel(item.quantityOnHand);
            return (
              <View key={item.id} style={styles.invRow}>
                <View style={{ flex: 1 }}>
                  <Text style={styles.invName}>{item.name}</Text>
                  <View style={styles.invMeta}>
                    <Text style={styles.invSku}>{item.sku}</Text>
                    <Text style={styles.invPrice}>
                      ${item.price.toFixed(2)}
                    </Text>
                  </View>
                  {/* Stock bar */}
                  <View style={styles.stockRow}>
                    <Text
                      style={[
                        styles.stockQty,
                        { color: levelColor[level] },
                      ]}
                    >
                      {item.quantityOnHand}
                    </Text>
                    <View style={styles.stockBar}>
                      <View
                        style={[
                          styles.stockBarFill,
                          {
                            backgroundColor: levelColor[level],
                            width: `${Math.min((item.quantityOnHand / maxQty) * 100, 100)}%`,
                          },
                        ]}
                      />
                    </View>
                    <Badge variant={levelVariant[level]}>
                      {stockLabel(item.quantityOnHand)}
                    </Badge>
                  </View>
                </View>
                <View style={styles.invActions}>
                  {item.quantityOnHand <= 10 && (
                    <Pressable
                      style={styles.restockBtn}
                      onPress={() => void handleRestock(item)}
                    >
                      <Text style={styles.restockText}>+50</Text>
                    </Pressable>
                  )}
                  <Pressable onPress={() => handleDeleteItem(item)}>
                    <Text style={styles.deleteText}>✕</Text>
                  </Pressable>
                </View>
              </View>
            );
          })
        )}
      </View>
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: Colors.bg },
  center: {
    flex: 1,
    justifyContent: "center",
    alignItems: "center",
    padding: 32,
  },
  errorTitle: { fontSize: 18, fontWeight: "700", marginBottom: 8 },
  errorDesc: {
    fontSize: 14,
    color: Colors.textSecondary,
    textAlign: "center",
  },
  card: {
    backgroundColor: Colors.surface,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: Colors.border,
    padding: 16,
  },
  storeName: { fontSize: 20, fontWeight: "700", marginBottom: 4 },
  storeMeta: { fontSize: 13, color: Colors.textSecondary, marginBottom: 12 },
  detailRow: {
    flexDirection: "row",
    gap: 8,
    marginBottom: 12,
  },
  detailItem: {
    flex: 1,
    backgroundColor: Colors.bg,
    borderRadius: 8,
    padding: 12,
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
  },
  detailLabel: { fontSize: 13, color: Colors.textSecondary },
  detailValue: { fontSize: 15, fontWeight: "700" },
  dangerBtn: {
    backgroundColor: Colors.dangerSoft,
    paddingVertical: 10,
    borderRadius: 8,
    alignItems: "center",
  },
  dangerBtnText: { color: Colors.danger, fontWeight: "600", fontSize: 13 },
  sectionHeader: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
    marginBottom: 12,
  },
  sectionTitle: { fontSize: 16, fontWeight: "700" },
  addText: { color: Colors.primary, fontWeight: "600", fontSize: 14 },
  form: { gap: 10, marginBottom: 12 },
  formRow: { flexDirection: "row", gap: 10 },
  input: {
    backgroundColor: Colors.bg,
    borderRadius: 8,
    borderWidth: 1,
    borderColor: Colors.border,
    paddingHorizontal: 12,
    paddingVertical: 10,
    fontSize: 14,
  },
  submitBtn: {
    backgroundColor: Colors.primary,
    borderRadius: 8,
    paddingVertical: 12,
    alignItems: "center",
  },
  submitBtnText: { color: "#fff", fontWeight: "600", fontSize: 14 },
  emptyText: {
    textAlign: "center",
    color: Colors.textSecondary,
    fontSize: 13,
    paddingVertical: 16,
  },
  invRow: {
    flexDirection: "row",
    paddingVertical: 12,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: Colors.border,
    gap: 12,
  },
  invName: { fontSize: 14, fontWeight: "600", marginBottom: 2 },
  invMeta: { flexDirection: "row", gap: 8 },
  invSku: {
    fontSize: 12,
    color: Colors.textTertiary,
    fontFamily: "monospace",
  },
  invPrice: { fontSize: 12, color: Colors.textSecondary },
  stockRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    marginTop: 6,
  },
  stockQty: { fontSize: 13, fontWeight: "700", width: 30 },
  stockBar: {
    flex: 1,
    height: 6,
    borderRadius: 3,
    backgroundColor: "#f0f0f0",
    overflow: "hidden",
    maxWidth: 80,
  },
  stockBarFill: {
    height: "100%",
    borderRadius: 3,
  },
  invActions: {
    justifyContent: "center",
    alignItems: "flex-end",
    gap: 8,
  },
  restockBtn: {
    backgroundColor: Colors.primarySoft,
    paddingHorizontal: 10,
    paddingVertical: 4,
    borderRadius: 6,
  },
  restockText: {
    color: Colors.primary,
    fontWeight: "700",
    fontSize: 12,
  },
  deleteText: {
    color: Colors.textTertiary,
    fontSize: 16,
    padding: 4,
  },
});
