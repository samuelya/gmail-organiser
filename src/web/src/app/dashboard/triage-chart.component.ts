import { DatePipe, DecimalPipe } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  ElementRef,
  inject,
  input,
  signal,
} from '@angular/core';
import { linePath, nearestIndex, niceMax, PlotBox, TriagePoint, xAt, yAt } from './triage.models';

/** Chart height in pixels; the width follows the container. */
export const CHART_HEIGHT = 220;
/** Room for the unread axis (left), the % axis (right) and the day labels (bottom). */
const PAD = { left: 48, right: 44, top: 12, bottom: 28 };

/** Hand-written SVG line chart: inbox unread (left axis) and % covered by policy (right axis), one point per day. */
@Component({
  selector: 'app-triage-chart',
  imports: [DatePipe, DecimalPipe],
  template: `
    <div class="relative" (mouseleave)="active.set(null)">
      <svg
        [attr.width]="width()"
        [attr.height]="height"
        [attr.viewBox]="'0 0 ' + width() + ' ' + height"
        role="img"
        tabindex="0"
        [attr.aria-label]="summary()"
        aria-describedby="triage-chart-keys"
        (mousemove)="hover($event)"
        (keydown)="key($event)"
        (blur)="active.set(null)"
        data-testid="triage-chart"
      >
        @for (t of unreadTicks(); track $index) {
          <line
            class="grid"
            [attr.x1]="box().left"
            [attr.x2]="box().left + box().width"
            [attr.y1]="t.y"
            [attr.y2]="t.y"
          />
          <text
            class="axis"
            [attr.x]="box().left - 6"
            [attr.y]="t.y"
            text-anchor="end"
            dominant-baseline="middle"
          >
            {{ t.value | number: '1.0-0' }}
          </text>
        }
        @for (t of coverageTicks(); track $index) {
          <text
            class="axis"
            [attr.x]="box().left + box().width + 6"
            [attr.y]="t.y"
            dominant-baseline="middle"
          >
            {{ t.value }}%
          </text>
        }
        @for (d of dayLabels(); track d.index) {
          <text class="axis" [attr.x]="d.x" [attr.y]="height - 8" [attr.text-anchor]="d.anchor">
            {{ d.day | date: 'd MMM' }}
          </text>
        }
        <path class="line unread" [attr.d]="unreadPath()" data-testid="unread-path" />
        <path class="line coverage" [attr.d]="coveragePath()" data-testid="coverage-path" />
        @if (points().length === 1) {
          <circle class="dot unread" [attr.cx]="xOf(0)" [attr.cy]="unreadY(0)" r="3" />
          <circle class="dot coverage" [attr.cx]="xOf(0)" [attr.cy]="coverageY(0)" r="3" />
        }
        @if (activePoint(); as a) {
          <line
            class="cursor"
            [attr.x1]="a.x"
            [attr.x2]="a.x"
            [attr.y1]="box().top"
            [attr.y2]="box().top + box().height"
          />
          <circle class="dot unread" [attr.cx]="a.x" [attr.cy]="a.unreadY" r="4" />
          <circle class="dot coverage" [attr.cx]="a.x" [attr.cy]="a.coverageY" r="4" />
        }
      </svg>
      @if (activePoint(); as a) {
        <div
          class="tooltip"
          [style.left.px]="a.x"
          [class.flip]="a.x > width() / 2"
          role="status"
          data-testid="chart-tooltip"
        >
          <div class="font-medium">{{ a.point.day | date: 'mediumDate' }}</div>
          <div><span class="swatch unread"></span>Inbox unread: {{ a.point.unread | number }}</div>
          <div>
            <span class="swatch coverage"></span>Covered by policy:
            {{ a.point.coverage | number: '1.1-1' }}%
          </div>
        </div>
      }
      <div class="legend mt-1 flex flex-wrap gap-4">
        <span><span class="swatch unread"></span>Inbox unread</span>
        <span><span class="swatch coverage"></span>% covered by policy</span>
        <span id="triage-chart-keys" class="sr-only"
          >Use the left and right arrow keys to read each day.</span
        >
      </div>
    </div>
  `,
  styles: `
    :host {
      display: block;
    }
    svg {
      display: block;
      outline-offset: 2px;
    }
    svg:focus-visible {
      outline: 2px solid var(--mat-sys-primary);
    }
    .grid {
      stroke: var(--mat-sys-outline-variant);
      stroke-width: 1;
    }
    .axis {
      fill: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-label-small);
    }
    .line {
      fill: none;
      stroke-width: 2;
      stroke-linejoin: round;
    }
    .line.unread,
    .dot.unread {
      stroke: var(--mat-sys-primary);
    }
    .line.coverage,
    .dot.coverage {
      stroke: var(--mat-sys-tertiary);
    }
    .dot {
      fill: var(--mat-sys-surface);
      stroke-width: 2;
    }
    .cursor {
      stroke: var(--mat-sys-outline);
      stroke-dasharray: 3 3;
    }
    .swatch {
      display: inline-block;
      width: 12px;
      height: 3px;
      margin-right: 6px;
      vertical-align: middle;
    }
    .swatch.unread {
      background: var(--mat-sys-primary);
    }
    .swatch.coverage {
      background: var(--mat-sys-tertiary);
    }
    .legend {
      color: var(--mat-sys-on-surface-variant);
      font: var(--mat-sys-body-small);
    }
    .tooltip {
      position: absolute;
      top: 0;
      transform: translateX(8px);
      pointer-events: none;
      white-space: nowrap;
      padding: 6px 8px;
      border-radius: var(--mat-sys-corner-small);
      background: var(--mat-sys-inverse-surface);
      color: var(--mat-sys-inverse-on-surface);
      font: var(--mat-sys-body-small);
    }
    .tooltip.flip {
      transform: translateX(calc(-100% - 8px));
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TriageChart {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly destroyRef = inject(DestroyRef);

  readonly points = input.required<readonly TriagePoint[]>();
  readonly height = CHART_HEIGHT;
  /** Follows the host's width; the default holds until the first measurement. */
  readonly width = signal(600);
  /** The point under the pointer or keyboard cursor. */
  readonly active = signal<number | null>(null);

  readonly box = computed<PlotBox>(() => ({
    left: PAD.left,
    top: PAD.top,
    width: Math.max(1, this.width() - PAD.left - PAD.right),
    height: CHART_HEIGHT - PAD.top - PAD.bottom,
  }));
  readonly unreadMax = computed(() => niceMax(Math.max(0, ...this.points().map((p) => p.unread))));
  readonly unreadPath = computed(() =>
    linePath(
      this.points().map((p) => p.unread),
      this.unreadMax(),
      this.box(),
    ),
  );
  readonly coveragePath = computed(() =>
    linePath(
      this.points().map((p) => p.coverage),
      100,
      this.box(),
    ),
  );
  readonly unreadTicks = computed(() =>
    [0, 0.5, 1].map((f) => {
      const value = this.unreadMax() * f;
      return { value, y: yAt(value, this.unreadMax(), this.box()) };
    }),
  );
  readonly coverageTicks = computed(() =>
    [0, 50, 100].map((value) => ({ value, y: yAt(value, 100, this.box()) })),
  );
  /** First, middle and last day, anchored so they stay inside the plot. */
  readonly dayLabels = computed(() => {
    const points = this.points();
    const n = points.length;
    const indexes = n === 1 ? [0] : n === 2 ? [0, 1] : [0, Math.floor((n - 1) / 2), n - 1];
    return indexes.map((index) => ({
      index,
      day: points[index].day,
      x: this.xOf(index),
      anchor: n === 1 ? 'middle' : index === 0 ? 'start' : index === n - 1 ? 'end' : 'middle',
    }));
  });
  readonly activePoint = computed(() => {
    const i = this.active();
    const point = i === null ? undefined : this.points()[i];
    if (i === null || !point) return null;
    return { point, x: this.xOf(i), unreadY: this.unreadY(i), coverageY: this.coverageY(i) };
  });
  readonly summary = computed(() => {
    const points = this.points();
    const last = points.at(-1);
    return last
      ? `Inbox unread and % covered by policy over ${points.length} days; latest ${last.unread} unread, ${last.coverage.toFixed(1)}% covered.`
      : 'No triage history yet.';
  });

  constructor() {
    afterNextRender(() => {
      const el = this.host.nativeElement;
      if (el.clientWidth > 0) this.width.set(el.clientWidth);
      if (typeof ResizeObserver === 'undefined') return;
      const observer = new ResizeObserver(([entry]) => {
        const w = Math.round(entry.contentRect.width);
        if (w > 0) this.width.set(w);
      });
      observer.observe(el);
      this.destroyRef.onDestroy(() => observer.disconnect());
    });
  }

  xOf(i: number): number {
    return xAt(i, this.points().length, this.box());
  }

  unreadY(i: number): number {
    return yAt(this.points()[i].unread, this.unreadMax(), this.box());
  }

  coverageY(i: number): number {
    return yAt(this.points()[i].coverage, 100, this.box());
  }

  hover(event: MouseEvent): void {
    const n = this.points().length;
    if (n === 0) return;
    const rect = (event.currentTarget as Element).getBoundingClientRect();
    this.active.set(nearestIndex(event.clientX - rect.left, n, this.box()));
  }

  /** Arrow keys, Home and End move the cursor; Escape hides it. */
  key(event: KeyboardEvent): void {
    const n = this.points().length;
    if (n === 0) return;
    // The first arrow press shows the latest day.
    const current = this.active() ?? n;
    let next: number | null;
    switch (event.key) {
      case 'ArrowRight':
        next = Math.min(n - 1, current + 1);
        break;
      case 'ArrowLeft':
        next = Math.max(0, current - 1);
        break;
      case 'Home':
        next = 0;
        break;
      case 'End':
        next = n - 1;
        break;
      case 'Escape':
        next = null;
        break;
      default:
        return;
    }
    event.preventDefault();
    this.active.set(next);
  }
}
