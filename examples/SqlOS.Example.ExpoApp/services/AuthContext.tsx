import React, { createContext, useContext, useState, useEffect, useCallback } from "react";
import * as auth from "./auth";
import type { SessionData } from "./types";

type AuthContextType = {
  session: SessionData | null;
  isLoading: boolean;
  isAuthenticated: boolean;
  refresh: () => Promise<void>;
  login: (data: SessionData) => Promise<void>;
  logout: () => Promise<void>;
};

const AuthContext = createContext<AuthContextType>({
  session: null,
  isLoading: true,
  isAuthenticated: false,
  refresh: async () => {},
  login: async () => {},
  logout: async () => {},
});

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [session, setSession] = useState<SessionData | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  const refresh = useCallback(async () => {
    const s = await auth.getSession();
    setSession(s);
  }, []);

  useEffect(() => {
    auth.getSession().then((s) => {
      setSession(s);
      setIsLoading(false);
    });
  }, []);

  const login = useCallback(async (data: SessionData) => {
    await auth.setSession(data);
    setSession(data);
  }, []);

  const logout = useCallback(async () => {
    await auth.signOut();
    setSession(null);
  }, []);

  return (
    <AuthContext.Provider
      value={{
        session,
        isLoading,
        isAuthenticated: !!session?.accessToken,
        refresh,
        login,
        logout,
      }}
    >
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  return useContext(AuthContext);
}
