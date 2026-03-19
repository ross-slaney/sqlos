import { useState, useEffect, useRef } from "react";
import {
  View,
  Text,
  StyleSheet,
  ActivityIndicator,
  Pressable,
  SafeAreaView,
} from "react-native";
import * as WebBrowser from "expo-web-browser";
import { useRouter } from "expo-router";
import { useAuth } from "../../services/AuthContext";
import {
  generateCodeVerifier,
  generateCodeChallenge,
  generateState,
  persistPKCE,
  getRedirectUri,
  buildAuthorizeUrl,
} from "../../services/sqlos-auth";
import { Colors } from "../../services/theme";

WebBrowser.maybeCompleteAuthSession();

export default function LoginScreen() {
  const router = useRouter();
  const { isAuthenticated } = useAuth();
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
    handleSignIn();
  }, []);

  async function handleSignIn() {
    setIsLoading(true);
    setError(null);

    try {
      const verifier = generateCodeVerifier();
      const challenge = await generateCodeChallenge(verifier);
      const state = generateState();
      const redirectUri = getRedirectUri();

      await persistPKCE(state, verifier);

      const authUrl = await buildAuthorizeUrl("login", redirectUri, state, challenge);

      const result = await WebBrowser.openAuthSessionAsync(authUrl, redirectUri);

      if (result.type === "success") {
        router.replace({
          pathname: "/(auth)/callback",
          params: { url: result.url },
        });
      } else {
        // User cancelled
        router.back();
      }
    } catch (e: any) {
      setError(e.message);
    } finally {
      setIsLoading(false);
    }
  }

  if (error) {
    return (
      <SafeAreaView style={styles.center}>
        <Text style={styles.errorTitle}>Sign In Failed</Text>
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
        {isLoading ? "Opening sign in..." : "Preparing..."}
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
