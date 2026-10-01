import { DOCUMENT } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { THEME_STORAGE_KEY, ThemeService } from './theme.service';

function stubSystemDark(matches: boolean) {
  let listener: ((e: { matches: boolean }) => void) | undefined;
  const mql = {
    matches,
    addEventListener: (_: string, fn: (e: { matches: boolean }) => void) => (listener = fn),
  };
  vi.spyOn(window, 'matchMedia').mockReturnValue(mql as unknown as MediaQueryList);
  return (dark: boolean) => listener?.({ matches: dark });
}

describe('ThemeService', () => {
  let root: HTMLElement;

  beforeEach(() => {
    localStorage.clear();
    if (!window.matchMedia) {
      Object.defineProperty(window, 'matchMedia', {
        configurable: true,
        writable: true,
        value: () => null,
      });
    }
    root = TestBed.inject(DOCUMENT).documentElement;
    root.classList.remove('dark', 'light');
  });

  afterEach(() => vi.restoreAllMocks());

  it('follows the system preference by default', () => {
    stubSystemDark(true);
    const theme = TestBed.inject(ThemeService);
    expect(theme.choice()).toBe('system');
    expect(theme.effective()).toBe('dark');
    expect(root.classList.contains('dark')).toBe(true);
  });

  it('reacts to system changes while on system', () => {
    const emit = stubSystemDark(false);
    const theme = TestBed.inject(ThemeService);
    expect(root.classList.contains('light')).toBe(true);
    emit(true);
    expect(theme.effective()).toBe('dark');
    expect(root.classList.contains('dark')).toBe(true);
    expect(root.classList.contains('light')).toBe(false);
  });

  it('uses a persisted choice over the system preference', () => {
    stubSystemDark(true);
    localStorage.setItem(THEME_STORAGE_KEY, 'light');
    const theme = TestBed.inject(ThemeService);
    expect(theme.choice()).toBe('light');
    expect(root.classList.contains('light')).toBe(true);
  });

  it('ignores an invalid stored value', () => {
    stubSystemDark(false);
    localStorage.setItem(THEME_STORAGE_KEY, 'neon');
    expect(TestBed.inject(ThemeService).choice()).toBe('system');
  });

  it('cycles system → light → dark → system and persists each step', () => {
    stubSystemDark(false);
    const theme = TestBed.inject(ThemeService);
    theme.cycle();
    expect(theme.choice()).toBe('light');
    theme.cycle();
    expect(theme.choice()).toBe('dark');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark');
    expect(root.classList.contains('dark')).toBe(true);
    theme.cycle();
    expect(theme.choice()).toBe('system');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('system');
  });
});
