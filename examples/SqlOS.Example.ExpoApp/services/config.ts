import { Platform } from "react-native";

// Android emulator uses 10.0.2.2 to reach host machine's localhost
const localhost = Platform.OS === "android" ? "10.0.2.2" : "localhost";

export const API_URL = `http://${localhost}:5062`;
export const CLIENT_ID = "example-web";
