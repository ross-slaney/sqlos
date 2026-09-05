import { useCallback, useEffect, useState } from "react";
import {
  ActivityIndicator,
  KeyboardAvoidingView,
  Platform,
  Pressable,
  SafeAreaView,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
} from "react-native";
import { useRouter } from "expo-router";
import { jwtDecode } from "jwt-decode";
import { credentialEnabled, type HeadlessView } from "@sqlos/headless";
import { useHeadlessAuth } from "@sqlos/headless/react-native";
import { useAuth } from "../services/AuthContext";
import { Colors } from "../services/theme";
import type { DecodedToken } from "../services/types";
import {
  exchangeHeadlessAuthorization,
  generateNativePkce,
  getAuthServerUrl,
  getClientId,
  getNativeHeadlessRedirectUri,
  startHostedAuth,
  type HostedTokenResult,
} from "../services/sqlos-auth";

type HeadlessAuthFormProps = {
  view: "login" | "signup";
};

/**
 * Views this screen draws. Anything else the server returns (phone OTP, magic
 * link, consent, device approval, invitations, ...) falls through to hosted
 * AuthPage in the system browser instead of dead-ending the user.
 */
const HANDLED_VIEWS: ReadonlySet<HeadlessView> = new Set<HeadlessView>([
  "login",
  "password",
  "email-otp",
  "email-otp-verify",
  "signup",
  "organization",
  "mfa",
  "mfa-enroll",
]);

