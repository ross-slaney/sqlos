"use client";

import { useEffect, useState } from "react";

// Simple inline SVG logos for each provider
const GoogleLogo = () => (
  <svg viewBox="0 0 24 24" className="h-5 w-5">
    <path d="M22.56 12.25c0-.78-.07-1.53-.2-2.25H12v4.26h5.92a5.06 5.06 0 01-2.2 3.32v2.77h3.57c2.08-1.92 3.28-4.74 3.28-8.1z" fill="#4285F4"/>
    <path d="M12 23c2.97 0 5.46-.98 7.28-2.66l-3.57-2.77c-.98.66-2.23 1.06-3.71 1.06-2.86 0-5.29-1.93-6.16-4.53H2.18v2.84C3.99 20.53 7.7 23 12 23z" fill="#34A853"/>
    <path d="M5.84 14.09c-.22-.66-.35-1.36-.35-2.09s.13-1.43.35-2.09V7.07H2.18C1.43 8.55 1 10.22 1 12s.43 3.45 1.18 4.93l2.85-2.22.81-.62z" fill="#FBBC05"/>
    <path d="M12 5.38c1.62 0 3.06.56 4.21 1.64l3.15-3.15C17.45 2.09 14.97 1 12 1 7.7 1 3.99 3.47 2.18 7.07l3.66 2.84c.87-2.6 3.3-4.53 6.16-4.53z" fill="#EA4335"/>
  </svg>
);

const MicrosoftLogo = () => (
  <svg viewBox="0 0 24 24" className="h-5 w-5">
    <rect x="1" y="1" width="10" height="10" fill="#F25022"/>
    <rect x="13" y="1" width="10" height="10" fill="#7FBA00"/>
    <rect x="1" y="13" width="10" height="10" fill="#00A4EF"/>
    <rect x="13" y="13" width="10" height="10" fill="#FFB900"/>
  </svg>
);

const AppleLogo = () => (
  <svg viewBox="0 0 24 24" className="h-5 w-5" fill="white">
    <path d="M18.71 19.5c-.83 1.24-1.71 2.45-3.05 2.47-1.34.03-1.77-.79-3.29-.79-1.53 0-2 .77-3.27.82-1.31.05-2.3-1.32-3.14-2.53C4.25 17 2.94 12.45 4.7 9.39c.87-1.52 2.43-2.48 4.12-2.51 1.28-.02 2.5.87 3.29.87.78 0 2.26-1.07 3.8-.91.65.03 2.47.26 3.64 1.98-.09.06-2.17 1.28-2.15 3.81.03 3.02 2.65 4.03 2.68 4.04-.03.07-.42 1.44-1.38 2.83M13 3.5c.73-.83 1.94-1.46 2.94-1.5.13 1.17-.34 2.35-1.04 3.19-.69.85-1.83 1.51-2.95 1.42-.15-1.15.41-2.35 1.05-3.11z"/>
  </svg>
);

const OktaLogo = () => (
  <svg viewBox="0 0 24 24" className="h-5 w-5">
    <circle cx="12" cy="12" r="10" fill="none" stroke="white" strokeWidth="2.5"/>
    <circle cx="12" cy="12" r="4" fill="white"/>
  </svg>
);

const EntraLogo = () => (
  <svg viewBox="0 0 24 24" className="h-5 w-5" fill="white">
    <path d="M11.4 2L2 7.5V16l4.5 2.6V9l6.5-3.8V2h-1.6zM12.6 2v3.2L19.1 9v9.6L22 16V7.5L12.6 2zM12 9.5L5.5 13v7.5L12 24l6.5-3.5V13L12 9.5zm0 2.3l4 2.2v4.3L12 20.5 8 18.3V14l4-2.2z"/>
  </svg>
);

const CustomLogo = () => (
  <svg viewBox="0 0 24 24" className="h-5 w-5" fill="none" stroke="white" strokeWidth="1.5">
    <path strokeLinecap="round" strokeLinejoin="round" d="M17.25 6.75L22.5 12l-5.25 5.25m-10.5 0L1.5 12l5.25-5.25m7.5-3l-4.5 16.5"/>
  </svg>
);

