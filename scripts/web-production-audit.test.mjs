import assert from "node:assert/strict";
import test from "node:test";
import {
  collectHighCriticalFindings,
  evaluateProductionAudit,
  loadExceptions,
} from "./web-production-audit.mjs";

test("collects only high and critical advisories from npm audit JSON", () => {
  const findings = collectHighCriticalFindings({
    vulnerabilities: {
      next: {
        name: "next",
        severity: "critical",
        via: [
          {
            name: "next",
            severity: "critical",
            title: "Server Action DoS",
            url: "https://github.com/advisories/GHSA-m99w-x7hq-7vfj",
          },
          "postcss",
        ],
      },
      mermaid: {
        name: "mermaid",
        severity: "moderate",
        via: [
          {
            name: "mermaid",
            severity: "moderate",
            title: "prototype pollution",
            url: "https://github.com/advisories/GHSA-c4c3-pg64-4m4v",
          },
        ],
      },
    },
  });

  assert.deepEqual(findings, [
    {
      package: "next",
      severity: "critical",
      title: "Server Action DoS",
      advisory: "GHSA-M99W-X7HQ-7VFJ",
      url: "https://github.com/advisories/GHSA-m99w-x7hq-7vfj",
    },
  ]);
});

test("exceptions must name advisory, package, reason, and expiry", () => {
  assert.throws(
    () => loadExceptions(JSON.stringify({ exceptions: [{ advisory: "GHSA-aaaa-bbbb-cccc" }] })),
    /must include advisory, package, reason, and expires/,
  );

  const exceptions = loadExceptions(
    JSON.stringify({
      exceptions: [
        {
          advisory: "ghsa-m99w-x7hq-7vfj",
          package: "next",
          reason: "Temporary pin while a host-side mitigation is verified.",
          expires: "2026-12-31",
        },
      ],
    }),
  );

  assert.equal(exceptions[0].advisory, "GHSA-M99W-X7HQ-7VFJ");
});

test("unexceptioned high findings, expired exceptions, and unused exceptions fail", () => {
  const findings = [
    {
      package: "next",
      severity: "high",
      title: "Server Action DoS",
      advisory: "GHSA-M99W-X7HQ-7VFJ",
      url: "https://github.com/advisories/GHSA-m99w-x7hq-7vfj",
    },
  ];

  assert.match(
    evaluateProductionAudit({ findings, exceptions: [] })[0],
    /GHSA-M99W-X7HQ-7VFJ/,
  );

  assert.match(
    evaluateProductionAudit({
      findings,
      exceptions: [
        {
          advisory: "GHSA-M99W-X7HQ-7VFJ",
          package: "next",
          reason: "Reviewed",
          expires: "2026-01-01",
        },
      ],
      now: new Date("2026-09-18T00:00:00Z"),
    })[0],
    /Expired exception/,
  );

  assert.match(
    evaluateProductionAudit({
      findings: [],
      exceptions: [
        {
          advisory: "GHSA-M99W-X7HQ-7VFJ",
          package: "next",
          reason: "Reviewed",
          expires: "2026-12-31",
        },
      ],
    })[0],
    /Unused exception/,
  );

  assert.deepEqual(
    evaluateProductionAudit({
      findings,
      exceptions: [
        {
          advisory: "GHSA-M99W-X7HQ-7VFJ",
          package: "next",
          reason: "Reviewed",
          expires: "2026-12-31",
        },
      ],
      now: new Date("2026-09-18T00:00:00Z"),
    }),
    [],
  );
});
