"use client";

const setupCode = `builder.AddSqlOS<AppDbContext>();

var app = builder.Build();
app.MapSqlOS();`;

function DashboardMockup() {
  return (
    <div className="overflow-hidden rounded-xl border border-stone-200 bg-white shadow-[0_8px_40px_rgba(0,0,0,0.08)]">
      {/* Browser bar */}
      <div className="flex items-center gap-2 border-b border-stone-100 bg-stone-50 px-3 py-2">
        <div className="flex gap-1.5">
          <span className="h-2 w-2 rounded-full bg-stone-200" />
          <span className="h-2 w-2 rounded-full bg-stone-200" />
          <span className="h-2 w-2 rounded-full bg-stone-200" />
        </div>
        <div className="flex-1 truncate rounded-md bg-white border border-stone-150 px-2.5 py-0.5 text-[9px] text-stone-400 text-center">
          localhost:5062/sqlos/admin/auth
        </div>
      </div>

      <div className="flex">
        {/* Sidebar */}
        <div className="hidden sm:block w-[100px] shrink-0 border-r border-stone-100 bg-[#1c1917] p-2">
          <div className="flex items-center gap-1.5 px-1 mb-3">
            <div className="flex h-4 w-4 items-center justify-center rounded bg-emerald-600 text-[6px] font-bold text-white">SO</div>
            <span className="text-[9px] font-bold text-white/90">SqlOS</span>
          </div>
          <div className="text-[6px] font-semibold text-white/30 uppercase tracking-wider px-1 mb-1">Auth Server</div>
          {["Overview", "Organizations", "Users", "Clients", "OIDC", "Auth Page", "Sessions"].map((item, i) => (
            <div
              key={item}
              className="px-1.5 py-[2px] rounded text-[7px] mb-[1px]"
              style={{
                backgroundColor: i === 2 ? "rgba(255,255,255,0.1)" : "transparent",
                color: i === 2 ? "#ffffff" : "rgba(255,255,255,0.35)",
                fontWeight: i === 2 ? 600 : 400,
              }}
            >
              {item}
            </div>
          ))}
          <div className="text-[6px] font-semibold text-white/30 uppercase tracking-wider px-1 mt-2 mb-1">FGA</div>
          {["Resources", "Grants", "Roles"].map((item) => (
            <div key={item} className="px-1.5 py-[2px] rounded text-[7px] text-white/35 mb-[1px]">{item}</div>
          ))}
        </div>

        {/* Main */}
        <div className="flex-1 min-w-0 p-3">
          <div className="flex items-center justify-between mb-2">
            <div>
              <div className="text-[7px] font-semibold text-emerald-600 uppercase tracking-wider">Auth Server</div>
              <div className="text-[13px] font-bold text-stone-900">Users</div>
            </div>
            <div className="rounded-md bg-stone-900 px-2 py-0.5 text-[7px] font-semibold text-white">+ Create</div>
          </div>

          {/* Stats */}
          <div className="grid grid-cols-4 gap-1.5 mb-2">
            {[
              { label: "Users", value: "11" },
              { label: "Sessions", value: "4" },
              { label: "Orgs", value: "2" },
              { label: "Providers", value: "3" },
            ].map((s) => (
              <div key={s.label} className="rounded border border-stone-100 bg-stone-50/50 px-1.5 py-1">
                <div className="text-[6px] text-stone-400">{s.label}</div>
                <div className="text-[12px] font-bold text-stone-900">{s.value}</div>
              </div>
            ))}
          </div>

          {/* Table */}
          <div className="rounded border border-stone-100 overflow-hidden">
            <div className="grid grid-cols-[1fr_60px_40px_32px] bg-stone-50 border-b border-stone-100 px-2 py-1">
              <span className="text-[6px] font-semibold text-stone-400 uppercase tracking-wider">User</span>
              <span className="text-[6px] font-semibold text-stone-400 uppercase tracking-wider">Provider</span>
              <span className="text-[6px] font-semibold text-stone-400 uppercase tracking-wider">Status</span>
              <span className="text-[6px] font-semibold text-stone-400 uppercase tracking-wider text-right">Logins</span>
            </div>
            {[
              { name: "Sarah Chen", email: "sarah@acme.co", i: "SC", c: "#6366f1", p: "Entra ID", n: 86 },
              { name: "James Miller", email: "james@acme.co", i: "JM", c: "#2563eb", p: "Google", n: 42 },
              { name: "Alex Torres", email: "alex@acme.co", i: "AT", c: "#059669", p: "Password", n: 15 },
            ].map((u) => (
              <div key={u.email} className="grid grid-cols-[1fr_60px_40px_32px] items-center border-t border-stone-50 px-2 py-1">
                <div className="flex items-center gap-1.5">
                  <div className="flex h-4 w-4 items-center justify-center rounded-full text-[6px] font-bold text-white" style={{ backgroundColor: u.c }}>{u.i}</div>
                  <div>
                    <div className="text-[8px] font-medium text-stone-800">{u.name}</div>
                    <div className="text-[6px] text-stone-400">{u.email}</div>
                  </div>
                </div>
                <span className="text-[7px] text-stone-500">{u.p}</span>
                <span className="flex items-center gap-0.5 text-[7px] text-emerald-600"><span className="h-1 w-1 rounded-full bg-emerald-500" />Active</span>
                <span className="text-[7px] text-stone-500 text-right">{u.n}</span>
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

export default function HeroViz() {
  return (
    <div className="relative min-h-[320px] lg:min-h-[420px]">
      {/* Dashboard — back layer, positioned lower-right */}
      <div className="absolute top-28 right-0 z-10 w-[95%] lg:-right-4">
        <DashboardMockup />
      </div>

      {/* Code block — front layer, positioned upper-left */}
      <div className="relative z-20 w-[85%] sm:w-[75%] overflow-hidden rounded-xl border border-stone-800 bg-stone-950 shadow-[0_20px_60px_rgba(0,0,0,0.2)]">
        <div className="flex items-center gap-1.5 border-b border-white/10 px-4 py-2.5">
          <span className="h-2.5 w-2.5 rounded-full bg-[#ff5f57]" />
          <span className="h-2.5 w-2.5 rounded-full bg-[#febc2e]" />
          <span className="h-2.5 w-2.5 rounded-full bg-[#28c840]" />
          <span className="ml-3 text-[11px] text-stone-500">Program.cs</span>
        </div>
        <pre className="overflow-x-auto px-3 sm:px-5 py-4 font-mono text-[10px] sm:text-[11px] leading-[1.8] text-stone-300">
          <code>{setupCode}</code>
        </pre>
      </div>
    </div>
  );
}
