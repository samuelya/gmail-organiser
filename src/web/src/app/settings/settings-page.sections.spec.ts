import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { onTestFinished } from 'vitest';
import { SECTION_FOLLOW_MS, SECTIONS } from './settings-page.component';
import { EMAIL, fullSettings, SettingsPageTest } from './settings-page.testing';

/** Replaces `scrollIntoView` (jsdom has none) for one test. */
function stubScroll() {
  const scroll = vi.fn();
  const original = Object.getOwnPropertyDescriptor(Element.prototype, 'scrollIntoView');
  Object.defineProperty(Element.prototype, 'scrollIntoView', {
    configurable: true,
    writable: true,
    value: scroll,
  });
  onTestFinished(() => {
    if (original) Object.defineProperty(Element.prototype, 'scrollIntoView', original);
    else delete (Element.prototype as Partial<Element>).scrollIntoView;
  });
  return scroll;
}

/** Answers the reduced-motion query for one test; other queries never match, as in jsdom. */
function stubReducedMotion(reduceMotion: boolean) {
  const matchMedia = vi.fn((query: string) =>
    query === '(prefers-reduced-motion: reduce)'
      ? ({ matches: reduceMotion } as MediaQueryList)
      : ({
          matches: false,
          addListener: () => undefined,
          removeListener: () => undefined,
        } as unknown as MediaQueryList),
  );
  vi.stubGlobal('matchMedia', matchMedia);
  return matchMedia;
}

/** A `ResizeObserver` the test fires by hand; jsdom has none. */
function stubResizeObserver() {
  const observers: { fire: () => void; observed: Element[]; disconnected: boolean }[] = [];
  class FakeResizeObserver {
    private readonly entry = {
      fire: () => undefined,
      observed: [] as Element[],
      disconnected: false,
    };
    constructor(callback: () => void) {
      this.entry.fire = () => {
        if (!this.entry.disconnected) callback();
      };
      observers.push(this.entry);
    }
    observe(el: Element) {
      this.entry.observed.push(el);
    }
    disconnect() {
      this.entry.disconnected = true;
    }
  }
  vi.stubGlobal('ResizeObserver', FakeResizeObserver);
  return observers;
}

