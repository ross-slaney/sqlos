import { useEffect, useState, useCallback } from "react";
import {
  View,
  Text,
  StyleSheet,
  FlatList,
  Pressable,
  TextInput,
  ActivityIndicator,
  RefreshControl,
  Alert,
} from "react-native";
import { useRouter } from "expo-router";
import { apiGet, apiPost } from "../../../services/api";
import { Colors } from "../../../services/theme";
import { Badge } from "../../../components/Badge";
import type { PagedResponse, ChainDto } from "../../../services/types";

export default function ChainsScreen() {
  const router = useRouter();
  const [chains, setChains] = useState<ChainDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [search, setSearch] = useState("");
  const [showCreate, setShowCreate] = useState(false);
  const [creating, setCreating] = useState(false);
  const [newName, setNewName] = useState("");
  const [newDesc, setNewDesc] = useState("");

  const loadChains = useCallback(async () => {
    const params = new URLSearchParams({ pageSize: "50" });
    if (search) params.set("search", search);
    try {
      const r = await apiGet<PagedResponse<ChainDto>>(`/api/chains?${params}`);
      setChains(r.data);
    } catch {}
  }, [search]);

  useEffect(() => {
    setLoading(true);
    loadChains().finally(() => setLoading(false));
  }, [loadChains]);

  async function handleCreate() {
    if (!newName.trim()) return;
    setCreating(true);
    try {
      await apiPost("/api/chains", {
        name: newName.trim(),
        description: newDesc.trim() || null,
      });
      setNewName("");
      setNewDesc("");
      setShowCreate(false);
      await loadChains();
    } catch (e: any) {
      Alert.alert("Error", e.message);
    } finally {
      setCreating(false);
    }
  }

  return (
    <View style={styles.container}>
      <View style={styles.toolbar}>
        <TextInput
          style={styles.searchInput}
          placeholder="Search chains..."
          placeholderTextColor={Colors.textTertiary}
          value={search}
          onChangeText={setSearch}
        />
        <Pressable
          style={styles.addBtn}
          onPress={() => setShowCreate(!showCreate)}
        >
          <Text style={styles.addBtnText}>
            {showCreate ? "Cancel" : "+ Add"}
          </Text>
        </Pressable>
      </View>

      {showCreate && (
        <View style={styles.createForm}>
          <TextInput
            style={styles.input}
            placeholder="Chain name"
            placeholderTextColor={Colors.textTertiary}
            value={newName}
            onChangeText={setNewName}
            autoFocus
          />
          <TextInput
            style={styles.input}
            placeholder="Description (optional)"
            placeholderTextColor={Colors.textTertiary}
            value={newDesc}
            onChangeText={setNewDesc}
          />
          <Pressable
            style={[styles.submitBtn, creating && { opacity: 0.5 }]}
            onPress={handleCreate}
            disabled={creating}
          >
            <Text style={styles.submitBtnText}>
              {creating ? "Creating..." : "Create Chain"}
            </Text>
          </Pressable>
        </View>
      )}

      {loading ? (
        <ActivityIndicator
          style={{ marginTop: 40 }}
          color={Colors.primary}
          size="large"
        />
      ) : (
        <FlatList
          data={chains}
          keyExtractor={(c) => c.id}
          refreshControl={
            <RefreshControl
              refreshing={refreshing}
              onRefresh={async () => {
                setRefreshing(true);
                await loadChains();
                setRefreshing(false);
              }}
            />
          }
          contentContainerStyle={{ paddingBottom: 40 }}
          ListEmptyComponent={
            <View style={styles.empty}>
              <Text style={styles.emptyTitle}>No chains found</Text>
              <Text style={styles.emptyDesc}>
                {search
                  ? "Try a different search."
                  : "No chains visible with your permissions."}
              </Text>
            </View>
          }
          renderItem={({ item }) => (
            <Pressable
              style={styles.row}
              onPress={() =>
                router.push(`/(app)/chains/${item.id}`)
              }
            >
              <View style={{ flex: 1 }}>
                <Text style={styles.rowTitle}>{item.name}</Text>
                {item.description && (
                  <Text style={styles.rowDesc}>{item.description}</Text>
                )}
              </View>
              <Badge variant="neutral">{item.locationCount}</Badge>
            </Pressable>
          )}
        />
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: Colors.bg },
  toolbar: {
    flexDirection: "row",
    gap: 10,
    paddingHorizontal: 16,
    paddingVertical: 12,
  },
  searchInput: {
    flex: 1,
    backgroundColor: Colors.surface,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: Colors.border,
    paddingHorizontal: 14,
    paddingVertical: 10,
    fontSize: 14,
  },
  addBtn: {
    backgroundColor: Colors.surface,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: Colors.border,
    paddingHorizontal: 14,
    justifyContent: "center",
  },
  addBtnText: { fontSize: 13, fontWeight: "600", color: Colors.primary },
  createForm: {
    marginHorizontal: 16,
    padding: 16,
    backgroundColor: Colors.surface,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: Colors.border,
    gap: 10,
    marginBottom: 8,
  },
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
  row: {
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    paddingHorizontal: 16,
    paddingVertical: 14,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: Colors.border,
    backgroundColor: Colors.surface,
  },
  rowTitle: { fontSize: 15, fontWeight: "600" },
  rowDesc: { fontSize: 13, color: Colors.textSecondary, marginTop: 1 },
  empty: { padding: 40, alignItems: "center" },
  emptyTitle: { fontSize: 15, fontWeight: "700", marginBottom: 4 },
  emptyDesc: { fontSize: 13, color: Colors.textSecondary, textAlign: "center" },
});
