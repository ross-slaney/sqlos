"use client";

import { useState } from "react";

export default function InstallCommand({
  command = "dotnet add package SqlOS",
  className,
}: {
  command?: string;
  className?: string;
}) {
  const [copied, setCopied] = useState(false);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(command);
      setCopied(true);
      setTimeout(() => setCopied(false), 1600);
    } catch {
      /* clipboard unavailable — ignore */
    }
  };

  return (
    <button
      type="button"
      onClick={copy}
      className={[
        "inline-flex items-center gap-2.5 rounded-[6px] bg-[#0b0d12] px-3.5 py-2.5 font-mono text-[13.5px] font-medium text-[#e5e7eb] shadow-sm",
        className ?? "",
      ].join(" ")}
      aria-label={`Copy ${command}`}
    >
      <span className="select-none text-[#6ee7a8]">$</span>
      <span>{command}</span>
      <span className="ml-0.5 text-[#71717a] transition-colors hover:text-[#e5e7eb]">
        {copied ? (
          <svg
            className="h-3.5 w-3.5"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="2"
            strokeLinecap="round"
            strokeLinejoin="round"
          >
            <path d="M20 6L9 17l-5-5" />
          </svg>
        ) : (
          <svg
            className="h-3.5 w-3.5"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.8"
            strokeLinecap="round"
            strokeLinejoin="round"
          >
            <rect x="9" y="9" width="11" height="11" rx="2" />
            <path d="M5 15V5a2 2 0 0 1 2-2h10" />
          </svg>
        )}
      </span>
    </button>
  );
}
