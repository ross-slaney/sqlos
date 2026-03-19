import { useState, useEffect } from "react";
import {
  View,
  Text,
  StyleSheet,
  Pressable,
  Modal,
  FlatList,
  ActivityIndicator,
} from "react-native";
import { jwtDecode } from "jwt-decode";
import { API_URL } from "../services/config";
import { useAuth } from "../services/AuthContext";
import * as authService from "../services/auth";
import { Colors } from "../services/theme";
import type { DemoSubject } from "../services/types";

function humanizeRole(raw: string): string {
  return raw
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .replace(/_/g, " ")
    .replace(/\b\w/g, (c) => c.toUpperCase())
    .trim();
}

function formatLabel(s: DemoSubject): string {
  const name = s.displayName;
  if (s.type === "agent") return `${name} (Agent)`;
  if (s.type === "service_account") return `${name} (API)`;
  const role = s.role;
  if (!role) return name;
  const parts = role
    .split(",")
    .map((r) => r.trim())
    .filter((r) => r && !/^org_(admin|member)$/i.test(r));
  if (parts.length === 0) return name;
  const humanized = parts.map(humanizeRole).join(", ");
  return `${name} · ${humanized}`;
}

export function UserSwitcher() {
  const { login, refresh } = useAuth();
  const [subjects, setSubjects] = useState<DemoSubject[]>([]);
  const [visible, setVisible] = useState(false);
  const [switching, setSwitching] = useState(false);

  useEffect(() => {
    fetch(`${API_URL}/api/demo/users`)
      .then((r) => r.json())
      .then(setSubjects)
      .catch(() => {});
  }, []);

  async function handleSwitch(subject: DemoSubject) {
    if (switching) return;
    setSwitching(true);
    try {
      if (subject.type === "agent" && subject.credential) {
        await authService.setAuthOverride({
          type: "agent",
          header: "X-Agent-Token",
          value: subject.credential,
          displayName: subject.displayName,
        });
        await refresh();
        setVisible(false);
        return;
      }
      if (subject.type === "service_account" && subject.credential) {
        await authService.setAuthOverride({
          type: "service_account",
          header: "X-Api-Key",
          value: subject.credential,
          displayName: subject.displayName,
        });
        await refresh();
        setVisible(false);
        return;
      }
      if (subject.type === "user" && subject.email) {
        await authService.setAuthOverride(null);
        const res = await fetch(`${API_URL}/api/v1/auth/demo/switch`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ email: subject.email }),
        });
        if (!res.ok) throw new Error("Switch failed");
        const data = await res.json();
        const decoded = jwtDecode<{
          sub?: string;
          exp: number;
          org_id?: string;
        }>(data.accessToken);
        await login({
          accessToken: data.accessToken,
          refreshToken: data.refreshToken,
          userId: decoded.sub ?? "",
          displayName: subject.displayName,
          email: subject.email,
          organizationId: data.organizationId ?? decoded.org_id ?? null,
          sessionId: data.sessionId,
          exp: decoded.exp,
        });
        setVisible(false);
      }
    } catch {
      // Ignore
    } finally {
      setSwitching(false);
    }
  }

  if (subjects.length === 0) return null;

  return (
    <>
      <Pressable style={styles.trigger} onPress={() => setVisible(true)}>
        <Text style={styles.triggerText}>Switch Identity</Text>
        <Text style={styles.chevron}>›</Text>
      </Pressable>

      <Modal
        visible={visible}
        transparent
        animationType="slide"
        onRequestClose={() => setVisible(false)}
      >
        <View style={styles.overlay}>
          <View style={styles.sheet}>
            <View style={styles.sheetHeader}>
              <Text style={styles.sheetTitle}>Switch Identity</Text>
              <Pressable onPress={() => setVisible(false)}>
                <Text style={styles.closeBtn}>Done</Text>
              </Pressable>
            </View>
            <Text style={styles.sheetSub}>
              Switch between demo identities to see FGA in action.
            </Text>
            {switching && (
              <ActivityIndicator
                style={{ marginVertical: 12 }}
                color={Colors.primary}
              />
            )}
            <FlatList
              data={subjects}
              keyExtractor={(s) =>
                s.email ?? `${s.type}:${s.credential ?? s.displayName}`
              }
              renderItem={({ item }) => (
                <Pressable
                  style={styles.item}
                  onPress={() => void handleSwitch(item)}
                  disabled={switching}
                >
                  <View style={styles.avatar}>
                    <Text style={styles.avatarText}>
                      {item.displayName.charAt(0).toUpperCase()}
                    </Text>
                  </View>
                  <View style={{ flex: 1 }}>
                    <Text style={styles.itemName}>
                      {formatLabel(item)}
                    </Text>
                    {item.description && (
                      <Text style={styles.itemDesc}>
                        {item.description}
                      </Text>
                    )}
                  </View>
                </Pressable>
              )}
            />
          </View>
        </View>
      </Modal>
    </>
  );
}

const styles = StyleSheet.create({
  trigger: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    backgroundColor: Colors.primarySoft,
    paddingHorizontal: 14,
    paddingVertical: 10,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: Colors.primaryMuted,
  },
  triggerText: {
    fontSize: 13,
    fontWeight: "600",
    color: Colors.primary,
  },
  chevron: {
    fontSize: 18,
    color: Colors.primary,
    fontWeight: "600",
  },
  overlay: {
    flex: 1,
    backgroundColor: "rgba(0,0,0,0.4)",
    justifyContent: "flex-end",
  },
  sheet: {
    backgroundColor: Colors.surface,
    borderTopLeftRadius: 20,
    borderTopRightRadius: 20,
    paddingHorizontal: 20,
    paddingTop: 16,
    paddingBottom: 40,
    maxHeight: "70%",
  },
  sheetHeader: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "center",
    marginBottom: 4,
  },
  sheetTitle: {
    fontSize: 18,
    fontWeight: "700",
  },
  closeBtn: {
    fontSize: 15,
    fontWeight: "600",
    color: Colors.primary,
  },
  sheetSub: {
    fontSize: 13,
    color: Colors.textSecondary,
    marginBottom: 16,
  },
  item: {
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    paddingVertical: 12,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: Colors.border,
  },
  avatar: {
    width: 36,
    height: 36,
    borderRadius: 9,
    backgroundColor: Colors.primary,
    alignItems: "center",
    justifyContent: "center",
  },
  avatarText: {
    color: "#fff",
    fontWeight: "700",
    fontSize: 15,
  },
  itemName: {
    fontSize: 14,
    fontWeight: "600",
  },
  itemDesc: {
    fontSize: 12,
    color: Colors.textSecondary,
    marginTop: 1,
  },
});
