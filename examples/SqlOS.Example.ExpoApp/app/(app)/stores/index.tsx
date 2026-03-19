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
} from "react-native";
import { useRouter } from "expo-router";
import { apiGet } from "../../../services/api";
import { Colors } from "../../../services/theme";
import type { PagedResponse, LocationDto } from "../../../services/types";

export default function StoresScreen() {
  const router = useRouter();
  const [stores, setStores] = useState<LocationDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [search, setSearch] = useState("");

  const loadStores = useCallback(async () => {
    const params = new URLSearchParams({ pageSize: "50" });
    if (search) params.set("search", search);
    try {
      const r = await apiGet<PagedResponse<LocationDto>>(
        `/api/locations?${params}`,
      );
      setStores(r.data);
    } catch {}
  }, [search]);

  useEffect(() => {
    setLoading(true);
    loadStores().finally(() => setLoading(false));
  }, [loadStores]);

  return (
    <View style={styles.container}>
      <View style={styles.toolbar}>
        <TextInput
          style={styles.searchInput}
          placeholder="Search stores..."
          placeholderTextColor={Colors.textTertiary}
          value={search}
          onChangeText={setSearch}
        />
        <View style={styles.countBadge}>
          <Text style={styles.countText}>
            {loading ? "..." : `${stores.length}`}
          </Text>
        </View>
      </View>

      {loading ? (
        <ActivityIndicator
          style={{ marginTop: 40 }}
          color={Colors.primary}
          size="large"
        />
      ) : (
        <FlatList
          data={stores}
          keyExtractor={(s) => s.id}
          refreshControl={
            <RefreshControl
              refreshing={refreshing}
              onRefresh={async () => {
                setRefreshing(true);
                await loadStores();
                setRefreshing(false);
              }}
            />
          }
          contentContainerStyle={{ paddingBottom: 40 }}
          ListEmptyComponent={
            <View style={styles.empty}>
              <Text style={styles.emptyTitle}>No stores found</Text>
              <Text style={styles.emptyDesc}>
                {search
                  ? "Try a different search."
                  : "No stores visible with your permissions."}
              </Text>
            </View>
          }
          renderItem={({ item }) => (
            <Pressable
              style={styles.row}
              onPress={() =>
                router.push(`/(app)/stores/${item.id}`)
              }
            >
              <View style={{ flex: 1 }}>
                <Text style={styles.rowTitle}>{item.name}</Text>
                <Text style={styles.rowDesc}>
                  {[
                    item.storeNumber && `#${item.storeNumber}`,
                    item.chainName,
                    item.city,
                    item.state,
                  ]
                    .filter(Boolean)
                    .join(" · ")}
                </Text>
              </View>
              <Text style={styles.chevron}>›</Text>
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
    alignItems: "center",
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
  countBadge: {
    backgroundColor: "#f5f5f5",
    paddingHorizontal: 10,
    paddingVertical: 6,
    borderRadius: 999,
  },
  countText: {
    fontSize: 12,
    fontWeight: "600",
    color: Colors.textSecondary,
  },
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
  chevron: { fontSize: 20, color: Colors.textTertiary, fontWeight: "600" },
  empty: { padding: 40, alignItems: "center" },
  emptyTitle: { fontSize: 15, fontWeight: "700", marginBottom: 4 },
  emptyDesc: {
    fontSize: 13,
    color: Colors.textSecondary,
    textAlign: "center",
  },
});
