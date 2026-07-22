import fs from "node:fs/promises";
import path from "node:path";

const owner = process.env.ROADMAP_OWNER ?? "ross-slaney";
const projectNumber = Number(process.env.ROADMAP_PROJECT_NUMBER ?? "1");
const viewNumber = Number(process.env.ROADMAP_VIEW_NUMBER ?? "5");
const projectUrl = `https://github.com/users/${owner}/projects/${projectNumber}/views/${viewNumber}`;
const outputDirectory = process.env.ROADMAP_OUTPUT_DIR ?? "_site";
const maximumItems = Number(process.env.ROADMAP_MAX_ITEMS ?? "20");
const githubApiVersion = "2026-03-10";

function escapeXml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&apos;");
}

function truncate(value, maximumLength) {
  const characters = [...String(value)];
  return characters.length <= maximumLength
    ? value
    : `${characters.slice(0, maximumLength - 1).join("")}…`;
}

function rawFieldValue(value) {
  if (value === null || value === undefined) return null;
  if (typeof value !== "object") return value;
  if (value.raw !== undefined) return value.raw;
  if (value.name?.raw !== undefined) return value.name.raw;
  if (value.title?.raw !== undefined) return value.title.raw;
  return null;
}

function normalizeItem(item) {
  const fields = Object.fromEntries(
    (item.fields ?? []).map((field) => [field.name, rawFieldValue(field.value)]),
  );
  const content = item.content ?? {};
  const titleField = item.fields?.find((field) => field.name === "Title")?.value;

  return {
    title: content.title ?? titleField?.raw ?? "Untitled roadmap item",
    url: content.html_url ?? titleField?.url ?? projectUrl,
    number: content.number ?? titleField?.number,
    track: fields.Track ?? "Unassigned",
    businessValue: fields["Business Value"] ?? "—",
    jobSize: fields["Job Size"] ?? "—",
  };
}

function nextPageUrl(response) {
  const nextLink = response.headers
    .get("link")
    ?.split(",")
    .map((link) => link.trim())
    .find((link) => link.endsWith('rel="next"'))
    ?.match(/^<([^>]+)>/)?.[1];
  return nextLink ? new URL(nextLink) : null;
}

async function requestJson(token, url) {
  const response = await fetch(url, {
    headers: {
      Accept: "application/vnd.github+json",
      Authorization: `Bearer ${token}`,
      "User-Agent": "sqlos-roadmap-renderer",
      "X-GitHub-Api-Version": githubApiVersion,
    },
  });
  const result = await response.json();

  if (!response.ok) {
    throw new Error(
      `GitHub Project request failed (${response.status}): ${result.message ?? response.statusText}`,
    );
  }

  return { result, next: nextPageUrl(response) };
}

async function listAll(token, initialUrl) {
  const values = [];
  let url = new URL(initialUrl);

  while (url) {
    url.searchParams.set("per_page", "100");
    const { result, next } = await requestJson(token, url);
    if (!Array.isArray(result)) {
      throw new Error("GitHub Project list request returned an unexpected response.");
    }
    values.push(...result);
    url = next;
  }

  return values;
}

function compareFieldValues(leftValue, rightValue, field) {
  if (field.data_type === "single_select") {
    const optionRank = new Map(
      (field.options ?? []).map((option, index) => [String(option.id), index]),
    );
    return (
      (optionRank.get(String(leftValue.id)) ?? Number.MAX_SAFE_INTEGER) -
      (optionRank.get(String(rightValue.id)) ?? Number.MAX_SAFE_INTEGER)
    );
  }

  if (field.data_type === "number") {
    return Number(leftValue) - Number(rightValue);
  }

  return String(rawFieldValue(leftValue) ?? leftValue).localeCompare(
    String(rawFieldValue(rightValue) ?? rightValue),
    "en",
    { numeric: true, sensitivity: "base" },
  );
}

