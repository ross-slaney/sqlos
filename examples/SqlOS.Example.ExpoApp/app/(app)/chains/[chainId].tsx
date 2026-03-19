import { useEffect, useState } from "react";
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
import { Colors } from "../../../services/theme";
import type {
  ChainDetail,
  LocationDto,
  PagedResponse,
} from "../../../services/types";

export default function ChainDetailScreen() {
  const { chainId } = useLocalSearchParams<{ chainId: string }>();
  const router = useRouter();
  const [chain, setChain] = useState<ChainDetail | null>(null);
  const [locations, setLocations] = useState<LocationDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [showAddLoc, setShowAddLoc] = useState(false);
  const [newLocName, setNewLocName] = useState("");
  const [newLocNumber, setNewLocNumber] = useState("");
  const [newLocCity, setNewLocCity] = useState("");
  const [newLocState, setNewLocState] = useState("");
  const [addingLoc, setAddingLoc] = useState(false);

  async function loadData() {
    setError(null);
    try {
      const [c, locs] = await Promise.all([
        apiGet<ChainDetail>(`/api/chains/${chainId}`),
        apiGet<PagedResponse<LocationDto>>(
          `/api/chains/${chainId}/locations?pageSize=50`,
        ),
      ]);
      setChain(c);
      setLocations(locs.data);
    } catch (e: any) {
      setError(e.message);
    }
  }

  useEffect(() => {
    setLoading(true);
    loadData().finally(() => setLoading(false));
  }, [chainId]);

  async function handleDelete() {
    if (!chain) return;
    Alert.alert("Delete Chain", `Delete "${chain.name}"? This cannot be undone.`, [
      { text: "Cancel", style: "cancel" },
      {
        text: "Delete",
        style: "destructive",
        onPress: async () => {
          try {
            await apiDelete(`/api/chains/${chain.id}`);
            router.back();
          } catch (e: any) {
            Alert.alert("Error", e.message);
          }
        },
      },
    ]);
  }

  async function handleAddLocation() {
    if (!newLocName.trim()) return;
    setAddingLoc(true);
    try {
      await apiPost(`/api/chains/${chainId}/locations`, {
        name: newLocName.trim(),
        storeNumber: newLocNumber.trim() || null,
        city: newLocCity.trim() || null,
        state: newLocState.trim() || null,
      });
      setNewLocName("");
      setNewLocNumber("");
      setNewLocCity("");
      setNewLocState("");
      setShowAddLoc(false);
      await loadData();
    } catch (e: any) {
      Alert.alert("Error", e.message);
    } finally {
      setAddingLoc(false);
    }
  }

  if (loading) {
    return (
      <View style={styles.center}>
        <ActivityIndicator size="large" color={Colors.primary} />
      </View>
    );
  }

  if (error && !chain) {
    const is403 = error.includes("403");
    return (
      <View style={styles.center}>
        <Text style={styles.errorTitle}>
          {is403 ? "Access Denied" : "Error"}
        </Text>
        <Text style={styles.errorDesc}>
          {is403
            ? "You don't have permission to view this chain."
            : error}
        </Text>
      </View>
    );
  }

  if (!chain) return null;

  return (
    <ScrollView style={styles.container} contentContainerStyle={{ padding: 16, gap: 16, paddingBottom: 40 }}>
      {/* Chain info card */}
      <View style={styles.card}>
        <Text style={styles.chainName}>{chain.name}</Text>
        {chain.description && (
          <Text style={styles.chainDesc}>{chain.description}</Text>
        )}
        <Text style={styles.chainMeta}>
          {chain.locationCount} location{chain.locationCount !== 1 ? "s" : ""}
          {chain.headquartersAddress && ` · HQ: ${chain.headquartersAddress}`}
        </Text>
        <View style={styles.actionsRow}>
          <Pressable style={styles.dangerBtn} onPress={handleDelete}>
            <Text style={styles.dangerBtnText}>Delete Chain</Text>
          </Pressable>
        </View>
      </View>

      {/* Locations */}
      <View style={styles.card}>
        <View style={styles.sectionHeader}>
          <Text style={styles.sectionTitle}>Locations</Text>
          <Pressable onPress={() => setShowAddLoc(!showAddLoc)}>
            <Text style={styles.addText}>
              {showAddLoc ? "Cancel" : "+ Add"}
            </Text>
          </Pressable>
        </View>

        {showAddLoc && (
          <View style={styles.form}>
            <View style={styles.formRow}>
              <TextInput
                style={[styles.input, { flex: 1 }]}
                placeholder="Location name"
                placeholderTextColor={Colors.textTertiary}
                value={newLocName}
                onChangeText={setNewLocName}
                autoFocus
              />
              <TextInput
                style={[styles.input, { flex: 1 }]}
                placeholder="Store #"
                placeholderTextColor={Colors.textTertiary}
                value={newLocNumber}
                onChangeText={setNewLocNumber}
              />
            </View>
            <View style={styles.formRow}>
              <TextInput
                style={[styles.input, { flex: 1 }]}
                placeholder="City"
                placeholderTextColor={Colors.textTertiary}
                value={newLocCity}
                onChangeText={setNewLocCity}
              />
              <TextInput
                style={[styles.input, { flex: 1 }]}
                placeholder="State"
                placeholderTextColor={Colors.textTertiary}
                value={newLocState}
                onChangeText={setNewLocState}
              />
            </View>
            <Pressable
              style={[styles.submitBtn, addingLoc && { opacity: 0.5 }]}
              onPress={handleAddLocation}
              disabled={addingLoc}
            >
              <Text style={styles.submitBtnText}>
                {addingLoc ? "Adding..." : "Add Location"}
              </Text>
            </Pressable>
          </View>
        )}

        {locations.length === 0 ? (
          <Text style={styles.emptyText}>
            No locations yet. Add one above.
          </Text>
        ) : (
          locations.map((loc) => (
            <Pressable
              key={loc.id}
              style={styles.locRow}
              onPress={() =>
                router.push(`/(app)/stores/${loc.id}`)
              }
            >
              <View style={{ flex: 1 }}>
                <Text style={styles.locName}>{loc.name}</Text>
                <Text style={styles.locMeta}>
                  {[loc.storeNumber && `#${loc.storeNumber}`, loc.city, loc.state]
                    .filter(Boolean)
                    .join(" · ") || "No details"}
                </Text>
              </View>
              <Text style={styles.chevron}>›</Text>
            </Pressable>
          ))
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
  chainName: { fontSize: 20, fontWeight: "700", marginBottom: 4 },
  chainDesc: {
    fontSize: 14,
    color: Colors.textSecondary,
    marginBottom: 4,
  },
  chainMeta: { fontSize: 12, color: Colors.textTertiary, marginBottom: 12 },
  actionsRow: { flexDirection: "row", gap: 10 },
  dangerBtn: {
    backgroundColor: Colors.dangerSoft,
    paddingHorizontal: 14,
    paddingVertical: 8,
    borderRadius: 8,
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
  locRow: {
    flexDirection: "row",
    alignItems: "center",
    paddingVertical: 12,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: Colors.border,
  },
  locName: { fontSize: 14, fontWeight: "600" },
  locMeta: { fontSize: 12, color: Colors.textSecondary, marginTop: 1 },
  chevron: { fontSize: 20, color: Colors.textTertiary, fontWeight: "600" },
});
