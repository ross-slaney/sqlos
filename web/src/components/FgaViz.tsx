"use client";

import { useEffect, useState } from "react";

interface TreeNode {
  id: string;
  type: string;
  name: string;
  children?: TreeNode[];
}

const resourceTree: TreeNode = {
  id: "org-1",
  type: "organization",
  name: "Acme Corp",
  children: [
    {
      id: "ws-1",
      type: "workspace",
      name: "North America",
      children: [
        {
          id: "chain-1",
          type: "chain",
          name: "Flagship Retail",
          children: [
            { id: "store-1", type: "store", name: "Seattle #01" },
            { id: "store-2", type: "store", name: "Portland #04" },
          ],
        },
        {
          id: "chain-2",
          type: "chain",
          name: "Express Outlets",
          children: [
            { id: "store-3", type: "store", name: "Denver #12" },
          ],
        },
      ],
    },
  ],
};

const typeColors: Record<string, string> = {
  organization: "#6366f1",
  workspace: "#2563eb",
  chain: "#0891b2",
  store: "#059669",
};

const typeIcons: Record<string, React.ReactNode> = {
  organization: (
    // Building / office
    <svg className="h-3.5 w-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M3.75 21h16.5M4.5 3h15M5.25 3v18m13.5-18v18M9 6.75h1.5m-1.5 3h1.5m-1.5 3h1.5m3-6H15m-1.5 3H15m-1.5 3H15M9 21v-3.375c0-.621.504-1.125 1.125-1.125h3.75c.621 0 1.125.504 1.125 1.125V21" />
    </svg>
  ),
  workspace: (
    // Grid / layout
    <svg className="h-3.5 w-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M3.75 6A2.25 2.25 0 016 3.75h2.25A2.25 2.25 0 0110.5 6v2.25a2.25 2.25 0 01-2.25 2.25H6a2.25 2.25 0 01-2.25-2.25V6zM3.75 15.75A2.25 2.25 0 016 13.5h2.25a2.25 2.25 0 012.25 2.25V18a2.25 2.25 0 01-2.25 2.25H6A2.25 2.25 0 013.75 18v-2.25zM13.5 6a2.25 2.25 0 012.25-2.25H18A2.25 2.25 0 0120.25 6v2.25A2.25 2.25 0 0118 10.5h-2.25a2.25 2.25 0 01-2.25-2.25V6zM13.5 15.75a2.25 2.25 0 012.25-2.25H18a2.25 2.25 0 012.25 2.25V18A2.25 2.25 0 0118 20.25h-2.25A2.25 2.25 0 0113.5 18v-2.25z" />
    </svg>
  ),
  chain: (
    // Link / chain
    <svg className="h-3.5 w-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M13.19 8.688a4.5 4.5 0 011.242 7.244l-4.5 4.5a4.5 4.5 0 01-6.364-6.364l1.757-1.757m13.35-.622l1.757-1.757a4.5 4.5 0 00-6.364-6.364l-4.5 4.5a4.5 4.5 0 001.242 7.244" />
    </svg>
  ),
  store: (
    // Storefront
    <svg className="h-3.5 w-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 21v-7.5a.75.75 0 01.75-.75h3a.75.75 0 01.75.75V21m-4.5 0H2.36m11.14 0H18m0 0h3.64m-1.39 0V9.349m-16.5 11.65V9.35m0 0a3.001 3.001 0 003.75-.615A2.993 2.993 0 009.75 9.75c.896 0 1.7-.393 2.25-1.016a2.993 2.993 0 002.25 1.016c.896 0 1.7-.393 2.25-1.016a3.001 3.001 0 003.75.614m-16.5 0a3.004 3.004 0 01-.621-4.72L4.318 3.44A1.5 1.5 0 015.378 3h13.243a1.5 1.5 0 011.06.44l1.19 1.189a3 3 0 01-.621 4.72m-13.5 8.65h3.75a.75.75 0 00.75-.75V13.5a.75.75 0 00-.75-.75H6.75a.75.75 0 00-.75.75v3.75c0 .415.336.75.75.75z" />
    </svg>
  ),
};

interface Grant {
  nodeId: string;
  role: string;
  user: { name: string; initials: string; color: string; avatar: string };
  permission: string;
}

