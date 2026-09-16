"use client";

import { useEffect } from "react";

export default function DocsMobileChrome() {
  useEffect(() => {
    const toggle = document.querySelector<HTMLButtonElement>(
      ".sqlos-docs-shell .emcydocs-embedded-nav-toggle",
    );
    const bar = document.querySelector<HTMLElement>(
      ".sqlos-docs-shell .emcydocs-embedded-mobile-bar",
    );
    if (!toggle || !bar) {
      return;
    }

    const syncLabel = () => {
      const open = toggle.getAttribute("aria-expanded") === "true";
      toggle.setAttribute(
        "aria-label",
        open ? "Close docs navigation" : "Open docs navigation",
      );
    };

    const syncDock = () => {
      const top = `${Math.round(bar.getBoundingClientRect().bottom)}px`;
      document.documentElement.style.setProperty(
        "--sqlos-docs-search-dock-top",
        top,
      );
    };

    syncLabel();
    syncDock();

    const observer = new MutationObserver(syncLabel);
    observer.observe(toggle, {
      attributes: true,
      attributeFilter: ["aria-expanded"],
    });

    const resize = new ResizeObserver(syncDock);
    resize.observe(bar);
    window.addEventListener("resize", syncDock);
    window.addEventListener("scroll", syncDock, { passive: true });

    return () => {
      observer.disconnect();
      resize.disconnect();
      window.removeEventListener("resize", syncDock);
      window.removeEventListener("scroll", syncDock);
      document.documentElement.style.removeProperty(
        "--sqlos-docs-search-dock-top",
      );
    };
  }, []);

  return null;
}
