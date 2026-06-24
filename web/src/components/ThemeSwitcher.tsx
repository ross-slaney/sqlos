"use client";

import * as DropdownMenu from "@radix-ui/react-dropdown-menu";
import { Check, Monitor, MoonStar, Palette, SunMedium } from "lucide-react";
import { useTheme } from "next-themes";
import { useEffect, useMemo, useState, useSyncExternalStore, type ReactNode } from "react";

const STORAGE_KEY = "sqlos-site-accent";

const accents = [
  { id: "violet", label: "Violet", hue: 262 },
  { id: "blue", label: "Blue", hue: 217 },
  { id: "teal", label: "Teal", hue: 172 },
  { id: "amber", label: "Amber", hue: 32 },
] as const;

type AccentId = (typeof accents)[number]["id"];

export default function ThemeSwitcher({ className }: { className?: string }) {
  const { resolvedTheme, theme, setTheme } = useTheme();
  const mounted = useSyncExternalStore(subscribe, getClientSnapshot, getServerSnapshot);
  const [accent, setAccent] = useState<AccentId>(() => {
    if (typeof window === "undefined") {
      return "violet";
    }

    const saved = window.localStorage.getItem(STORAGE_KEY);
    return isAccentId(saved) ? saved : "violet";
  });

  useEffect(() => {
    const mode = resolvedTheme === "dark" ? "dark" : "light";
    document.querySelectorAll<HTMLElement>(".sqlos-docs-shell").forEach((node) => {
      node.dataset.emcydocsMode = mode;
    });
  }, [resolvedTheme]);

  useEffect(() => {
    if (!mounted) {
      return;
    }

    document.documentElement.dataset.theme = accent;
    window.localStorage.setItem(STORAGE_KEY, accent);
  }, [accent, mounted]);

  const currentHue = useMemo(
    () => accents.find((item) => item.id === accent)?.hue ?? accents[0].hue,
    [accent]
  );

  const handleAccentChange = (value: AccentId) => {
    setAccent(value);
  };

  const currentMode = mounted ? theme ?? "system" : "system";

  return (
    <DropdownMenu.Root>
      <DropdownMenu.Trigger asChild>
        <button
          type="button"
          className={[
            "inline-flex items-center gap-2 rounded-full border border-border/70 bg-card/70 px-3 py-2 text-xs font-semibold text-foreground shadow-[0_16px_36px_-26px_var(--shadow-color)] backdrop-blur-xl",
            "hover:border-border hover:bg-card",
            className ?? "",
          ]
            .filter(Boolean)
            .join(" ")}
          aria-label="Change SqlOS theme"
        >
          <span
            className="h-4 w-4 rounded-full border border-white/25"
            style={{
              background: `linear-gradient(135deg, hsl(${currentHue} 88% 72%), hsl(${currentHue} 74% 48%))`,
            }}
          />
          <Palette className="h-3.5 w-3.5 text-muted-foreground" />
          <span className="hidden sm:inline">Theme</span>
        </button>
      </DropdownMenu.Trigger>

      <DropdownMenu.Portal>
        <DropdownMenu.Content
          align="end"
          sideOffset={10}
          className="z-50 w-72 rounded-3xl border border-border/80 bg-popover/95 p-3 text-popover-foreground shadow-[0_36px_90px_-44px_var(--shadow-color)] backdrop-blur-2xl"
        >
          <div className="mb-3">
            <p className="text-[0.68rem] font-semibold uppercase tracking-[0.18em] text-muted-foreground">
              Appearance
            </p>
            <p className="mt-1 text-sm text-muted-foreground">
              Keep the docs shell and marketing pages in the same theme mode and accent.
            </p>
          </div>

          <div className="grid grid-cols-3 gap-2">
            <ModeButton
              active={currentMode === "light"}
              icon={<SunMedium className="h-4 w-4" />}
              label="Light"
              onClick={() => setTheme("light")}
            />
            <ModeButton
              active={currentMode === "dark"}
              icon={<MoonStar className="h-4 w-4" />}
              label="Dark"
              onClick={() => setTheme("dark")}
            />
            <ModeButton
              active={currentMode === "system"}
              icon={<Monitor className="h-4 w-4" />}
              label="System"
              onClick={() => setTheme("system")}
            />
          </div>

          <DropdownMenu.Separator className="my-3 h-px bg-border" />

          <div>
            <p className="mb-2 text-[0.68rem] font-semibold uppercase tracking-[0.18em] text-muted-foreground">
              Accent
            </p>
            <div className="grid grid-cols-2 gap-2">
              {accents.map((item) => {
                const isActive = accent === item.id;
                return (
                  <button
                    key={item.id}
                    type="button"
                    onClick={() => handleAccentChange(item.id)}
                    className={[
                      "flex items-center gap-3 rounded-2xl border px-3 py-3 text-left text-xs font-semibold",
                      isActive
                        ? "border-primary/30 bg-accent text-accent-foreground"
                        : "border-border/70 bg-card/60 text-muted-foreground hover:border-border hover:bg-card",
                    ].join(" ")}
                  >
                    <span
                      className="relative h-5 w-5 rounded-full border border-white/25"
                      style={{
                        background: `linear-gradient(135deg, hsl(${item.hue} 88% 72%), hsl(${item.hue} 74% 48%))`,
                      }}
                    >
                      {isActive ? (
                        <Check className="absolute inset-0 m-auto h-3 w-3 text-white" />
                      ) : null}
                    </span>
                    <span>{item.label}</span>
                  </button>
                );
              })}
            </div>
          </div>
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}

function subscribe() {
  return () => {};
}

function getClientSnapshot() {
  return true;
}

function getServerSnapshot() {
  return false;
}

function isAccentId(value: string | null): value is AccentId {
  return accents.some((item) => item.id === value);
}

function ModeButton({
  active,
  icon,
  label,
  onClick,
}: {
  active: boolean;
  icon: ReactNode;
  label: string;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={[
        "flex flex-col items-center justify-center gap-2 rounded-2xl border px-2 py-3 text-[11px] font-medium",
        active
          ? "border-primary/30 bg-accent text-accent-foreground"
          : "border-border/70 bg-card/60 text-muted-foreground hover:border-border hover:bg-card",
      ].join(" ")}
    >
      {icon}
      <span>{label}</span>
    </button>
  );
}
