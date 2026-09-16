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
  });
});