function applySavedViewOrder(items, view, projectFields) {
  const fieldsById = new Map(
    projectFields.map((field) => [String(field.id), field]),
  );
  const sortRules = view.sort_by ?? [];

  return items
    .map((item, originalIndex) => ({ item, originalIndex }))
    .sort((left, right) => {
      for (const [fieldId, direction] of sortRules) {
        const field = fieldsById.get(String(fieldId));
        if (!field) {
          throw new Error(`Saved Value Table sorts by unknown field ${fieldId}.`);
        }

        const leftValue = left.item.fields?.find(
          (value) => String(value.id) === String(fieldId),
        )?.value;
        const rightValue = right.item.fields?.find(
          (value) => String(value.id) === String(fieldId),
        )?.value;
        const leftMissing = leftValue === null || leftValue === undefined;
        const rightMissing = rightValue === null || rightValue === undefined;
        if (leftMissing || rightMissing) {
          if (leftMissing !== rightMissing) return leftMissing ? 1 : -1;
          continue;
        }
        const comparison = compareFieldValues(leftValue, rightValue, field);
        if (comparison !== 0) {
          return direction.toLowerCase() === "desc" ? -comparison : comparison;
        }
      }

      // Preserve the view endpoint's order for ties and manually ordered items.
      return left.originalIndex - right.originalIndex;
    })
    .map(({ item }) => item);
}

function buildProject(view, fields, items) {
  return {
    title: "SqlOS Roadmap",
    viewName: view.name ?? "Value Table",
    nodes: applySavedViewOrder(items, view, fields),
  };
}

async function loadProject() {
  if (process.env.ROADMAP_FIXTURE_JSON) {
    const fixture = JSON.parse(process.env.ROADMAP_FIXTURE_JSON);
    return buildProject(fixture.view, fixture.fields, fixture.items);
  }

  const token = process.env.PROJECTS_TOKEN;
  if (!token) {
    throw new Error(
      "PROJECTS_TOKEN is required. Use a classic PAT with the read:project scope for this user-owned Project.",
    );
  }

  const projectApiUrl = `https://api.github.com/users/${owner}/projectsV2/${projectNumber}`;
  const [{ result: view }, fields] = await Promise.all([
    requestJson(token, `${projectApiUrl}/views/${viewNumber}`),
    listAll(token, `${projectApiUrl}/fields`),
  ]);
  const fieldIds = new Set(
    fields
      .filter((field) =>
        ["Title", "Track", "Business Value", "Job Size"].includes(field.name),
      )
      .map((field) => String(field.id)),
  );
  for (const [fieldId] of view.sort_by ?? []) fieldIds.add(String(fieldId));

  const itemsUrl = new URL(`${projectApiUrl}/views/${viewNumber}/items`);
  itemsUrl.searchParams.set("fields", [...fieldIds].join(","));
  const items = await listAll(token, itemsUrl);
  return buildProject(view, fields, items);
}

const trackColors = new Map([
  ["Authentication & Accounts", ["#dafbe1", "#116329"]],
  ["OAuth & Token Platform", ["#ddf4ff", "#0969da"]],
  ["Enterprise Identity & Provisioning", ["#fbefff", "#8250df"]],
  ["Authorization & Access", ["#fff1e5", "#bc4c00"]],
  ["Admin, Audit & Operations", ["#eaeef2", "#57606a"]],
  ["Integrations & Delivery", ["#ffeff7", "#bf3989"]],
  ["Developer Experience & Ecosystem", ["#fff8c5", "#7d4e00"]],
]);

function renderPill({ x, y, width, text, background, foreground }) {
  return `
    <rect x="${x}" y="${y}" width="${width}" height="24" rx="12" fill="${background}"/>
    <text x="${x + 10}" y="${y + 16}" class="pill" fill="${foreground}">${escapeXml(text)}</text>`;
}

