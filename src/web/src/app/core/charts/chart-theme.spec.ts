import { TestBed } from '@angular/core/testing';
import { THEME_STORAGE_KEY, ThemeService } from '../theme.service';
import { cartesianOptions, ChartPalette, ChartTheme, resolveColours } from './chart-theme';

const TOKENS = [
  'primary',
  'secondary',
  'tertiary',
  'error',
  'outline',
  'outline-variant',
  'on-surface-variant',
  'inverse-surface',
  'inverse-on-surface',
  'tertiary-container',
  'surface',
];

/** Gives every token a distinct synthetic rgb() per scheme, keyed off the class ThemeService sets on <html>. */
function stubComputedColours(): void {
  const real = window.getComputedStyle.bind(window);
  vi.spyOn(window, 'getComputedStyle').mockImplementation((el, pseudo) => {
    const style = real(el, pseudo);
    const match = /var\(--mat-sys-([a-z-]+)\)/.exec((el as HTMLElement).style.color ?? '');
    if (!match) return style;
    const dark = document.documentElement.classList.contains('dark');
    const i = TOKENS.indexOf(match[1]);
    const colour = dark ? `rgb(${200 + i}, 200, 200)` : `rgb(${i}, 0, 0)`;
    return { ...style, color: colour } as CSSStyleDeclaration;
  });
}

describe('chart theme', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    document.documentElement.classList.remove('light', 'dark');
    localStorage.removeItem(THEME_STORAGE_KEY);
  });

  // jsdom doesn't resolve var(); the stub stands in for the browser's computed colour.
  it('resolves a theme variable to an rgb() string through a probe and removes the probe', () => {
    stubComputedColours();
    const before = document.body.childElementCount;

    expect(resolveColours(document, ['primary', 'outline'])).toEqual([
      'rgb(0, 0, 0)',
      'rgb(4, 0, 0)',
    ]);
    expect(document.body.childElementCount).toBe(before);
  });

  it('rebuilds the palette and options when the effective theme changes', () => {
    stubComputedColours();
    const theme = TestBed.inject(ThemeService);
    const charts = TestBed.inject(ChartTheme);

    theme.set('light');
    const light = charts.palette();
    expect(light.primary).toBe('rgb(0, 0, 0)');
    expect(light.tertiary).toBe('rgb(2, 0, 0)');
    expect(charts.cartesian().plugins?.tooltip?.backgroundColor).toBe('rgb(7, 0, 0)');

    theme.set('dark');
    const dark = charts.palette();
    expect(dark.primary).toBe('rgb(200, 200, 200)');
    expect(dark.grid).toBe('rgb(205, 200, 200)');
    expect(dark.text).toBe('rgb(206, 200, 200)');
    expect(charts.cartesian().plugins?.tooltip?.bodyColor).toBe('rgb(208, 200, 200)');
  });

  it('applies the palette to axes, legend and tooltip and turns animation off for reduced motion', () => {
    const p: ChartPalette = {
      primary: 'p',
      secondary: 's',
      tertiary: 't',
      error: 'e',
      outline: 'o',
      grid: 'grid',
      text: 'text',
      tooltipBackground: 'tip-bg',
      tooltipText: 'tip-fg',
      tertiaryContainer: 'tc',
      surface: 'surface',
      fontFamily: 'Synthetic Sans',
    };

    const moving = cartesianOptions(p, false);
    expect(moving.animation).toBeUndefined();
    expect(moving.scales?.['x']?.grid?.color).toBe('grid');
    expect(moving.scales?.['y']?.ticks?.color).toBe('text');
    expect(moving.plugins?.legend?.labels?.color).toBe('text');
    expect(moving.plugins?.tooltip?.titleColor).toBe('tip-fg');
    expect(moving.plugins?.legend?.labels?.font).toEqual({ family: 'Synthetic Sans' });

    expect(cartesianOptions(p, true).animation).toBe(false);
  });
});
