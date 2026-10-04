import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { expect, Page, test } from '@playwright/test';
import {
  approveAndApplyAll,
  fetchMailbox,
  getSetupStatus,
  markWizardSeen,
  runAnalysis,
  selectModels,
  syncFilters,
} from './api';

// `npm run screenshots` only (the `screenshots` project); needs the fake stack, see docs/ci.md.
// The README images come from the fake mailbox, so every picture shows synthetic example.com data.
const imagesDir = join(__dirname, '..', '..', '..', 'docs', 'images');

/** Waits for the page to settle, then writes `docs/images/<name>.png` at the viewport size. */
async function shoot(page: Page, name: string): Promise<void> {
  await page.waitForLoadState('networkidle');
  await expect(page.getByRole('main')).toBeVisible();
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
  // Progress bars of a load that just finished fade out; never picture them.
  await expect(page.locator('mat-progress-bar[mode="indeterminate"]')).toHaveCount(0);
  await page.screenshot({ path: join(imagesDir, `${name}.png`), fullPage: false });
}

test('README screenshots from the fake stack', async ({ page, request }) => {
  test.setTimeout(300_000);
  mkdirSync(imagesDir, { recursive: true });

  if (!(await getSetupStatus(request)).wizardSeen) await markWizardSeen(request);
  await selectModels(request);
  await fetchMailbox(request);

  // fetch → analysis → review → apply. A reused database may have nothing left to analyse;
  // the run still completes and the review step below finds no pending group.
  expect(await runAnalysis(request, 'inbox', 20)).toBe('completed');

  await page.goto('/review');
  const sender = page.getByTestId('sender-item').first();
  await expect(sender.or(page.getByTestId('senders-empty'))).toBeVisible();
  if (await sender.isVisible()) {
    await sender.click();
    const approve = page.getByTestId('group-approve').first();
    await expect(approve).toBeEnabled();
    await shoot(page, 'review');
    await approve.click();
    const apply = page.getByTestId('apply-approved');
    await expect(apply).toHaveText(/\([1-9]\d*\)/);
    await apply.click();
    // The fake apply can finish before its progress card renders; the count drops once it has.
    await expect(apply).toHaveText(/\(0\)/, { timeout: 60_000 });
  } else {
    await shoot(page, 'review');
  }

  // A second run over all mail: approving it marks the fake promotions with the delete label,
  // which gives the Clean-up page its content.
  expect(await runAnalysis(request, 'all', 20)).toBe('completed');
  await approveAndApplyAll(request);
  await syncFilters(request);

  for (const name of [
    'dashboard',
    'senders',
    'analyse',
    'clean-up',
    'rules',
    'history',
    'settings',
  ]) {
    await page.goto(`/${name}`);
    await shoot(page, name);
  }
});
