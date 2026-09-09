import path from "node:path";
import { test, type Page } from "@playwright/test";

const shots = path.join(__dirname, "screenshots");

async function capture(page: Page, name: string) {
  await page.waitForLoadState("networkidle");
  await page.locator("h1").first().waitFor();
  await page.screenshot({
    path: path.join(shots, `${name}.png`),
    fullPage: true,
    animations: "disabled",
  });
}

test.describe("docs screenshots", () => {
  test("getting started", async ({ page }) => {
    await page.goto("/docs/getting-started");
    await capture(page, "getting-started");
    const code = page.locator(".emcydocs-codeblock").first();
    await code.scrollIntoViewIfNeeded();
    await code.screenshot({
      path: path.join(shots, "getting-started-code.png"),
      animations: "disabled",
    });
  });

  test("docs home", async ({ page }) => {
    await page.goto("/docs");
    await capture(page, "docs-home");
  });

  test("protect an API", async ({ page }) => {
    await page.goto("/docs/quickstarts/protect-api");
    await capture(page, "protect-api");
  });

  test("6.0 blog post", async ({ page }) => {
    await page.goto("/blog/sqlos-6-0-0-one-call-is-the-only-call");
    await capture(page, "blog-6-0");
    const code = page.locator("[data-rehype-pretty-code-figure]").nth(1);
    await code.scrollIntoViewIfNeeded();
    await code.screenshot({
      path: path.join(shots, "blog-6-0-code.png"),
      animations: "disabled",
    });
  });
});
