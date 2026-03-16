"use client";

import { useEffect, useState } from "react";

const modules = [
  { key: "auth", name: "AuthServer", desc: "OAuth 2.0, sessions, users, orgs", color: "#059669" },
  { key: "page", name: "AuthPage", desc: "Login, signup, social, SSO", color: "#0d9488" },
  { key: "fga", name: "FGA", desc: "Roles, grants, query filters", color: "#0891b2" },
  { key: "dash", name: "Dashboard", desc: "Admin UI, audit, settings", color: "#6366f1" },
];

export default function ArchitectureViz() {
  const [activeIdx, setActiveIdx] = useState(-1);

  useEffect(() => {
    const timer = setTimeout(() => setActiveIdx(0), 600);
    const interval = setInterval(() => {
      setActiveIdx((i) => (i + 1) % modules.length);
    }, 2200);
    return () => { clearTimeout(timer); clearInterval(interval); };
  }, []);

  return (
    <div className="overflow-hidden rounded-xl border border-stone-200 bg-white shadow-[0_8px_40px_rgba(0,0,0,0.05)]">
      {/* Header */}
      <div className="border-b border-stone-100 bg-stone-50 px-5 py-3 flex items-center justify-between">
        <span className="text-[12px] font-semibold text-stone-700">Your ASP.NET App</span>
        <span className="text-[10px] text-stone-400">single process</span>
      </div>

      <div className="p-5">
        {/* App container with modules */}
        <div className="rounded-lg border border-stone-150 bg-stone-50/50 p-4">
          <div className="grid grid-cols-2 gap-2">
            {modules.map((mod, i) => {
              const isActive = activeIdx === i;
              return (
                <div
                  key={mod.key}
                  className="rounded-md border px-3 py-2.5 transition-all duration-300 cursor-default"
                  style={{
                    borderColor: isActive ? mod.color : "#e7e5e4",
                    backgroundColor: isActive ? `${mod.color}06` : "white",
                  }}
                  onMouseEnter={() => setActiveIdx(i)}
                >
                  <div className="flex items-center gap-2">
                    <span
                      className="h-1.5 w-1.5 rounded-full transition-colors duration-300"
                      style={{ backgroundColor: isActive ? mod.color : "#d6d3d1" }}
                    />
                    <span className="text-[12px] font-semibold text-stone-800">{mod.name}</span>
                  </div>
                  <div className="mt-0.5 ml-3.5 text-[10px] text-stone-400">{mod.desc}</div>
                </div>
              );
            })}
          </div>
        </div>

        {/* Connection to DB */}
        <div className="flex justify-center py-2">
          <div className="h-6 w-px bg-stone-200" />
        </div>

        <div className="rounded-md border border-stone-200 bg-stone-50 px-4 py-2.5 flex items-center justify-between">
          <div className="flex items-center gap-2">
            <span className="flex h-5 w-5 items-center justify-center rounded bg-stone-200 text-[8px] font-bold text-stone-500">DB</span>
            <span className="text-[12px] font-semibold text-stone-700">Your SQL Server</span>
          </div>
          <span className="text-[10px] text-stone-400">auth + business data together</span>
        </div>

        {/* Key points */}
        <div className="mt-4 flex flex-wrap gap-x-4 gap-y-1 text-[11px] text-stone-400">
          <span>In-process</span>
          <span>·</span>
          <span>One schema</span>
          <span>·</span>
          <span>JOINable</span>
          <span>·</span>
          <span>No network hops</span>
        </div>
      </div>
    </div>
  );
}
