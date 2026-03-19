import { View, Text, StyleSheet, Pressable, SafeAreaView, Alert } from "react-native";
import { useRouter } from "expo-router";
import { useAuth } from "../../services/AuthContext";
import { UserSwitcher } from "../../components/UserSwitcher";
import { Colors } from "../../services/theme";

export default function SettingsScreen() {
  const { session, logout } = useAuth();
  const router = useRouter();

  async function handleLogout() {
    Alert.alert("Sign Out", "Are you sure you want to sign out?", [
      { text: "Cancel", style: "cancel" },
      {
        text: "Sign Out",
        style: "destructive",
        onPress: async () => {
          await logout();
          router.replace("/");
        },
      },
    ]);
  }

  return (
    <SafeAreaView style={styles.container}>
      <View style={styles.section}>
        <Text style={styles.sectionTitle}>Account</Text>
        <View style={styles.card}>
          <View style={styles.row}>
            <Text style={styles.label}>Name</Text>
            <Text style={styles.value}>{session?.displayName ?? "—"}</Text>
          </View>
          <View style={styles.divider} />
          <View style={styles.row}>
            <Text style={styles.label}>Email</Text>
            <Text style={styles.value}>{session?.email ?? "—"}</Text>
          </View>
        </View>
      </View>

      <View style={styles.section}>
        <Text style={styles.sectionTitle}>FGA Demo</Text>
        <Text style={styles.sectionDesc}>
          Switch between demo identities to see how fine-grained access
          controls filter data.
        </Text>
        <UserSwitcher />
      </View>

      <View style={styles.section}>
        <Pressable style={styles.logoutBtn} onPress={handleLogout}>
          <Text style={styles.logoutText}>Sign Out</Text>
        </Pressable>
      </View>

      <Text style={styles.footnote}>
        Northwind Retail — Powered by SqlOS
      </Text>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: Colors.bg,
    padding: 20,
  },
  section: { marginBottom: 28 },
  sectionTitle: {
    fontSize: 13,
    fontWeight: "600",
    color: Colors.textSecondary,
    textTransform: "uppercase",
    letterSpacing: 0.5,
    marginBottom: 8,
  },
  sectionDesc: {
    fontSize: 13,
    color: Colors.textSecondary,
    marginBottom: 12,
    lineHeight: 19,
  },
  card: {
    backgroundColor: Colors.surface,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: Colors.border,
    overflow: "hidden",
  },
  row: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
    paddingHorizontal: 16,
    paddingVertical: 14,
  },
  divider: {
    height: StyleSheet.hairlineWidth,
    backgroundColor: Colors.border,
    marginHorizontal: 16,
  },
  label: {
    fontSize: 14,
    color: Colors.textSecondary,
  },
  value: {
    fontSize: 14,
    fontWeight: "500",
  },
  logoutBtn: {
    backgroundColor: Colors.dangerSoft,
    paddingVertical: 14,
    borderRadius: 12,
    alignItems: "center",
    borderWidth: 1,
    borderColor: "transparent",
  },
  logoutText: {
    color: Colors.danger,
    fontSize: 15,
    fontWeight: "600",
  },
  footnote: {
    textAlign: "center",
    fontSize: 12,
    color: Colors.textTertiary,
    marginTop: "auto",
  },
});
