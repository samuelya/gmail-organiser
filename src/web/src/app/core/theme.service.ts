import { DOCUMENT, Injectable, computed, inject, signal } from '@angular/core';
import { readLocal, writeLocal } from './local-store';

export type ThemeChoice = 'light' | 'dark' | 'system';
export type EffectiveTheme = 'light' | 'dark';

export const THEME_STORAGE_KEY = 'gmo.theme';
const DARK_QUERY = '(prefers-color-scheme: dark)';
const CYCLE: Record<ThemeChoice, ThemeChoice> = { system: 'light', light: 'dark', dark: 'system' };

/**
 * Light/dark theme. Defaults to the system preference; an explicit choice is persisted.
 * The resolved theme is applied as a `light` or `dark` class on <html>, which drives both
 * Material's `color-scheme` (styles.scss) and Tailwind's `dark:` variant (tailwind.css).
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly document = inject(DOCUMENT);
  private readonly media = this.document.defaultView?.matchMedia?.(DARK_QUERY) ?? null;
  private readonly systemDark = signal(this.media?.matches ?? false);

  readonly choice = signal<ThemeChoice>(readStoredChoice());
  readonly effective = computed<EffectiveTheme>(() => {
    const choice = this.choice();
    if (choice !== 'system') return choice;
    return this.systemDark() ? 'dark' : 'light';
  });

  constructor() {
    this.media?.addEventListener?.('change', (e) => {
      this.systemDark.set(e.matches);
      this.apply();
    });
    this.apply();
  }

  set(choice: ThemeChoice): void {
    this.choice.set(choice);
    writeLocal(THEME_STORAGE_KEY, choice);
    this.apply();
  }

  /** system → light → dark → system */
  cycle(): void {
    this.set(CYCLE[this.choice()]);
  }

  private apply(): void {
    const root = this.document.documentElement;
    const theme = this.effective();
    root.classList.toggle('dark', theme === 'dark');
    root.classList.toggle('light', theme === 'light');
  }
}

function readStoredChoice(): ThemeChoice {
  const stored = readLocal(THEME_STORAGE_KEY);
  return stored === 'light' || stored === 'dark' || stored === 'system' ? stored : 'system';
}