const grantSequence: Grant[] = [
  {
    nodeId: "ws-1",
    role: "Owner",
    user: { name: "Sarah Chen", initials: "SC", color: "#6366f1", avatar: "👩🏻‍💼" },
    permission: "workspace.*",
  },
  {
    nodeId: "chain-1",
    role: "Operator",
    user: { name: "James Miller", initials: "JM", color: "#2563eb", avatar: "👨🏽‍🔧" },
    permission: "chain.write, store.read",
  },
  {
    nodeId: "store-1",
    role: "Manager",
    user: { name: "Alex Torres", initials: "AT", color: "#059669", avatar: "👩🏼‍💻" },
    permission: "store.*, inventory.*",
  },
];

function GrantCard({ grant, nodeType }: { grant: Grant; nodeType: string }) {
  return (
    <div
      className="mt-1 mb-2 rounded-lg border border-stone-200 bg-white p-3 shadow-sm"
      style={{
        animation: "grantSlideDown 0.3s ease-out",
      }}
    >
      {/* User row */}
      <div className="flex items-center gap-2.5">
        <div
          className="relative flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-sm"
          style={{ backgroundColor: `${grant.user.color}14` }}
        >
          <span className="text-[14px]">{grant.user.avatar}</span>
          {/* Online dot */}
          <span
            className="absolute -bottom-0.5 -right-0.5 h-2.5 w-2.5 rounded-full border-2 border-white"
            style={{ backgroundColor: "#22c55e" }}
          />
        </div>
        <div className="min-w-0 flex-1">
          <div className="text-[12px] font-semibold text-stone-900">
            {grant.user.name}
          </div>
          <div className="text-[10px] text-stone-400">User</div>
        </div>
        <span
          className="shrink-0 rounded-full px-2.5 py-0.5 text-[10px] font-semibold text-white"
          style={{ backgroundColor: typeColors[nodeType] }}
        >
          {grant.role}
        </span>
      </div>

      {/* Grants row */}
      <div className="mt-2.5 flex items-center gap-2 rounded-md bg-stone-50 px-2.5 py-2">
        <span className="text-[10px] font-medium text-stone-400 uppercase tracking-wider shrink-0">
          Grants
        </span>
        <span className="text-[11px] text-stone-600 font-mono">
          {grant.permission}
        </span>
      </div>

      {/* Inherits indicator */}
      <div className="mt-2 flex items-center gap-1.5 text-[10px] text-stone-400">
        <svg className="h-3 w-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M19 14l-7 7m0 0l-7-7m7 7V3" />
        </svg>
        Inherits to child resources
      </div>
    </div>
  );
}

function TreeNodeComponent({
  node,
  depth = 0,
  activeGrant,
  expandedNodes,
}: {
  node: TreeNode;
  depth?: number;
  activeGrant: Grant | null;
  expandedNodes: Set<string>;
}) {
  const isActive = activeGrant?.nodeId === node.id;
  const isExpanded = expandedNodes.has(node.id);
  const hasChildren = node.children && node.children.length > 0;
  const indent = depth * 20;

  return (
    <div>
      {/* Node row */}
      <div
        className="flex items-center gap-2.5 rounded-lg px-3 py-2 transition-all duration-300"
        style={{
          marginLeft: indent,
          backgroundColor: isActive ? `${typeColors[node.type]}06` : "transparent",
          borderLeft: isActive ? `2px solid ${typeColors[node.type]}` : "2px solid transparent",
        }}
      >
        {/* Connector dash */}
        {depth > 0 && (
          <span className="text-stone-300 text-[10px] -ml-1 select-none">&mdash;</span>
        )}

        {/* Type badge */}
        <span
          className="flex h-6 w-6 shrink-0 items-center justify-center rounded-md text-[10px] font-bold text-white transition-transform duration-300"
          style={{
            backgroundColor: typeColors[node.type],
            transform: isActive ? "scale(1.15)" : "scale(1)",
          }}
        >
          {typeIcons[node.type]}
        </span>

        {/* Label */}
        <div className="min-w-0 flex-1">
          <div className="text-[12px] font-semibold text-stone-800 truncate">
            {node.name}
          </div>
          <div className="text-[10px] text-stone-400">{node.type}</div>
        </div>

        {/* Avatar hint when active */}
        {isActive && activeGrant && (
          <span
            className="flex h-6 w-6 items-center justify-center rounded-full text-[12px]"
            style={{
              backgroundColor: `${activeGrant.user.color}14`,
              animation: "grantFadeIn 0.25s ease-out",
            }}
          >
            {activeGrant.user.avatar}
          </span>
        )}
      </div>

      {/* Grant card — renders inline below the node row */}
      {isActive && activeGrant && (
        <div style={{ marginLeft: indent + 12 }}>
          <GrantCard grant={activeGrant} nodeType={node.type} />
        </div>
      )}

      {/* Children */}
      {hasChildren && isExpanded && (
        <div>
          {node.children!.map((child) => (
            <TreeNodeComponent
              key={child.id}
              node={child}
              depth={depth + 1}
              activeGrant={activeGrant}
              expandedNodes={expandedNodes}
            />
          ))}
        </div>
      )}
    </div>
  );
}

