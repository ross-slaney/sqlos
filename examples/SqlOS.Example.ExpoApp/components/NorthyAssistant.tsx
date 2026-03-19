import { View, Text, StyleSheet } from "react-native";
import { Colors } from "../services/theme";

type Mood = "happy" | "alert" | "wave" | "thinking";

const faceMap: Record<Mood, string> = {
  happy: "😊",
  alert: "😯",
  wave: "👋",
  thinking: "🤔",
};

export function NorthyAssistant({
  message,
  mood,
}: {
  message: string;
  mood: Mood;
}) {
  return (
    <View style={styles.container}>
      <View style={styles.bag}>
        <Text style={styles.face}>{faceMap[mood]}</Text>
      </View>
      <View style={styles.bubble}>
        <Text style={styles.text}>{message}</Text>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flexDirection: "row",
    alignItems: "flex-start",
    gap: 12,
    padding: 16,
    borderRadius: 12,
    backgroundColor: "#eef2ff",
    borderWidth: 1,
    borderColor: Colors.primaryMuted,
  },
  bag: {
    width: 40,
    height: 40,
    borderRadius: 10,
    backgroundColor: Colors.primary,
    alignItems: "center",
    justifyContent: "center",
  },
  face: {
    fontSize: 20,
  },
  bubble: {
    flex: 1,
  },
  text: {
    fontSize: 13,
    lineHeight: 20,
    color: Colors.textSecondary,
  },
});