describe('SettingsPage section index', () => {
  let ctx: SettingsPageTest;

  beforeEach(() => (ctx = new SettingsPageTest(vi.fn(), vi.fn())));
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    ctx.verify();
  });

  const links = (el: HTMLElement) =>
    [...el.querySelectorAll<HTMLElement>('nav[aria-label="Settings sections"] li button')].map(
      (b) => b.textContent!.trim(),
    );
  /** The column of cards the deep link watches. */
  const cardsOf = (el: HTMLElement) =>
    el.querySelector('#settings-setup')!.closest('mat-card')!.parentElement!;
  const headings = (el: HTMLElement) =>
    [...el.querySelectorAll<HTMLElement>('mat-card h2')].map((h) => h.textContent!.trim());

  it('shows the page description', async () => {
    const { q } = await ctx.render();
    expect(q('page-description')?.textContent).toContain('Each section saves on its own.');
  });

  it('lists every section heading in page order, as buttons rather than hash links', async () => {
    const { el } = await ctx.render(true, fullSettings());
    expect(headings(el)).toEqual(SECTIONS.map((s) => s.label));
    expect(links(el)).toEqual(headings(el));
    for (const s of SECTIONS) expect(el.querySelector(`#settings-${s.id}`)).not.toBeNull();
    expect(el.querySelector('nav a[href]')).toBeNull();
  });

  it('leaves out the cards an older API hides', async () => {
    const { el } = await ctx.render();
    expect(links(el)).not.toContain('Claude review');
    expect(links(el)).toEqual(headings(el));
  });

  it('clicking a link scrolls smoothly to the section and focuses its heading', async () => {
    const scroll = stubScroll();
    stubReducedMotion(false);
    const { el, q } = await ctx.render();
    q('index-retention')!.click();
    const heading = el.querySelector<HTMLElement>('#settings-retention')!;
    expect(document.activeElement).toBe(heading);
    expect(heading.getAttribute('tabindex')).toBe('-1');
    expect(scroll.mock.contexts[0]).toBe(heading);
    expect(scroll).toHaveBeenCalledWith({ block: 'start', behavior: 'smooth' });
  });

  it('scrolls without animation under prefers-reduced-motion', async () => {
    const scroll = stubScroll();
    const matchMedia = stubReducedMotion(true);
    const { q } = await ctx.render();
    q('index-data')!.click();
    expect(matchMedia).toHaveBeenCalledWith('(prefers-reduced-motion: reduce)');
    expect(scroll).toHaveBeenCalledWith({ block: 'start', behavior: 'auto' });
  });

  it('?section=claude focuses Claude review once the settings load', async () => {
    const scroll = stubScroll();
    const { harness, el } = await ctx.openAt('/settings?section=claude', true, fullSettings());
    await harness.fixture.whenStable();
    const heading = el.querySelector<HTMLElement>('#settings-claude')!;
    expect(document.activeElement).toBe(heading);
    expect(scroll.mock.contexts[0]).toBe(heading);
    expect(scroll).toHaveBeenCalledWith({ block: 'start', behavior: 'auto' });
  });

  it('an unknown ?section= does nothing', async () => {
    const scroll = stubScroll();
    const { harness } = await ctx.openAt('/settings?section=nope');
    await harness.fixture.whenStable();
    expect(scroll).not.toHaveBeenCalled();
    expect(document.activeElement?.tagName).not.toBe('H2');
  });

  describe('while the cards above load', () => {
    async function openRetention() {
      const scroll = stubScroll();
      const observers = stubResizeObserver();
      const page = await ctx.openAt('/settings?section=retention');
      await page.harness.fixture.whenStable();
      const heading = page.el.querySelector<HTMLElement>('#settings-retention')!;
      const following = observers.filter((o) => o.observed.includes(cardsOf(page.el)));
      expect(following).toHaveLength(1);
      scroll.mockClear();
      return { ...page, scroll, observer: following[0], heading };
    }

    it('re-scrolls to the heading whenever the cards change height', async () => {
      const { scroll, observer, heading } = await openRetention();
      observer.fire();
      observer.fire();
      expect(scroll).toHaveBeenCalledTimes(2);
      expect(scroll.mock.contexts[1]).toBe(heading);
      expect(scroll).toHaveBeenCalledWith({ block: 'start', behavior: 'auto' });
      expect(document.activeElement).toBe(heading);
    });

    it('stops once the user scrolls or presses a key', async () => {
      const { scroll, observer } = await openRetention();
      window.dispatchEvent(new Event('wheel'));
      expect(observer.disconnected).toBe(true);
      observer.fire();
      expect(scroll).not.toHaveBeenCalled();
    });

    it('stops after a while even if the cards keep changing', async () => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'], shouldAdvanceTime: true });
      const { observer } = await openRetention();
      vi.advanceTimersByTime(SECTION_FOLLOW_MS - 100);
      observer.fire();
      expect(observer.disconnected).toBe(false);
      vi.advanceTimersByTime(100);
      expect(observer.disconnected).toBe(true);
    });
  });

  it('callback connected: shows the result on the Gmail card, scrolls to it, clears the params', async () => {
    const scroll = stubScroll();
    const { el, q } = await ctx.openAt('/settings?gmail=connected');
    expect(q('connect-success')!.textContent).toContain(`Gmail connected as ${EMAIL}.`);
    expect(TestBed.inject(Router).url).toBe('/settings');
    expect(scroll).toHaveBeenCalledTimes(1);
    expect(scroll.mock.contexts[0]).toBe(el.querySelector('[data-testid="section-gmail"]'));
  });

  it('callback plus ?section=: only the Gmail result is scrolled to', async () => {
    const scroll = stubScroll();
    const observers = stubResizeObserver();
    const { harness, el } = await ctx.openAt('/settings?gmail=connected&section=retention');
    await harness.fixture.whenStable();
    expect(scroll).toHaveBeenCalledTimes(1);
    expect(scroll.mock.contexts[0]).toBe(el.querySelector('[data-testid="section-gmail"]'));
    expect(document.activeElement?.id).not.toBe('settings-retention');
    expect(observers.filter((o) => o.observed.includes(cardsOf(el)))).toHaveLength(0);
  });
});
