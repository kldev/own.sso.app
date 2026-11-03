// End-to-end screenshots of Great SSO (ADR 0018) - and a smoke test: every step waits for what it needs and the run
// fails at the first step that does not work.
//
// Needs Great SSO in Development with the sign-in stub and the demo seed - ./deploy/deploy.sh without provider ids in
// .env, or `dotnet run --project apps/Sso.Api` (appsettings.Development.json) - on an empty database, so the demo
// data is what the screenshots show. The sample application (samples/Sso.SampleClient) is started here with the
// secret the panel shows once.
//
//   cd e2e && npm install && npx playwright install chromium && npm run screenshots
//   SSO_URL=http://localhost:6100 SAMPLE_URL=http://localhost:5200 npm run screenshots
import { spawn } from "node:child_process";
import { mkdir } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium, devices } from "playwright";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const out = join(root, "docs", "screenshots");
const sso = process.env.SSO_URL ?? "http://localhost:6100";
const sample = process.env.SAMPLE_URL ?? "http://localhost:5200";
const desktop = { viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1 };
const phone = { ...devices["iPhone 13"] };

let step = 0;
async function shot(page, name, options = {}) {
  step += 1;
  const file = join(out, `${String(step).padStart(2, "0")}-${name}.png`);
  await page.waitForLoadState("networkidle");
  await page.screenshot({ path: file, fullPage: options.fullPage ?? false });
  console.log(`  ${file.slice(root.length + 1)}`);
}

async function signIn(page, email, provider = "github") {
  await page.goto(`${sso}/sign-in`);
  await page.locator(`a[href^="auth/external/${provider}"]`).click();
  await page.locator('[data-testid="dev-sign-in"]').waitFor();
  await page.fill("#email", email);
  await page.getByRole("button", { name: "Continue as this account" }).click();
}

async function waitForUrl(url, seconds) {
  for (let i = 0; i < seconds * 2; i++) {
    try {
      const response = await fetch(url);
      if (response.status < 500) return;
    } catch {
      // not listening yet
    }
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error(`${url} did not answer within ${seconds} s`);
}

async function main() {
  await mkdir(out, { recursive: true });
  await waitForUrl(`${sso}/health`, 60);
  const browser = await chromium.launch();
  let sampleProcess;
  try {
    // ---- the sign-in page and the panel as the seeded admin ----
    const context = await browser.newContext(desktop);
    const page = await context.newPage();
    await page.goto(`${sso}/sign-in`);
    await page.getByRole("heading", { name: "Sign in" }).waitFor();
    await shot(page, "sign-in");

    await signIn(page, "admin@example.com");
    await page.waitForURL(`${sso}/admin`);
    await page.getByRole("heading", { name: /Welcome/ }).waitFor();
    await shot(page, "dashboard");

    await page.goto(`${sso}/admin/people`);
    await page.getByText("Ada Lovelace").waitFor();
    await shot(page, "people");
    await page.getByRole("row", { name: /Ada Lovelace/ }).getByRole("button", { name: "Edit" }).click();
    await page.getByRole("dialog").waitFor();
    await shot(page, "person-edit");
    await page.getByRole("button", { name: "Cancel" }).click();

    // ---- applications: the sample client gets a new secret, shown once ----
    await page.goto(`${sso}/admin/applications`);
    await page.getByText("Great SSO sample client").waitFor();
    await shot(page, "applications");
    await page
      .getByRole("row", { name: /Great SSO sample client/ })
      .getByRole("button", { name: "Edit" })
      .click();
    await page.getByRole("button", { name: "New secret" }).click();
    const secret = (await page.locator('[data-testid="secret"]').innerText()).trim();
    if (secret.length < 40) throw new Error("No client secret was shown.");
    await shot(page, "application-secret");
    await page.getByRole("button", { name: "I have copied it" }).click();

    // ---- activity ----
    await page.goto(`${sso}/admin/activity`);
    await page.getByRole("button", { name: "Sessions" }).click();
    await page.getByText("Grace Hopper").first().waitFor();
    await shot(page, "activity-sessions");
    await page.getByRole("button", { name: "Log" }).click();
    await page.getByText("no_entry").first().waitFor();
    await shot(page, "activity-log");

    // ---- marketing ----
    await page.goto(`${sso}/admin/marketing/mail`);
    await page.getByText("Your team now signs in with Great SSO").first().waitFor();
    await page.getByRole("radio").first().check();
    await page.locator("textarea").first().waitFor();
    await shot(page, "marketing-template");
    await page.getByRole("button", { name: "Addresses" }).first().click();
    await page.getByText("linus@example.com").waitFor();
    await shot(page, "marketing-mailing");

    // ---- MCP for Claude Code ----
    await page.goto(`${sso}/admin/claude-code`);
    await page.getByRole("button", { name: "New token" }).click();
    await page.getByRole("button", { name: "Make the token" }).click();
    await page.locator('[data-testid="mcp-command"]').waitFor();
    await shot(page, "mcp-token");
    await page.getByRole("button", { name: "I have copied it" }).click();

    // ---- the API reference ----
    await page.goto(`${sso}/scalar/`);
    await page.getByText("Great SSO API").first().waitFor();
    await shot(page, "scalar");

    // ---- the dark theme on a desktop ----
    await page.goto(`${sso}/admin/people`);
    await page.getByRole("button", { name: "Change theme" }).click();
    await page.getByText("Ada Lovelace").waitFor();
    await shot(page, "dark-desktop");
    await page.getByRole("button", { name: "Change theme" }).click();

    // ---- an application signs a person in through Great SSO ----
    sampleProcess = spawn(
      "dotnet",
      ["run", "--project", join(root, "samples", "Sso.SampleClient"), "--urls", sample],
      { env: { ...process.env, GreatSso__ClientSecret: secret, GreatSso__Authority: sso }, stdio: "ignore" }
    );
    await waitForUrl(sample, 120);
    await page.goto(sample);
    await page.getByRole("link", { name: "Sign in with Great SSO" }).click();
    await page.waitForURL(`${sample}/`);
    await page.locator('[data-testid="claims"]').waitFor();
    await shot(page, "sample-client");
    await context.close();

    // ---- a phone: a refused sign-in, then the panel in the dark theme ----
    const mobile = await browser.newContext(phone);
    const phonePage = await mobile.newPage();
    await signIn(phonePage, "stranger@example.com");
    await phonePage.waitForURL(/\/sign-in\/refused/);
    await phonePage.getByText("There is no Great SSO entry").waitFor();
    await shot(phonePage, "refused-mobile");

    await mobile.addInitScript(() => localStorage.setItem("sso.theme", "dark"));
    await signIn(phonePage, "ada@example.com");
    await phonePage.waitForURL(`${sso}/admin`);
    await shot(phonePage, "dark-mobile");
    await mobile.close();
  } finally {
    sampleProcess?.kill();
    await browser.close();
  }
  console.log(`${step} screenshots in docs/screenshots.`);
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
