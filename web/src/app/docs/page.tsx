import { redirect } from "next/navigation";

export const metadata = {
  title: "Documentation - SqlOS",
  description:
    "SqlOS documentation for the merged AuthServer and Fga runtime, shared example stack, and SQL-backed test setup.",
};

export default function DocsPage() {
  redirect("/docs/guides");
}
