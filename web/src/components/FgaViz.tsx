"use client";

import { Box, Building2, Check, GitBranch, ShieldCheck, Store } from "lucide-react";
import type { ReactNode } from "react";
import { useEffect, useState } from "react";

type ResourceNode = {
  id: string;
  type: string;
  label: string;
  icon: ReactNode;
  children?: ResourceNode[];
};

const resources: ResourceNode = {
  id: "org",
  type: "org",
  label: "Acme Corp",
  icon: <Building2 className="h-4 w-4" />,
  children: [
    {
      id: "workspace",
      type: "workspace",
      label: "North America",
      icon: <GitBranch className="h-4 w-4" />,
      children: [
        {
          id: "chain",
          type: "chain",
          label: "Flagship Retail",
          icon: <Box className="h-4 w-4" />,
          children: [
            { id: "store-seattle", type: "store", label: "Seattle #01", icon: <Store className="h-4 w-4" /> },
            { id: "store-portland", type: "store", label: "Portland #04", icon: <Store className="h-4 w-4" /> },
          ],
        },
      ],
    },
  ],
};

const grants = [
  {
    nodeId: "workspace",
    subject: "Sarah Chen",
    role: "Owner",
    permission: "workspace.*",
  },
  {
    nodeId: "chain",
    subject: "James Miller",
    role: "Operator",
    permission: "chain.write, store.read",
  },
  {
    nodeId: "store-seattle",
    subject: "Alex Torres",
    role: "Manager",
    permission: "store.*, inventory.*",
  },
] as const;

export default function FgaViz() {
  const [activeGrant, setActiveGrant] = useState(0);

  useEffect(() => {
    const prefersReducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    if (prefersReducedMotion) {
      return;
    }

    const interval = setInterval(() => {
      setActiveGrant((current) => (current + 1) % grants.length);
    }, 2600);

    return () => clearInterval(interval);
  }, []);

  const grant = grants[activeGrant] ?? grants[0];

  return (
    <div className="neon-panel overflow-hidden rounded-lg">
      <div className="flex items-center justify-between border-b border-border/70 bg-muted/45 px-4 py-3">
        <div className="flex items-center gap-2">
          <span className="flex h-7 w-7 items-center justify-center rounded-md border border-neon-cyan/35 bg-neon-cyan/10 text-neon-cyan">
            <ShieldCheck className="h-4 w-4" />
          </span>
          <div>
            <p className="text-sm font-semibold text-foreground">Resource hierarchy</p>
            <p className="font-mono text-[10px] text-muted-foreground">role grants cascade down</p>
          </div>
        </div>
        <span className="flex items-center gap-1.5 font-mono text-[10px] text-neon-green">
          <span className="h-1.5 w-1.5 animate-pulse rounded-full bg-neon-green" />
          live
        </span>
      </div>

      <div className="grid gap-5 p-4 lg:grid-cols-[1fr_0.82fr]">
        <div className="rounded-lg border border-border/70 bg-background/70 p-3">
          <TreeNode node={resources} activeNodeId={grant.nodeId} />
        </div>

        <div className="rounded-lg border border-neon-green/25 bg-neon-green/10 p-4">
          <p className="font-mono text-[11px] uppercase text-neon-green">active grant</p>
          <h3 className="mt-2 text-xl font-semibold text-foreground">{grant.subject}</h3>
          <div className="mt-4 space-y-3 text-sm">
            <GrantFact label="Role" value={grant.role} />
            <GrantFact label="Permission" value={grant.permission} />
            <GrantFact label="Scope" value={grant.nodeId} />
          </div>
          <div className="mt-5 flex items-center gap-2 rounded-md border border-neon-cyan/25 bg-background/70 px-3 py-2 text-xs text-muted-foreground">
            <Check className="h-4 w-4 text-neon-cyan" />
            SQL query receives only authorized rows.
          </div>
        </div>
      </div>
    </div>
  );
}

function TreeNode({
  node,
  activeNodeId,
  depth = 0,
}: {
  node: ResourceNode;
  activeNodeId: string;
  depth?: number;
}) {
  const isActive = node.id === activeNodeId;

  return (
    <div>
      <div
        className={[
          "mb-2 flex items-center gap-3 rounded-md border px-3 py-2 transition-colors",
          isActive
            ? "border-neon-cyan/45 bg-neon-cyan/12 text-neon-cyan shadow-[0_0_22px_oklch(0.82_0.17_200_/_0.14)]"
            : "border-border/70 bg-card/50 text-foreground",
        ].join(" ")}
        style={{ marginLeft: depth * 18 }}
      >
        <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-md border border-current/30 bg-background/55">
          {node.icon}
        </span>
        <div className="min-w-0">
          <p className="truncate text-sm font-semibold">{node.label}</p>
          <p className="font-mono text-[10px] text-muted-foreground">{node.type}</p>
        </div>
      </div>
      {node.children?.map((child) => (
        <TreeNode key={child.id} node={child} activeNodeId={activeNodeId} depth={depth + 1} />
      ))}
    </div>
  );
}

function GrantFact({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-md border border-border/70 bg-background/72 px-3 py-2">
      <p className="font-mono text-[10px] uppercase text-muted-foreground">{label}</p>
      <p className="mt-1 font-mono text-xs text-foreground">{value}</p>
    </div>
  );
}
