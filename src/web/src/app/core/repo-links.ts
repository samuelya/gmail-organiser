/** The project's public repository; guides under `docs/` are linked from here. */
export const REPO_URL = 'https://github.com/samuelya/gmail-organiser';

/** The GitHub page of a file in the repository, e.g. `docs/setup/apps-script.md`. */
export function repoDocUrl(path: string): string {
  return `${REPO_URL}/blob/main/${path}`;
}