const providers = [
  { name: "Google", color: "#ffffff", bg: "#ffffff", logo: <GoogleLogo /> },
  { name: "Microsoft", color: "#ffffff", bg: "#ffffff", logo: <MicrosoftLogo /> },
  { name: "Apple", color: "#ffffff", bg: "#1c1917", logo: <AppleLogo /> },
  { name: "Okta", color: "#007DC1", bg: "#007DC1", logo: <OktaLogo /> },
  { name: "Entra ID", color: "#0078D4", bg: "#0078D4", logo: <EntraLogo /> },
  { name: "Custom", color: "#6366f1", bg: "#6366f1", logo: <CustomLogo /> },
];

const R = 105;
const positions = providers.map((_, i) => {
  const angle = (i / providers.length) * 2 * Math.PI - Math.PI / 2;
  return { x: Math.cos(angle) * R, y: Math.sin(angle) * R };
});

export default function AuthStackViz() {
  const [activeIdx, setActiveIdx] = useState(-1);
  const [connected, setConnected] = useState<Set<number>>(new Set());

  useEffect(() => {
    const timers: ReturnType<typeof setTimeout>[] = [];

    providers.forEach((_, i) => {
      timers.push(
        setTimeout(() => {
          setActiveIdx(i);
          setConnected((prev) => new Set([...prev, i]));
        }, 600 + i * 500)
      );
    });

    const startCycle = 600 + providers.length * 500 + 600;
    timers.push(setTimeout(() => setActiveIdx(-1), startCycle));

    const interval = setInterval(() => {
      setActiveIdx((prev) => (prev + 1) % providers.length);
    }, 1600);

    return () => {
      timers.forEach(clearTimeout);
      clearInterval(interval);
    };
  }, []);

  const center = R + 40;

  return (
    <div className="flex items-center justify-center">
      <div className="relative" style={{ width: R * 2 + 80, height: R * 2 + 80 }}>
        {/* Connection lines */}
        <svg className="absolute inset-0 w-full h-full" viewBox={`0 0 ${R * 2 + 80} ${R * 2 + 80}`}>
          {providers.map((p, i) => {
            const isConnected = connected.has(i);
            const isActive = activeIdx === i;
            return (
              <line
                key={p.name}
                x1={center}
                y1={center}
                x2={center + positions[i].x}
                y2={center + positions[i].y}
                stroke={isConnected ? (isActive ? "rgba(255,255,255,0.3)" : "rgba(255,255,255,0.1)") : "transparent"}
                strokeWidth={isActive ? 2 : 1}
                className="transition-all duration-500"
              />
            );
          })}
        </svg>

        {/* Center hub */}
        <div
          className="absolute flex flex-col items-center"
          style={{ left: "50%", top: "50%", transform: "translate(-50%, -50%)" }}
        >
          <div className="flex h-14 w-14 items-center justify-center rounded-2xl bg-white/10 border border-white/20 backdrop-blur-sm">
            <span className="text-[11px] font-bold text-white tracking-tight">SqlOS</span>
          </div>
          <div className="mt-1.5 text-[10px] font-medium text-white/50">AuthServer</div>
        </div>

        {/* Provider nodes */}
        {providers.map((p, i) => {
          const isConnected = connected.has(i);
          const isActive = activeIdx === i;
          const pos = positions[i];

          return (
            <div
              key={p.name}
              className="absolute flex flex-col items-center transition-all duration-500"
              style={{
                left: "50%",
                top: "50%",
                transform: `translate(calc(-50% + ${pos.x}px), calc(-50% + ${pos.y}px))`,
                opacity: isConnected ? 1 : 0.2,
              }}
            >
              <div
                className="flex h-11 w-11 items-center justify-center rounded-xl transition-all duration-300 overflow-hidden"
                style={{
                  backgroundColor: isConnected ? p.bg : "#44403c",
                  boxShadow: isActive ? `0 0 28px ${p.color}40` : "none",
                  transform: isActive ? "scale(1.15)" : "scale(1)",
                }}
              >
                {p.logo}
              </div>
              <div
                className="mt-1.5 text-[9px] font-medium transition-colors duration-300 whitespace-nowrap"
                style={{ color: isActive ? "#ffffff" : "rgba(255,255,255,0.35)" }}
              >
                {p.name}
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
