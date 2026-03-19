import { View, Text, StyleSheet } from "react-native";
import { Colors } from "../services/theme";

type Variant = "neutral" | "primary" | "success" | "warning" | "danger";

const bgMap: Record<Variant, string> = {
  neutral: "#f5f5f5",
  primary: Colors.primarySoft,
  success: Colors.successSoft,
  warning: Colors.warningSoft,
  danger: Colors.dangerSoft,
};

const colorMap: Record<Variant, string> = {
  neutral: Colors.textSecondary,
  primary: Colors.primary,
  success: Colors.success,
  warning: Colors.warning,
  danger: Colors.danger,
};

export function Badge({
  children,
  variant = "neutral",
}: {
  children: React.ReactNode;
  variant?: Variant;
}) {
  return (
    <View style={[styles.badge, { backgroundColor: bgMap[variant] }]}>
      <Text style={[styles.text, { color: colorMap[variant] }]}>
        {children}
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  badge: {
    paddingHorizontal: 8,
    paddingVertical: 2,
    borderRadius: 999,
    alignSelf: "flex-start",
  },
  text: {
    fontSize: 11,
    fontWeight: "600",
  },
});
