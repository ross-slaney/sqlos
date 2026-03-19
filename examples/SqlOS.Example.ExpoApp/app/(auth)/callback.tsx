import { useEffect, useState } from "react";
import {
  View,
  Text,
  StyleSheet,
  ActivityIndicator,
  SafeAreaView,
  Pressable,
} from "react-native";
import { useRouter, useLocalSearchParams } from "expo-router";
import { jwtDecode } from "jwt-decode";
import { useAuth } from "../../services/AuthContext";
import {
  readPKCE,
  clearPKCE,
  getAuthServerUrl,
  getClientId,
  getRedirectUri,
} from "../../services/sqlos-auth";
import { Colors } from "../../services/theme";
import type { DecodedToken } from "../../services/types";

export default function AuthCallbackScreen() {
  const router = useRouter();
  const params = useLocalSearchParams();
  const { login } = useAuth();
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    handleCallback();
  }, []);

  async function handleCallback() {
    try {
      const callbackUrl = params.url as string;
      if (!callbackUrl) throw new Error("No callback URL provided");

      const url = new URL(callbackUrl);
      const code = url.searchParams.get("code");
      const state = url.searchParams.get("state");
      const errorParam = url.searchParams.get("error");

      if (errorParam) {
        throw new Error(url.searchParams.get("error_description") || errorParam);
      }
      if (!code || !state) throw new Error("Missing authorization code or state");

      const pkce = await readPKCE();
      if (!pkce.verifier) throw new Error("Missing code verifier");
      if (state !== pkce.state) throw new Error("State mismatch");

      // Exchange code for tokens
      const tokenRes = await fetch(`${getAuthServerUrl()}/token`, {
        method: "POST",
        headers: { "Content-Type": "application/x-www-form-urlencoded" },
        body: new URLSearchParams({
          grant_type: "authorization_code",
          code,
          client_id: getClientId(),
          redirect_uri: getRedirectUri(),
          code_verifier: pkce.verifier,
        }).toString(),
      });

      const tokenData = await tokenRes.json();
      if (!tokenRes.ok || !tokenData.access_token || !tokenData.refresh_token) {
        throw new Error(
          tokenData.error_description || tokenData.error || "Token exchange failed",
        );
      }

      const decoded = jwtDecode<DecodedToken>(tokenData.access_token);
      await login({
        accessToken: tokenData.access_token,
        refreshToken: tokenData.refresh_token,
        userId: decoded.sub ?? "",
        email: decoded.email ?? "",
        displayName: decoded.name ?? decoded.email ?? "User",
        organizationId: decoded.org_id ?? null,
        sessionId: decoded.sid ?? "",
        exp: decoded.exp,
      });

      await clearPKCE();
      router.replace("/(app)");
    } catch (e: any) {
      await clearPKCE();
      setError(e.message);
      setTimeout(() => router.replace("/"), 3000);
    }
  }

  if (error) {
    return (
      <SafeAreaView style={styles.center}>
        <Text style={styles.errorTitle}>Authentication Error</Text>
        <Text style={styles.errorText}>{error}</Text>
        <Pressable style={styles.btn} onPress={() => router.replace("/")}>
          <Text style={styles.btnText}>Back to Home</Text>
        </Pressable>
      </SafeAreaView>
    );
  }

  return (
    <SafeAreaView style={styles.center}>
      <ActivityIndicator size="large" color={Colors.primary} />
      <Text style={styles.loadingText}>Completing authentication...</Text>
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
