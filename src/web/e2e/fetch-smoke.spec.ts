import { expect, test } from '@playwright/test';
import { getFetchStatus, getSetupStatus, markWizardSeen } from './api';

/** Formats a count as the dashboard's `number` pipe does (default `en-US` locale). */
const count = (value: number) => new Intl.NumberFormat('en-US').format(value);

test('setup banner, mailbox fetch to Completed, senders', async ({ page, request }) => {
  // Ollama is absent with the fake stack, so setup stays incomplete: once the wizard is marked
  // seen, the guard lets the dashboard through and the layout shows the banner instead.
  let setup = await getSetupStatus(request);
  if (!setup.wizardSeen) {
    await markWizardSeen(request);
    setup = await getSetupStatus(request);
  }
  expect(setup.wizardSeen).toBe(true);

  await page.goto('/dashboard');
  await expect(page).toHaveURL(/\/dashboard$/);
  if (!setup.complete) {
    await expect(page.getByRole('status').filter({ hasText: 'Setup incomplete' })).toBeVisible();
  }

  const card = page.getByRole('region', { name: 'Mailbox fetch' });
  const phase = card.getByTestId('phase');
  await expect(phase).toHaveText(/^(Not started|Completed)$/);

  // A first run starts the mailbox fetch; later runs on the same database fetch new mail.
  const start = card.getByRole('button', { name: /^(Start fetch|Fetch new mail)$/ });
  await expect(start).toBeEnabled();
  await start.click();
  // The fake fetch can finish before a progress bar renders, so only the final state is asserted.
  await expect(phase).toHaveText('Completed', { timeout: 60_000 });
  // Start comes back once no fetch job is active.
  await expect(card.getByRole('button', { name: 'Fetch new mail' })).toBeEnabled({
    timeout: 60_000,
  });

  const fetch = await getFetchStatus(request);
  // Compare with the stored count (messagesTotal is the raw Gmail total incl. spam/trash).
  expect(fetch.messagesStored, 'messagesStored after the fetch').toBeGreaterThan(0);
  await expect(card.getByTestId('messages-stored')).toHaveText(count(fetch.messagesStored));

  await page.goto('/senders');
  await expect(page.getByRole('cell', { name: /example\.com/ }).first()).toBeVisible();
});
