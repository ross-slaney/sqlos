import { useEffect, useRef, useState, useCallback } from "react";
import {
  View,
  Text,
  StyleSheet,
  Pressable,
  Dimensions,
  FlatList,
  ActivityIndicator,
} from "react-native";
import { useRouter } from "expo-router";
import { useAuth } from "../services/AuthContext";
import { Colors } from "../services/theme";

const { width } = Dimensions.get("window");

const slides = [
  {
    icon: "🏬",
    title: "Manage Your Chains",
    desc: "Organize your entire retail empire by chain. Track headquarters, regions, and performance from one place.",
    accent: Colors.primary,
  },
  {
    icon: "📦",
    title: "Track Inventory",
    desc: "Visual stock levels with automatic low-stock alerts. One-tap restock keeps your shelves full.",
    accent: "#06b6d4",
  },
  {
    icon: "🛡️",
    title: "Fine-Grained Access",
    desc: "Company admins see everything. Store clerks see their store. Everyone gets exactly what they need.",
    accent: "#8b5cf6",
  },
  {
    icon: "⚡",
    title: "Powered by SqlOS",
    desc: "Built-in auth, FGA, and multi-tenancy. No external services needed — it ships with your app.",
    accent: Colors.warning,
  },
];

export default function LandingScreen() {
  const { isAuthenticated, isLoading } = useAuth();
  const router = useRouter();
  const flatListRef = useRef<FlatList>(null);
  const [activeIndex, setActiveIndex] = useState(0);

  // Auto-advance slides
  useEffect(() => {
    const timer = setInterval(() => {
      setActiveIndex((prev) => {
        const next = (prev + 1) % slides.length;
        flatListRef.current?.scrollToIndex({ index: next, animated: true });
        return next;
      });
    }, 4000);
    return () => clearInterval(timer);
  }, []);

  const onViewableItemsChanged = useCallback(
    ({ viewableItems }: any) => {
      if (viewableItems.length > 0) {
        setActiveIndex(viewableItems[0].index ?? 0);
      }
    },
    [],
  );

  if (isLoading) {
    return (
      <View style={styles.loadingContainer}>
        <ActivityIndicator size="large" color={Colors.primary} />
      </View>
    );
  }

  // Root layout handles redirect to /(app) when authenticated

  return (
    <View style={styles.container}>
      {/* Brand header */}
      <View style={styles.header}>
        <View style={styles.logoIcon}>
          <Text style={styles.logoLetter}>N</Text>
        </View>
        <Text style={styles.brandName}>Northwind Retail</Text>
      </View>

      {/* Slide carousel */}
      <View style={styles.sliderContainer}>
        <FlatList
          ref={flatListRef}
          data={slides}
          horizontal
          pagingEnabled
          showsHorizontalScrollIndicator={false}
          keyExtractor={(_, i) => String(i)}
          onViewableItemsChanged={onViewableItemsChanged}
          viewabilityConfig={{ viewAreaCoveragePercentThreshold: 50 }}
          getItemLayout={(_, index) => ({
            length: width - 48,
            offset: (width - 48) * index,
            index,
          })}
          contentContainerStyle={{ paddingHorizontal: 0 }}
          renderItem={({ item }) => (
            <View style={[styles.slide, { width: width - 48 }]}>
              <View
                style={[
                  styles.slideIcon,
                  { backgroundColor: item.accent + "18" },
                ]}
              >
                <Text style={styles.slideEmoji}>{item.icon}</Text>
              </View>
              <Text style={styles.slideTitle}>{item.title}</Text>
              <Text style={styles.slideDesc}>{item.desc}</Text>
            </View>
          )}
        />
        {/* Dots */}
        <View style={styles.dots}>
          {slides.map((_, i) => (
            <View
              key={i}
              style={[
                styles.dot,
                i === activeIndex && styles.dotActive,
              ]}
            />
          ))}
        </View>
      </View>

      {/* Auth buttons */}
      <View style={styles.footer}>
        <Pressable
          style={styles.primaryBtn}
          onPress={() => router.push("/(auth)/login")}
        >
          <Text style={styles.primaryBtnText}>Sign In</Text>
        </Pressable>
        <Pressable
          style={styles.ghostBtn}
          onPress={() => router.push("/(auth)/signup")}
        >
          <Text style={styles.ghostBtnText}>Create Account</Text>
        </Pressable>
        <Text style={styles.footnote}>
          Built with SqlOS — Auth & FGA in a single NuGet package
        </Text>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  loadingContainer: {
    flex: 1,
    justifyContent: "center",
    alignItems: "center",
    backgroundColor: Colors.bg,
  },
  container: {
    flex: 1,
    backgroundColor: Colors.bg,
    paddingTop: 80,
    paddingHorizontal: 24,
  },
  header: {
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    marginBottom: 40,
    justifyContent: "center",
  },
  logoIcon: {
    width: 36,
    height: 36,
    borderRadius: 10,
    backgroundColor: Colors.primary,
    alignItems: "center",
    justifyContent: "center",
  },
  logoLetter: {
    color: "#fff",
    fontWeight: "800",
    fontSize: 17,
  },
  brandName: {
    fontSize: 20,
    fontWeight: "700",
    letterSpacing: -0.3,
  },
  sliderContainer: {
    flex: 1,
    justifyContent: "center",
  },
  slide: {
    alignItems: "center",
    justifyContent: "center",
    paddingHorizontal: 16,
  },
  slideIcon: {
    width: 80,
    height: 80,
    borderRadius: 24,
    alignItems: "center",
    justifyContent: "center",
    marginBottom: 24,
  },
  slideEmoji: {
    fontSize: 36,
  },
  slideTitle: {
    fontSize: 26,
    fontWeight: "800",
    textAlign: "center",
    letterSpacing: -0.5,
    marginBottom: 12,
    color: Colors.text,
  },
  slideDesc: {
    fontSize: 15,
    lineHeight: 23,
    textAlign: "center",
    color: Colors.textSecondary,
    maxWidth: 320,
  },
  dots: {
    flexDirection: "row",
    justifyContent: "center",
    gap: 8,
    marginTop: 32,
  },
  dot: {
    width: 8,
    height: 8,
    borderRadius: 4,
    backgroundColor: Colors.border,
  },
  dotActive: {
    backgroundColor: Colors.primary,
    width: 24,
  },
  footer: {
    paddingBottom: 40,
    gap: 12,
  },
  primaryBtn: {
    backgroundColor: Colors.primary,
    paddingVertical: 15,
    borderRadius: 12,
    alignItems: "center",
  },
  primaryBtnText: {
    color: "#fff",
    fontSize: 16,
    fontWeight: "700",
  },
  ghostBtn: {
    backgroundColor: Colors.surface,
    paddingVertical: 15,
    borderRadius: 12,
    alignItems: "center",
    borderWidth: 1,
    borderColor: Colors.border,
  },
  ghostBtnText: {
    color: Colors.text,
    fontSize: 16,
    fontWeight: "600",
  },
  footnote: {
    textAlign: "center",
    fontSize: 12,
    color: Colors.textTertiary,
    marginTop: 4,
  },
});
