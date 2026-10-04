import { defineConfig, devices } from '@playwright/test';

// Local only (not part of CI): needs the API running with GMAIL_FAKE=true; see docs/ci.md.
const baseURL = process.env['E2E_BASE_URL'] ?? 'http://localhost:4200';
const port = new URL(baseURL).port || '4200';

export default defineConfig({
  testDir: './e2e',
  globalSetup: './e2e/global-setup.ts',
  fullyParallel: false,
  workers: 1,
  retries: 1,
  reporter: 'list',
  use: {
    baseURL,
    trace: 'on-first-retry',
  },
  projects: [
    {
      name: 'chromium',
      testIgnore: /screenshots\.spec\.ts/,
      use: { ...devices['Desktop Chrome'] },
    },
    // `npm run screenshots`: the README images in docs/images (see docs/ci.md).
    {
      name: 'screenshots',
      testMatch: /screenshots\.spec\.ts/,
      retries: 0,
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1440, height: 900 },
        deviceScaleFactor: 1,
        colorScheme: 'light',
      },
    },
  ],
  webServer: {
    command: `npx ng serve --port ${port} --proxy-config e2e/proxy.conf.mjs`,
    url: baseURL,
    // With E2E_API_URL set, a running dev server would proxy to the default API instead.
    reuseExistingServer: !process.env['E2E_API_URL'],
    timeout: 120_000,
  },
});
