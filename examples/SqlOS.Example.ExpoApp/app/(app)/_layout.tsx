import { Tabs, Redirect } from "expo-router";
import { Text } from "react-native";
import { useAuth } from "../../services/AuthContext";
import { Colors } from "../../services/theme";

function TabIcon({ emoji, focused }: { emoji: string; focused: boolean }) {
  return (
    <Text style={{ fontSize: 20, opacity: focused ? 1 : 0.5 }}>
      {emoji}
    </Text>
  );
}

export default function AppTabLayout() {
  const { isAuthenticated, isLoading } = useAuth();

  if (!isLoading && !isAuthenticated) {
    return <Redirect href="/" />;
  }

  return (
    <Tabs
      screenOptions={{
        tabBarActiveTintColor: Colors.primary,
        tabBarInactiveTintColor: Colors.textTertiary,
        tabBarStyle: {
          backgroundColor: Colors.surface,
          borderTopColor: Colors.border,
        },
        headerStyle: {
          backgroundColor: Colors.surface,
        },
        headerShadowVisible: false,
        headerTitleStyle: {
          fontWeight: "700",
          fontSize: 17,
        },
      }}
    >
      <Tabs.Screen
        name="index"
        options={{
          title: "Dashboard",
          tabBarIcon: ({ focused }) => (
            <TabIcon emoji="📊" focused={focused} />
          ),
        }}
      />
      <Tabs.Screen
        name="chains/index"
        options={{
          title: "Chains",
          tabBarIcon: ({ focused }) => (
            <TabIcon emoji="💎" focused={focused} />
          ),
        }}
      />
      <Tabs.Screen
        name="stores/index"
        options={{
          title: "Stores",
          tabBarIcon: ({ focused }) => (
            <TabIcon emoji="🏪" focused={focused} />
          ),
        }}
      />
      {/* Hidden screens — accessible via navigation but not tabs */}
      <Tabs.Screen
        name="chains/[chainId]"
        options={{ href: null, title: "Chain Detail" }}
      />
      <Tabs.Screen
        name="stores/[locationId]"
        options={{ href: null, title: "Store Detail" }}
      />
      <Tabs.Screen
        name="settings"
        options={{
          title: "Settings",
          tabBarIcon: ({ focused }) => (
            <TabIcon emoji="⚙️" focused={focused} />
          ),
        }}
      />
    </Tabs>
  );
}
