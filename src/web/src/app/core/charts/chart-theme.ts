import { DOCUMENT, Injectable, computed, inject, signal } from '@angular/core';
import type { ChartOptions, ChartType } from 'chart.js';
import { ThemeService } from '../theme.service';

/** Chart colours resolved from the Material theme for the active scheme, as `rgb(...)` strings canvas can paint. */
export interface ChartPalette {
  primary: string;
  secondary: string;
  tertiary: string;
  error: string;
  outline: string;
  grid: string;
  text: string;
  tooltipBackground: string;
  tooltipText: string;
  fontFamily: string;
}

const REDUCED_MOTION_QUERY = '(prefers-reduced-motion: reduce)';

/**
 * Resolves `--mat-sys-<token>` to the colour the browser computes for the active scheme.
 * The theme emits `light-dark()` values, which canvas can't parse, so each variable is applied
 * to its own hidden probe element and the computed `color`s are read back in one pass
 * (one style recalculation rather than one per token).
 */
export function resolveColours(doc: Document, tokens: readonly string[]): string[] {
  const host = doc.createElement('div');
  host.style.display = 'none';
  const probes = tokens.map((token) => {
    const probe = doc.createElement('span');
    probe.style.color = `var(--mat-sys-${token})`;
    return host.appendChild(probe);
  });
  doc.body.appendChild(host);
  const view = doc.defaultView!;
  try {
    return probes.map((probe) => view.getComputedStyle(probe).color);
  } finally {
    host.remove();
  }
}

export function chartPalette(doc: Document): ChartPalette {
  const [primary, secondary, tertiary, error, outline, grid, text, tooltipBackground, tooltipText] =
    resolveColours(doc, [
      'primary',
      'secondary',
      'tertiary',
      'error',
      'outline',
      'outline-variant',
      'on-surface-variant',
      'inverse-surface',
      'inverse-on-surface',
    ]);
  return {
    primary,
    secondary,
    tertiary,
    error,
    outline,
    grid,
    text,
    tooltipBackground,
    tooltipText,
    fontFamily: doc.defaultView!.getComputedStyle(doc.body).fontFamily,
  };
}

/** Themed grid, border and ticks for one cartesian axis; spread into a typed scale. */
export function chartAxis(p: ChartPalette) {
  return {
    grid: { color: p.grid },
    border: { color: p.grid },
    ticks: { color: p.text, font: { family: p.fontFamily } },
  };
}

/** Base options for cartesian charts (bar, line): themed grid, ticks, legend and tooltip. */
export function cartesianOptions<T extends ChartType = 'bar'>(
  p: ChartPalette,
  reducedMotion: boolean,
): ChartOptions<T> {
  const font = { family: p.fontFamily };
  const axis = chartAxis(p);
  const options: ChartOptions = {
    responsive: true,
    maintainAspectRatio: false,
    animation: reducedMotion ? false : undefined,
    color: p.text,
    plugins: {
      legend: { labels: { color: p.text, font } },
      tooltip: {
        backgroundColor: p.tooltipBackground,
        titleColor: p.tooltipText,
        bodyColor: p.tooltipText,
        titleFont: font,
        bodyFont: font,
      },
    },
    scales: { x: axis, y: axis },
  };
  // Scale and plugin types differ only per chart type, not in the themed fields set here.
  return options as ChartOptions<T>;
}

/** The chart palette and motion preference, recomputed when the effective theme changes. */
@Injectable({ providedIn: 'root' })
export class ChartTheme {
  private readonly document = inject(DOCUMENT);
  private readonly theme = inject(ThemeService);
  private readonly motionQuery =
    this.document.defaultView?.matchMedia?.(REDUCED_MOTION_QUERY) ?? null;

  readonly reducedMotion = signal(this.motionQuery?.matches ?? false);
  readonly palette = computed(() => {
    // ThemeService toggles the scheme class on <html> before this is read, so the probe sees the new scheme.
    this.theme.effective();
    return chartPalette(this.document);
  });
  readonly cartesian = computed(() => cartesianOptions(this.palette(), this.reducedMotion()));

  constructor() {
    this.motionQuery?.addEventListener?.('change', (e) => this.reducedMotion.set(e.matches));
  }
}
