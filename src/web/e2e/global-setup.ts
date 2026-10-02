import { apiUrl } from './api';

/** Fails the run at once, with the precondition, when the API is not up. */
export default async function globalSetup(): Promise<void> {
  const url = new URL('/healthz', apiUrl).toString();
  let reason: string;
  try {
    const response = await fetch(url, { signal: AbortSignal.timeout(5000) });
    if (response.ok) return;
    reason = `HTTP ${response.status}`;
  } catch (error) {
    reason = error instanceof Error ? error.message : String(error);
  }
  throw new Error(
    `The API is not reachable at ${url} (${reason}). Start it with GMAIL_FAKE=true and the dev ` +
      'database first (see docs/ci.md, "End-to-end"), or set E2E_API_URL.',
  );
}
