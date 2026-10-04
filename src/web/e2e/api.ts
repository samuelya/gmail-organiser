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

interface ModelList {
  chatModels: { name: string }[];
  embeddingModels: { name: string }[];
}

interface ModelSettings {
  chatModel: string | null;
  embeddingModel: string | null;
}

/** Picks the first chat and embedding model the API lists, unless Settings already names them. */
export async function selectModels(request: APIRequestContext): Promise<void> {
  const settings = await request.get('/api/settings');
  expect(settings.ok(), 'GET /api/settings').toBe(true);
  const current: ModelSettings = await settings.json();
  if (current.chatModel && current.embeddingModel) return;

  const models = await request.get('/api/llm/models');
  expect(models.ok(), 'GET /api/llm/models').toBe(true);
  const list: ModelList = await models.json();
  expect(list.chatModels.length, 'chat models').toBeGreaterThan(0);
  expect(list.embeddingModels.length, 'embedding models').toBeGreaterThan(0);
  const response = await request.put('/api/settings', {
    headers,
    data: {
      chatModel: current.chatModel ?? list.chatModels[0].name,
      embeddingModel: current.embeddingModel ?? list.embeddingModels[0].name,
    },
  });
  expect(response.ok(), 'PUT /api/settings (models)').toBe(true);
}

/** Starts the mailbox fetch on a fresh database, or a fetch of new mail later, and waits until no fetch job runs. */
export async function fetchMailbox(request: APIRequestContext): Promise<void> {
  const before = await getFetchStatus(request);
  const path =
    before.mailboxPhase === 'completed' ? '/api/fetch/incremental' : '/api/fetch/mailbox/start';
  const response = await request.post(path, { headers, data: {} });
  expect(response.ok(), `POST ${path}`).toBe(true);
  await expect
    .poll(
      async () => {
        const status = await getFetchStatus(request);
        return status.mailboxPhase === 'completed' && !status.activeJob;
      },
      { timeout: 60_000 },
    )
    .toBe(true);
}

/** Starts an analysis run and waits for it to finish; returns its final status. */
export async function runAnalysis(
  request: APIRequestContext,
  scope: string,
  count: number,
): Promise<string> {
  const response = await request.post('/api/analysis/runs', { headers, data: { scope, count } });
  expect(response.ok(), 'POST /api/analysis/runs').toBe(true);
  const { id } = (await response.json()) as { id: string };
  let status = '';
  await expect
    .poll(
      async () => {
        const run = await request.get(`/api/analysis/runs/${id}`);
        status = ((await run.json()) as { status: string }).status;
        return ['completed', 'failed', 'cancelled'].includes(status);
      },
      { timeout: 120_000 },
    )
    .toBe(true);
  return status;
}

/** Approves every pending suggestion at the lowest threshold the API accepts (protected deletions stay pending) and applies all approved ones. */
export async function approveAndApplyAll(request: APIRequestContext): Promise<void> {
  const approve = await request.post('/api/review/bulk-approve', {
    headers,
    data: { threshold: 0.5, includeDerived: true },
  });
  expect(approve.ok(), 'POST /api/review/bulk-approve').toBe(true);
  if (((await approve.json()) as { approved: number }).approved === 0) return;

  const apply = await request.post('/api/review/apply', { headers, data: {} });
  expect(apply.ok(), 'POST /api/review/apply').toBe(true);
  const { jobId } = (await apply.json()) as { jobId: string | null };
  if (jobId) expect(await waitForJob(request, jobId), 'apply job status').toBe('completed');
}

/** Reads the Gmail filters into the local snapshot, as the Rules page's Sync does. */
export async function syncFilters(request: APIRequestContext): Promise<void> {
  const response = await request.post('/api/rules/filters/sync', { headers, data: {} });
  expect(response.ok(), 'POST /api/rules/filters/sync').toBe(true);
}

/** Waits until the job reaches a terminal status and returns it. */
async function waitForJob(request: APIRequestContext, id: string): Promise<string> {
  let status = '';
  await expect
    .poll(
      async () => {
        const job = await request.get(`/api/jobs/${id}`);
        status = ((await job.json()) as { status: string }).status;
        return ['completed', 'failed', 'cancelled'].includes(status);
      },
      { timeout: 120_000 },
    )
    .toBe(true);
  return status;
}
