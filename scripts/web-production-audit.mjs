import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const HIGH_SEVERITIES = new Set(["high", "critical"]);
const ADVISORY_PATTERN = /GHSA-[0-9a-z-]+/i;
const CVE_PATTERN = /CVE-\d{4}-\d+/i;

export function collectHighCriticalFindings(audit) {
  const findings = [];
  const vulnerabilities = audit?.vulnerabilities ?? {};

  for (const item of Object.values(vulnerabilities)) {
    const via = Array.isArray(item?.via) ? item.via : [];
    for (const entry of via) {
      if (!entry || typeof entry !== "object") {
        continue;
      }

      const severity = String(entry.severity ?? "").toLowerCase();
      if (!HIGH_SEVERITIES.has(severity)) {
        continue;
      }

      const url = String(entry.url ?? "");
      const advisory =
        url.match(ADVISORY_PATTERN)?.[0]?.toUpperCase() ??
        url.match(CVE_PATTERN)?.[0]?.toUpperCase() ??
        url;

      findings.push({
        package: String(entry.name ?? item.name ?? "unknown"),
        severity,
        title: String(entry.title ?? item.name ?? "Untitled advisory"),
        advisory,
        url,
      });
    }
  }

  const seen = new Set();
  return findings.filter((finding) => {
    const key = `${finding.advisory}|${finding.package}`;
    if (seen.has(key)) {
      return false;
    }
    seen.add(key);
    return true;
  });
}

export function normalizeAdvisory(value) {
  return String(value ?? "").trim().toUpperCase();
}

export function loadExceptions(fileContents) {
  const parsed = JSON.parse(fileContents);
  if (!Array.isArray(parsed?.exceptions)) {
    throw new Error("npm-audit-exceptions.json must contain an exceptions array.");
  }

  return parsed.exceptions.map((entry, index) => {
    const advisory = normalizeAdvisory(entry?.advisory);
    const packageName = String(entry?.package ?? "").trim();
    const reason = String(entry?.reason ?? "").trim();
    const expires = String(entry?.expires ?? "").trim();

    if (!advisory || !packageName || !reason || !expires) {
      throw new Error(
        `Exception ${index} must include advisory, package, reason, and expires (YYYY-MM-DD).`,
      );
    }

    if (!/^\d{4}-\d{2}-\d{2}$/.test(expires) || Number.isNaN(Date.parse(`${expires}T00:00:00Z`))) {
      throw new Error(`Exception ${index} has an invalid expires date: ${expires}`);
    }

    return { advisory, package: packageName, reason, expires };
  });
}

export function evaluateProductionAudit({ findings, exceptions, now = new Date() }) {
  const today = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()));
  const errors = [];
  const used = new Set();

  for (const finding of findings) {
    const match = exceptions.find(
      (exception) =>
        exception.advisory === normalizeAdvisory(finding.advisory) &&
        exception.package === finding.package,
    );

    if (!match) {
      errors.push(
        `${finding.severity.toUpperCase()} ${finding.package} ${finding.advisory}: ${finding.title}${finding.url ? ` (${finding.url})` : ""}`,
      );
      continue;
    }

    used.add(`${match.advisory}|${match.package}`);
    const expiresAt = new Date(`${match.expires}T00:00:00Z`);
    if (expiresAt < today) {
      errors.push(
        `Expired exception for ${finding.package} ${finding.advisory} (expired ${match.expires}). Re-review or remove it.`,
      );
    }
  }

  for (const exception of exceptions) {
    const key = `${exception.advisory}|${exception.package}`;
    if (!used.has(key)) {
      errors.push(
        `Unused exception for ${exception.package} ${exception.advisory}. Remove it from web/npm-audit-exceptions.json.`,
      );
    }
  }

  return errors;
}

function isMain() {
  const invoked = process.argv[1] ? path.resolve(process.argv[1]) : "";
  return invoked === fileURLToPath(import.meta.url);
}

function runNpmAudit(webRoot) {
  const result = spawnSync(
    "npm",
    ["audit", "--omit=dev", "--json", "--prefix", webRoot],
    {
      encoding: "utf8",
      maxBuffer: 20 * 1024 * 1024,
    },
  );

  if (result.error) {
    throw result.error;
  }

  const stdout = result.stdout?.trim() || "";
  try {
    return JSON.parse(stdout);
  } catch (error) {
    const stderr = result.stderr?.trim();
    throw new Error(
      `npm audit did not return JSON (exit ${result.status}). ${stderr || stdout || error.message}`,
    );
  }
}

function main() {
  const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const webRoot = path.join(repoRoot, "web");
  const exceptionsPath = path.join(webRoot, "npm-audit-exceptions.json");

  const exceptions = loadExceptions(fs.readFileSync(exceptionsPath, "utf8"));
  const audit = runNpmAudit(webRoot);
  const findings = collectHighCriticalFindings(audit);
  const errors = evaluateProductionAudit({ findings, exceptions });

  if (errors.length > 0) {
    console.error("Production web dependency audit failed:\n");
    for (const error of errors) {
      console.error(`- ${error}`);
    }
    console.error(
      "\nIf a finding is genuinely accepted, add a reviewable exception to web/npm-audit-exceptions.json.",
    );
    process.exit(1);
  }

  console.log(
    findings.length === 0
      ? "Production web dependency audit passed with no high or critical findings."
      : `Production web dependency audit passed with ${findings.length} reviewable exception(s).`,
  );
}

if (isMain()) {
  main();
}
