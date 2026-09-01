import { useState, useEffect, useRef } from "react";
import {
  Text,
  StyleSheet,
  ActivityIndicator,
  Pressable,
  SafeAreaView,
} from "react-native";
import * as WebBrowser from "expo-web-browser";
import { useRouter } from "expo-router";
import { jwtDecode } from "jwt-decode";
import { useAuth } from "../../services/AuthContext";
import { startHostedAuth } from "../../services/sqlos-auth";
import { Colors } from "../../services/theme";
import type { DecodedToken } from "../../services/types";

WebBrowser.maybeCompleteAuthSession();

export default function SignupScreen() {
  const router = useRouter();
  const { isAuthenticated, login } = useAuth();
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const startedRef = useRef(false);

  useEffect(() => {
    if (isAuthenticated) {
      router.replace("/(app)");
      return;
    }
    if (startedRef.current) return;
    startedRef.current = true;
    void handleSignUp();
  }, []);

  async function handleSignUp() {
    setIsLoading(true);
    setError(null);

    try {
      const tokens = await startHostedAuth("signup");
      const decoded = jwtDecode<DecodedToken>(tokens.accessToken);
      await login({
        accessToken: tokens.accessToken,
        refreshToken: tokens.refreshToken,
        userId: decoded.sub ?? "",
        email: decoded.email ?? "",
        displayName: decoded.name ?? decoded.email ?? "User",
        organizationId: decoded.org_id ?? null,
        sessionId: decoded.sid ?? "",
        exp: decoded.exp,
      });
      router.replace("/(app)");
    } catch (e: unknown) {
      const message = e instanceof Error ? e.message : "Sign up failed";
      if (message.includes("cancelled")) {
        router.back();
        return;
      }
      setError(message);
    } finally {
      setIsLoading(false);
    }
  }

  if (error) {
    return (
      <SafeAreaView style={styles.center}>
        <Text style={styles.errorTitle}>Sign Up Failed</Text>
        <Text style={styles.errorText}>{error}</Text>
        <Pressable style={styles.btn} onPress={() => router.back()}>
          <Text style={styles.btnText}>Go Back</Text>
        </Pressable>
      </SafeAreaView>
    );
  }

  return (
    <SafeAreaView style={styles.center}>
      <ActivityIndicator size="large" color={Colors.primary} />
      <Text style={styles.loadingText}>
        {isLoading ? "Opening sign up..." : "Preparing..."}
      </Text>
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
  errorTitle: { fontSize: 18, fontWeight: "700", marginBottom: 8 },
  errorText: {
    fontSize: 14,
    color: Colors.danger,
    textAlign: "center",
    marginBottom: 24,
  },
  btn: {
    backgroundColor: Colors.primary,
    paddingHorizontal: 24,
    paddingVertical: 12,
    borderRadius: 10,
  },
  btnText: { color: "#fff", fontWeight: "600", fontSize: 15 },
});
