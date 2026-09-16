import { expect, test } from "@playwright/test";

test.describe("docs reading layout", () => {
  test("docs home spreads across the main column", async ({ page }) => {
    await page.goto("/docs");
    await page.locator("h1").first().waitFor();

    const hero = page.locator(".emcydocs-home-hero");
    const box = await hero.boundingBox();
    expect(box).not.toBeNull();
    expect(box!.width).toBeGreaterThan(900);
    expect(box!.x + box!.width).toBeGreaterThan(1250);
  });

  test("getting started keeps the TOC beside the article", async ({ page }) => {
    await page.goto("/docs/getting-started");
    await page.locator("h1").first().waitFor();

    const article = page.locator(".emcydocs-article");
    const toc = page.locator(".emcydocs-page-aside");
    const articleBox = await article.boundingBox();
    const tocBox = await toc.boundingBox();

    expect(articleBox).not.toBeNull();
    expect(tocBox).not.toBeNull();
    expect(articleBox!.width).toBeGreaterThan(780);

    const gap = tocBox!.x - (articleBox!.x + articleBox!.width);
    expect(gap).toBeGreaterThanOrEqual(24);
    expect(gap).toBeLessThanOrEqual(56);
  });

  test("docs header and sidebar share a professional left gutter", async ({
    page,
  }) => {
    await page.goto("/docs");
    await page.locator("h1").first().waitFor();

    const headerPadding = await page
      .locator("header > div")
      .evaluate((el) => getComputedStyle(el).paddingLeft);
    const sidebarPadding = await page
      .locator(".emcydocs-desktop-nav .emcydocs-sidebar")
      .evaluate((el) => getComputedStyle(el).paddingLeft);

    expect(headerPadding).toBe("32px");
    expect(sidebarPadding).toBe("32px");

    const heroRadius = await page
      .locator(".emcydocs-home-hero")
      .evaluate((el) => getComputedStyle(el).borderTopLeftRadius);
    expect(heroRadius).toBe("4px");
  });
});

test.describe("docs mobile chrome", () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test("hides on-this-page and pins Docs on the right", async ({ page }) => {
    await page.goto("/docs/reference/api-reference");
    await page.locator("h1").first().waitFor();

    await expect(page.locator(".emcydocs-mobile-toc")).toBeHidden();

    const search = page.locator(".emcydocs-search-trigger-compact");
    const toggle = page.locator(".emcydocs-embedded-nav-toggle");
    await expect(search).toBeVisible();
    await expect(toggle).toBeVisible();

    const searchBox = await search.boundingBox();
    const toggleBox = await toggle.boundingBox();
    expect(searchBox).not.toBeNull();
    expect(toggleBox).not.toBeNull();
    expect(searchBox!.x).toBeGreaterThanOrEqual(24);
    expect(toggleBox!.x).toBeGreaterThan(searchBox!.x);
    expect(toggleBox!.x + toggleBox!.width).toBeGreaterThan(300);

    const barPadding = await page
      .locator(".emcydocs-embedded-mobile-bar")
      .evaluate((el) => getComputedStyle(el).paddingLeft);
    expect(barPadding).toBe("24px");

    const searchRadius = await search.evaluate(
      (el) => getComputedStyle(el).borderTopLeftRadius,
    );
    const toggleRadius = await toggle.evaluate(
      (el) => getComputedStyle(el).borderTopLeftRadius,
    );
    expect(searchRadius).toBe("4px");
    expect(toggleRadius).toBe("4px");

    await expect(toggle).toHaveAttribute("aria-label", /docs navigation/i);
    const docsLabel = await toggle.locator("span").evaluate((el) =>
      getComputedStyle(el, "::after").content.replaceAll('"', ""),
    );
    expect(docsLabel).toBe("Docs");

    await toggle.click();
    const panelSearch = page.locator(
      ".emcydocs-embedded-nav-panel .emcydocs-search-input",
    );
    const section = page
      .locator(".emcydocs-embedded-nav-panel .emcydocs-sidebar-section-toggle")
      .first();
    await expect(panelSearch).toBeVisible();
    await expect(section).toBeVisible();

    const panelSearchBox = await panelSearch.boundingBox();
    const sectionBox = await section.boundingBox();
    expect(panelSearchBox).not.toBeNull();
    expect(sectionBox).not.toBeNull();
    expect(panelSearchBox!.x).toBeGreaterThanOrEqual(24);
    expect(panelSearchBox!.x).toBeLessThanOrEqual(26);
    expect(sectionBox!.x).toBeGreaterThanOrEqual(24);
    expect(sectionBox!.x).toBeLessThanOrEqual(26);
  });

  test("docks search under the docs bar", async ({ page }) => {
    await page.goto("/docs/reference/api-reference");
    await page.locator("h1").first().waitFor();
    await page.locator(".emcydocs-search-trigger-compact").click();

    const dialog = page.locator(".emcydocs-search-dialog");
    await expect(dialog).toBeVisible();

    const barBox = await page.locator(".emcydocs-embedded-mobile-bar").boundingBox();
    const dialogBox = await dialog.boundingBox();
    expect(barBox).not.toBeNull();
    expect(dialogBox).not.toBeNull();
    expect(dialogBox!.y).toBeGreaterThanOrEqual(barBox!.y);
    expect(dialogBox!.y).toBeLessThanOrEqual(barBox!.y + barBox!.height + 2);
    expect(dialogBox!.x).toBeLessThanOrEqual(1);
    expect(dialogBox!.width).toBeGreaterThan(350);
  });
});
