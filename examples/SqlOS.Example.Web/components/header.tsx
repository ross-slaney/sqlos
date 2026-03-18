"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { UserSwitcher } from "@/components/user-switcher";
import { LogoutButton } from "@/components/logout-button";

export function Header() {
  const pathname = usePathname();

  const links: { href: "/retail" | "/retail/chains" | "/retail/stores"; label: string }[] = [
    { href: "/retail", label: "Dashboard" },
    { href: "/retail/chains", label: "Chains" },
    { href: "/retail/stores", label: "Stores" },
  ];

  return (
    <header className="site-header">
      <div className="header-inner">
        <nav className="header-nav">
          <Link href="/retail" className="header-brand">
            Northwind Retail
          </Link>
          {links.map((link) => (
            <Link
              key={link.href}
              href={link.href}
              className={`header-link${pathname === link.href ? " active" : ""}`}
            >
              {link.label}
            </Link>
          ))}
        </nav>
        <div className="header-actions">
          <UserSwitcher />
          <LogoutButton />
        </div>
      </div>
    </header>
  );
}
