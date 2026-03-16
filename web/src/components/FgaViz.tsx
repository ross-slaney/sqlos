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

const typeIcons: Record<string, string> = {
  organization: "O",
  workspace: "W",
  chain: "C",
  store: "S",
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
    <div>
      <div className="overflow-hidden rounded-xl border border-stone-200 bg-white shadow-[0_8px_40px_rgba(0,0,0,0.06)]">
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
        <div className="p-4">
          <TreeNodeComponent
            node={resourceTree}
            activeGrant={activeGrant}
            expandedNodes={expandedNodes}
          />

          {/* Legend */}
          <div className="mt-4 flex flex-wrap gap-3 border-t border-stone-100 pt-4">
            {Object.entries(typeColors).map(([type, color]) => (
              <div key={type} className="flex items-center gap-1.5">
                <span className="h-2 w-2 rounded-sm" style={{ backgroundColor: color }} />
                <span className="text-[10px] text-stone-400 capitalize">{type}</span>
              </div>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}
