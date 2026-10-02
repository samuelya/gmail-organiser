import { APIRequestContext, expect } from '@playwright/test';

/** The API the dev-server proxies to; the same default as `proxy.conf.json`. */
export const apiUrl = process.env['E2E_API_URL'] ?? 'http://localhost:5181';

/** The anti-CSRF header the app's interceptor adds to every API call. */
const headers = { 'X-Requested-With': 'XMLHttpRequest' };

export interface SetupStatus {
  wizardSeen: boolean;
  complete: boolean;
}

export interface FetchStatus {
  mailboxPhase: string;
  messagesTotal: number | null;
  messagesStored: number;
  activeJob: unknown;
}

export async function getSetupStatus(request: APIRequestContext): Promise<SetupStatus> {
  const response = await request.get('/api/setup/status');
  expect(response.ok(), 'GET /api/setup/status').toBe(true);
  return response.json();
}

/** What the wizard's Finish saves; afterwards the setup guard shows the banner instead of redirecting. */
export async function markWizardSeen(request: APIRequestContext): Promise<void> {
  const response = await request.put('/api/settings', { headers, data: { setupWizardSeen: true } });
  expect(response.ok(), 'PUT /api/settings').toBe(true);
}

export async function getFetchStatus(request: APIRequestContext): Promise<FetchStatus> {
  const response = await request.get('/api/fetch/status');
  expect(response.ok(), 'GET /api/fetch/status').toBe(true);
  return response.json();
}
