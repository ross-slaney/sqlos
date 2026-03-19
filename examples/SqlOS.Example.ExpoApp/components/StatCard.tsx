import { View, Text, StyleSheet, Pressable } from "react-native";
import { Colors } from "../services/theme";

const gradients: Record<string, string> = {
  chains: Colors.primary,
  stores: "#06b6d4",
  items: Colors.warning,
};

export function StatCard({
  label,
  value,
  sub,
  type,
  onPress,
}: {
  label: string;
  value: string | number;
  sub: string;
  type: "chains" | "stores" | "items";
  onPress?: () => void;
}) {
  const Wrapper = onPress ? Pressable : View;
  return (
    <Wrapper onPress={onPress} style={styles.card}>
      <View
        style={[styles.topBar, { backgroundColor: gradients[type] }]}
      />
      <Text style={styles.label}>{label}</Text>
      <Text style={styles.value}>{value}</Text>
      <Text style={styles.sub}>{sub}</Text>
    </Wrapper>
  );
}

const styles = StyleSheet.create({
  card: {
    flex: 1,
    backgroundColor: Colors.surface,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: Colors.border,
    padding: 16,
    overflow: "hidden",
  },
  topBar: {
    position: "absolute",
    top: 0,
    left: 0,
    right: 0,
    height: 3,
  },
  label: {
    fontSize: 11,
    fontWeight: "500",
    color: Colors.textSecondary,
    textTransform: "uppercase",
    letterSpacing: 0.5,
  },
  value: {
    fontSize: 26,
    fontWeight: "800",
    letterSpacing: -0.5,
    marginTop: 2,
  },
  sub: {
    fontSize: 11,
    color: Colors.textTertiary,
    marginTop: 2,
  },
});