function renderSvg(project) {
  const viewItems = project.nodes.map(normalizeItem);
  const items = viewItems.slice(0, maximumItems);
  const width = 1200;
  const rowHeight = 46;
  const headerHeight = 142;
  const footerHeight = 54;
  const height = headerHeight + items.length * rowHeight + footerHeight;
  const updated = new Intl.DateTimeFormat("en-US", {
    dateStyle: "medium",
    timeZone: "UTC",
  }).format(new Date());

  const rows = items
    .map((item, index) => {
      const y = headerHeight + index * rowHeight;
      const issueSuffix = item.number ? ` #${item.number}` : "";
      const [trackBackground, trackForeground] = trackColors.get(item.track) ?? [
        "#eaeef2",
        "#57606a",
      ];

      return `
        <g>
          <rect x="1" y="${y}" width="1198" height="${rowHeight}" fill="${index % 2 === 0 ? "#ffffff" : "#f6f8fa"}"/>
          <line x1="0" y1="${y + rowHeight}" x2="1200" y2="${y + rowHeight}" stroke="#d8dee4"/>
          <a href="${escapeXml(item.url)}" target="_blank">
            <text x="24" y="${y + 29}" class="title">${escapeXml(truncate(item.title, 67))}<tspan dx="4" class="number">${escapeXml(issueSuffix.trim())}</tspan></text>
          </a>
          ${renderPill({ x: 680, y: y + 11, width: 292, text: truncate(item.track, 35), background: trackBackground, foreground: trackForeground })}
          ${renderPill({ x: 994, y: y + 11, width: 78, text: item.businessValue, background: "#ffebe9", foreground: "#cf222e" })}
          ${renderPill({ x: 1090, y: y + 11, width: 86, text: item.jobSize, background: "#dafbe1", foreground: "#116329" })}
        </g>`;
    })
    .join("");

  const countDescription =
    viewItems.length > items.length
      ? `Showing the first ${items.length} of ${viewItems.length} items in saved Value Table order`
      : `${viewItems.length} items in saved Value Table order`;

  return `<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" role="img" aria-labelledby="title description">
  <title id="title">${escapeXml(project.title)} Value Table</title>
  <desc id="description">${escapeXml(countDescription)}.</desc>
  <style>
    text { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; }
    .heading { font-size: 26px; font-weight: 650; fill: #1f2328; }
    .subheading { font-size: 14px; fill: #656d76; }
    .column { font-size: 12px; font-weight: 600; fill: #656d76; text-transform: uppercase; letter-spacing: .04em; }
    .title { font-size: 14px; font-weight: 500; fill: #0969da; }
    .number { fill: #656d76; }
    .pill { font-size: 12px; font-weight: 600; }
    .footer { font-size: 13px; fill: #656d76; }
    .link { font-size: 13px; font-weight: 600; fill: #0969da; }
  </style>
  <rect x="0.5" y="0.5" width="1199" height="${height - 1}" rx="10" fill="#ffffff" stroke="#d0d7de"/>
  <text x="24" y="40" class="heading">SqlOS Value Table</text>
  <text x="24" y="66" class="subheading">The saved Value Table&apos;s filters and sort rules · Updated ${escapeXml(updated)}</text>
  <line x1="0" y1="88" x2="1200" y2="88" stroke="#d8dee4"/>
  <text x="24" y="120" class="column">Initiative</text>
  <text x="680" y="120" class="column">Track</text>
  <text x="994" y="120" class="column">Value</text>
  <text x="1090" y="120" class="column">Size</text>
  <line x1="0" y1="${headerHeight}" x2="1200" y2="${headerHeight}" stroke="#d8dee4"/>
  ${rows}
  <text x="24" y="${height - 21}" class="footer">${escapeXml(countDescription)}</text>
  <a href="${escapeXml(projectUrl)}" target="_blank">
    <text x="1176" y="${height - 21}" text-anchor="end" class="link">Open the interactive roadmap →</text>
  </a>
</svg>`;
}

function renderIndex(projectTitle) {
  return `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <meta http-equiv="refresh" content="0; url=${escapeXml(projectUrl)}">
    <title>${escapeXml(projectTitle)} Value Table</title>
  </head>
  <body>
    <p><a href="${escapeXml(projectUrl)}">Open the interactive Value Table</a></p>
  </body>
</html>`;
}

const project = await loadProject();
await fs.mkdir(outputDirectory, { recursive: true });
await Promise.all([
  fs.writeFile(path.join(outputDirectory, "roadmap.svg"), renderSvg(project)),
  fs.writeFile(path.join(outputDirectory, "index.html"), renderIndex(project.title)),
]);

console.log(
  `Rendered ${project.nodes.length} project items to ${path.join(outputDirectory, "roadmap.svg")}`,
);