export default function FgaViz() {
  const [activeGrantIndex, setActiveGrantIndex] = useState(-1);
  const [expandedNodes, setExpandedNodes] = useState<Set<string>>(new Set());

  useEffect(() => {
    const timers: ReturnType<typeof setTimeout>[] = [];

    // Expand tree progressively
    timers.push(setTimeout(() => setExpandedNodes(new Set(["org-1"])), 300));
    timers.push(setTimeout(() => setExpandedNodes(new Set(["org-1", "ws-1"])), 600));
    timers.push(setTimeout(() => setExpandedNodes(new Set(["org-1", "ws-1", "chain-1", "chain-2"])), 900));

    // First cycle
    timers.push(setTimeout(() => setActiveGrantIndex(0), 1600));
    timers.push(setTimeout(() => setActiveGrantIndex(1), 4200));
    timers.push(setTimeout(() => setActiveGrantIndex(2), 6800));
    timers.push(setTimeout(() => setActiveGrantIndex(-1), 9400));

    const loop = setInterval(() => {
      setActiveGrantIndex(-1);
      timers.push(setTimeout(() => setActiveGrantIndex(0), 600));
      timers.push(setTimeout(() => setActiveGrantIndex(1), 3200));
      timers.push(setTimeout(() => setActiveGrantIndex(2), 5800));
      timers.push(setTimeout(() => setActiveGrantIndex(-1), 8400));
    }, 9600);

    return () => {
      timers.forEach(clearTimeout);
      clearInterval(loop);
    };
  }, []);

  const activeGrant = activeGrantIndex >= 0 ? grantSequence[activeGrantIndex] : null;

  return (
    <div className="relative h-[520px] overflow-hidden rounded-xl border border-stone-200 bg-white shadow-[0_8px_40px_rgba(0,0,0,0.06)]">
      {/* Header */}
      <div className="flex items-center justify-between border-b border-stone-100 bg-stone-50 px-4 py-2.5">
        <div className="flex items-center gap-2">
          <div className="flex h-5 w-5 items-center justify-center rounded bg-stone-900 text-[8px] font-bold text-white">
            FGA
          </div>
          <span className="text-[12px] font-semibold text-stone-700">
            Resource Hierarchy
          </span>
        </div>
        <div className="flex items-center gap-1.5">
          <span className="h-1.5 w-1.5 rounded-full bg-emerald-400 animate-pulse" />
          <span className="text-[10px] text-stone-400">Live</span>
        </div>
      </div>

      {/* Tree */}
      <div className="p-4 pb-16">
        <TreeNodeComponent
          node={resourceTree}
          activeGrant={activeGrant}
          expandedNodes={expandedNodes}
        />
      </div>

      {/* Legend — pinned to bottom */}
      <div className="absolute bottom-0 left-0 right-0 flex flex-wrap gap-4 border-t border-stone-100 bg-white px-4 py-3">
        {Object.entries(typeColors).map(([type, color]) => (
          <div key={type} className="flex items-center gap-1.5">
            <span
              className="flex h-4 w-4 items-center justify-center rounded text-white"
              style={{ backgroundColor: color }}
            >
              <span className="scale-[0.7]">{typeIcons[type]}</span>
            </span>
            <span className="text-[10px] text-stone-400 capitalize">{type}</span>
          </div>
        ))}
      </div>
    </div>
  );
}
