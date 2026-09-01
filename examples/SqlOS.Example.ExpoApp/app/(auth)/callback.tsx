import { useEffect } from "react";
import { Text, StyleSheet, ActivityIndicator, SafeAreaView } from "react-native";
import { useRouter } from "expo-router";
import { Colors } from "../../services/theme";

export default function AuthCallbackScreen() {
  const router = useRouter();

  useEffect(() => {
    router.replace("/(auth)/login");
  }, [router]);

  return (
    <SafeAreaView style={styles.center}>
      <ActivityIndicator size="large" color={Colors.primary} />
      <Text style={styles.loadingText}>Returning to sign in...</Text>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  center: {
    flex: 1,
    justifyContent: "center",
    alignItems: "center",
    backgroundColor: Colors.bg,
    padding: 32,
  },
  loadingText: {
    marginTop: 16,
    fontSize: 14,
    color: Colors.textSecondary,
  },
});