export function HeadlessAuthForm({ view: initialView }: HeadlessAuthFormProps) {
  const router = useRouter();
  const { isAuthenticated, login } = useAuth();
  const {
    flow,
    status,
    view: flowView,
    viewModel,
    error,
    fieldErrors,
    authorization,
    redirectUrl,
  } = useHeadlessAuth({
    issuer: getAuthServerUrl(),
    clientId: getClientId(),
    redirectUri: getNativeHeadlessRedirectUri(),
    generatePkce: generateNativePkce,
  });

  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [otpCode, setOtpCode] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [organizationName, setOrganizationName] = useState("");
  const [mfaCode, setMfaCode] = useState("");
  const [localError, setLocalError] = useState<string | null>(null);
  const [hostedBusy, setHostedBusy] = useState(false);

  const view: HeadlessView = flowView ?? initialView;
  const loading = status === "loading" || hostedBusy;
  const supportsPassword = credentialEnabled(viewModel?.settings, "password");
  const supportsEmailOtp = credentialEnabled(viewModel?.settings, "email_otp");
  const providerRedirectNotice =
    status === "redirect" && redirectUrl && !authorization
      ? "This example uses in-app login and password. Follow provider redirects in a browser client."
      : null;
  const displayError = error || localError || providerRedirectNotice;

  const completeWithTokens = useCallback(
    async (tokens: HostedTokenResult) => {
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
    },
    [login, router],
  );

  useEffect(() => {
    if (isAuthenticated) {
      router.replace("/(app)");
    }
  }, [isAuthenticated, router]);

  // `flow.start` coalesces an identical in-flight call, so React StrictMode's
  // double effect does not need a guard here.
  useEffect(() => {
    void flow.start({
      scope: "openid profile email offline_access",
      view: initialView,
    });
  }, [flow, initialView]);

  useEffect(() => {
    if (viewModel?.email) setEmail(viewModel.email);
  }, [viewModel?.email]);

  useEffect(() => {
    if (viewModel?.challengeToken) setOtpCode("");
  }, [viewModel?.challengeToken]);

  useEffect(() => {
    if (status !== "redirect" || !authorization) {
      return;
    }

    void (async () => {
      try {
        // The package stops at the code; expo-auth-session finishes /token.
        await completeWithTokens(await exchangeHeadlessAuthorization(authorization));
      } catch (err) {
        setLocalError(err instanceof Error ? err.message : "Token exchange failed.");
      }
    })();
  }, [authorization, completeWithTokens, status]);

  const continueInBrowser = async () => {
    setHostedBusy(true);
    setLocalError(null);
    try {
      await completeWithTokens(await startHostedAuth(initialView));
    } catch (err) {
      setLocalError(err instanceof Error ? err.message : "Hosted sign-in failed.");
    } finally {
      setHostedBusy(false);
    }
  };

  return (
    <SafeAreaView style={styles.safe}>
      <KeyboardAvoidingView style={styles.flex} behavior={Platform.OS === "ios" ? "padding" : undefined}>
        <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
          <Text style={styles.title}>{initialView === "signup" ? "Create account" : "Sign in"}</Text>
          <Text style={styles.subtitle}>Native headless AuthPage — SqlOS owns the request; this screen draws the view.</Text>

          {displayError ? <Text style={styles.error}>{displayError}</Text> : null}
          {viewModel?.info ? <Text style={styles.info}>{viewModel.info}</Text> : null}

          {view === "login" && (
            <>
              <Text style={styles.label}>Email</Text>
              <TextInput
                autoCapitalize="none"
                autoComplete="email"
                keyboardType="email-address"
                style={styles.input}
                value={email}
                onChangeText={setEmail}
              />
              {fieldErrors.email ? <Text style={styles.fieldError}>{fieldErrors.email}</Text> : null}
              <Pressable
                style={styles.button}
                disabled={loading}
                onPress={() => void flow.identify({ email })}
              >
                <Text style={styles.buttonText}>{loading ? "Checking..." : "Continue"}</Text>
              </Pressable>
            </>
          )}

          {view === "password" && (
            <>
              <Text style={styles.label}>Password</Text>
              <TextInput
                secureTextEntry
                style={styles.input}
                value={password}
                onChangeText={setPassword}
              />
              {fieldErrors.password ? <Text style={styles.fieldError}>{fieldErrors.password}</Text> : null}
              <Pressable
                style={styles.button}
                disabled={loading}
                onPress={() => void flow.password.login({ email, password })}
              >
                <Text style={styles.buttonText}>{loading ? "Signing in..." : "Sign in"}</Text>
              </Pressable>
              {supportsEmailOtp ? (
                <Pressable disabled={loading} onPress={() => void flow.emailOtp.start({ email })}>
                  <Text style={styles.link}>Email me a code instead</Text>
                </Pressable>
              ) : null}
            </>
          )}

          {view === "email-otp" && (
            <>
              <Text style={styles.label}>Email</Text>
              <TextInput
                autoCapitalize="none"
                keyboardType="email-address"
                style={styles.input}
                value={email}
                onChangeText={setEmail}
              />
              {fieldErrors.email ? <Text style={styles.fieldError}>{fieldErrors.email}</Text> : null}
              <Pressable
                style={styles.button}
                disabled={loading}
                onPress={() => void flow.emailOtp.start({ email })}
              >
                <Text style={styles.buttonText}>{loading ? "Sending code..." : "Email me a code"}</Text>
              </Pressable>
              {supportsPassword ? (
                <Pressable disabled={loading} onPress={() => void flow.identify({ email })}>
                  <Text style={styles.link}>Use a password instead</Text>
                </Pressable>
              ) : null}
            </>
          )}

          {view === "email-otp-verify" && (
            <>
              <Text style={styles.label}>Code from your email</Text>
              <TextInput
                keyboardType="number-pad"
                autoComplete="one-time-code"
                style={styles.input}
                value={otpCode}
                onChangeText={setOtpCode}
              />
              {fieldErrors.code ? <Text style={styles.fieldError}>{fieldErrors.code}</Text> : null}
              <Pressable
                style={styles.button}
                disabled={loading}
                onPress={() => void flow.emailOtp.verify({ code: otpCode })}
              >
                <Text style={styles.buttonText}>{loading ? "Verifying..." : "Verify code"}</Text>
              </Pressable>
              <Pressable disabled={loading} onPress={() => void flow.emailOtp.start({ email })}>
                <Text style={styles.link}>Send a new code</Text>
              </Pressable>
            </>
          )}

          {view === "signup" && (
            <>
              <Text style={styles.label}>Display name</Text>
              <TextInput style={styles.input} value={displayName} onChangeText={setDisplayName} />
              <Text style={styles.label}>Organization</Text>
              <TextInput style={styles.input} value={organizationName} onChangeText={setOrganizationName} />
              <Text style={styles.label}>Email</Text>
              <TextInput
                autoCapitalize="none"
                keyboardType="email-address"
                style={styles.input}
                value={email}
                onChangeText={setEmail}
              />
              {fieldErrors.email ? <Text style={styles.fieldError}>{fieldErrors.email}</Text> : null}
              <Text style={styles.label}>Password</Text>
              <TextInput secureTextEntry style={styles.input} value={password} onChangeText={setPassword} />
              {fieldErrors.password ? <Text style={styles.fieldError}>{fieldErrors.password}</Text> : null}
              <Pressable
                style={styles.button}
                disabled={loading}
                onPress={() =>
                  void flow.signup({
                    displayName: displayName.trim() || email,
                    email,
                    password,
                    organizationName,
                  })
                }
              >
                <Text style={styles.buttonText}>{loading ? "Creating account..." : "Create account"}</Text>
              </Pressable>
            </>
          )}

          {view === "organization" && (
            <>
              {(viewModel?.organizationSelection ?? []).map((org) => (
                <Pressable
                  key={org.id}
                  style={styles.org}
                  disabled={loading}
                  onPress={() => void flow.organization.select({ organizationId: org.id })}
                >
                  <Text style={styles.orgName}>{org.name}</Text>
                  <Text style={styles.orgRole}>{org.role}</Text>
                </Pressable>
              ))}
            </>
          )}

          {(view === "mfa" || view === "mfa-enroll") && (
            <>
              {view === "mfa-enroll" && !viewModel?.totpEnrollment ? (
                <Pressable
                  style={styles.button}
                  disabled={loading}
                  onPress={() => void flow.mfa.totp.enrollStart({ displayName: "Authenticator app" })}
                >
                  <Text style={styles.buttonText}>Start authenticator setup</Text>
                </Pressable>
              ) : null}
              {viewModel?.totpEnrollment?.secret ? (
                <Text style={styles.secret}>Secret: {viewModel.totpEnrollment.secret}</Text>
              ) : null}
              <Text style={styles.label}>Authenticator or recovery code</Text>
              <TextInput
                keyboardType="number-pad"
                style={styles.input}
                value={mfaCode}
                onChangeText={setMfaCode}
              />
              {fieldErrors.code ? <Text style={styles.fieldError}>{fieldErrors.code}</Text> : null}
              <Pressable
                style={styles.button}
                disabled={loading}
                onPress={() =>
                  void (view === "mfa-enroll"
                    ? flow.mfa.totp.enrollVerify({ code: mfaCode })
                    : flow.mfa.verify({ code: mfaCode }))
                }
              >
                <Text style={styles.buttonText}>{loading ? "Verifying..." : "Verify"}</Text>
              </Pressable>
            </>
          )}

          {!HANDLED_VIEWS.has(view) && (
            <>
              <Text style={styles.notice}>
                SqlOS needs the &ldquo;{view}&rdquo; step, which this screen does not draw. Continue in the browser to
                finish with hosted AuthPage.
              </Text>
              <Pressable style={styles.button} disabled={loading} onPress={() => void continueInBrowser()}>
                <Text style={styles.buttonText}>{hostedBusy ? "Opening browser..." : "Continue in browser"}</Text>
              </Pressable>
            </>
          )}

          {loading && view === "login" && !viewModel ? (
            <ActivityIndicator color={Colors.primary} />
          ) : null}

          <Pressable onPress={() => router.back()}>
            <Text style={styles.back}>Go back</Text>
          </Pressable>
        </ScrollView>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: Colors.bg },
  flex: { flex: 1 },
  content: { padding: 24, gap: 10 },
  title: { fontSize: 24, fontWeight: "700", color: Colors.text },
  subtitle: { fontSize: 14, color: Colors.textSecondary, marginBottom: 8 },
  label: { fontSize: 13, fontWeight: "600", color: Colors.textSecondary, marginTop: 8 },
  input: {
    borderWidth: 1,
    borderColor: Colors.border,
    borderRadius: 10,
    paddingHorizontal: 12,
    paddingVertical: 12,
    backgroundColor: Colors.surface,
    color: Colors.text,
  },
  button: {
    backgroundColor: Colors.primary,
    paddingVertical: 14,
    borderRadius: 10,
    alignItems: "center",
    marginTop: 8,
  },
  buttonText: { color: "#fff", fontWeight: "600", fontSize: 15 },
  link: { color: Colors.primary, textAlign: "center", marginTop: 12, fontWeight: "600" },
  error: { color: Colors.danger, marginVertical: 8 },
  info: { color: Colors.textSecondary, marginVertical: 8 },
  notice: { color: Colors.text, marginVertical: 8, lineHeight: 20 },
  fieldError: { color: Colors.danger, fontSize: 12 },
  org: {
    borderWidth: 1,
    borderColor: Colors.border,
    borderRadius: 10,
    padding: 14,
    backgroundColor: Colors.surface,
  },
  orgName: { fontWeight: "700", color: Colors.text },
  orgRole: { color: Colors.textSecondary, marginTop: 2 },
  secret: { fontSize: 12, color: Colors.textSecondary },
  back: { color: Colors.primary, textAlign: "center", marginTop: 24, fontWeight: "600" },
});
