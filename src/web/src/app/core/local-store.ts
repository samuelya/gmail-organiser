/** Reads a value from localStorage; returns null when storage is unavailable or blocked. */
export function readLocal(key: string): string | null {
  try {
    return globalThis.localStorage?.getItem(key) ?? null;
  } catch {
    return null;
  }
}

/** Writes a value to localStorage; silently ignores unavailable or full storage. */
export function writeLocal(key: string, value: string): void {
  try {
    globalThis.localStorage?.setItem(key, value);
  } catch {
    // Storage blocked (private mode, quota): the choice just isn't remembered.
  }
}
