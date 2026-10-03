import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { of, Subject } from 'rxjs';
import { UnsubscribeInfo, UnsubscribeResult } from './clean-up.models';
import { CleanUpService } from './clean-up.service';
import { UnsubscribeButton } from './unsubscribe-button.component';

const info = (over: Partial<UnsubscribeInfo> = {}): UnsubscribeInfo => ({
  method: null,
  url: null,
  messageId: null,
  unsubscribedAt: null,
  unsubscribedVia: null,
  ...over,
});

describe('UnsubscribeButton', () => {
  afterEach(() => document.querySelector('.cdk-overlay-container')?.replaceChildren());

  async function render(first: UnsubscribeInfo) {
    const api = {
      unsubscribeInfo: vi.fn(() => of(first)),
      unsubscribe: vi.fn(() => of<UnsubscribeResult>({ status: 'done', httpStatus: 200 })),
      markUnsubscribed: vi.fn(() => of(undefined)),
    };
    TestBed.configureTestingModule({
      providers: [
        { provide: CleanUpService, useValue: api },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const fixture = TestBed.createComponent(UnsubscribeButton);
    fixture.componentRef.setInput('address', 'news@example.com');
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    const settle = async () => {
      fixture.detectChanges();
      await fixture.whenStable();
    };
    return { fixture, api, q, settle };
  }

  const snackText = () => document.querySelector('mat-snack-bar-container')?.textContent ?? '';
  const settleOverlay = () => new Promise((r) => setTimeout(r));

  it('disables the button with a tooltip when the sender has no unsubscribe header', async () => {
    const { api, q } = await render(info());
    expect(api.unsubscribeInfo).toHaveBeenCalledWith('news@example.com');
    expect((q('unsubscribe') as HTMLButtonElement).disabled).toBe(true);
    expect(q('unsubscribe-none')!.getAttribute('tabindex')).toBe('0');
    expect(q('unsubscribe-host')).toBeNull();
    expect(q('unsubscribed-chip')).toBeNull();
  });

  it('one-click: POSTs through the api with a spinner, then says Unsubscribed and reloads', async () => {
    const { api, q, settle } = await render(
      info({ method: 'one_click', url: 'https://lists.example.com/u/token-123' }),
    );
    const pending = new Subject<UnsubscribeResult>();
    api.unsubscribe.mockReturnValue(pending);
    expect(q('unsubscribe-host')!.textContent).toContain('lists.example.com');
    expect(q('unsubscribe-host')!.textContent).not.toContain('token');
    q('unsubscribe')!.click();
    await settle();
    expect(api.unsubscribe).toHaveBeenCalledWith('news@example.com');
    expect(q('unsubscribe-spinner')).not.toBeNull();
    expect((q('unsubscribe') as HTMLButtonElement).disabled).toBe(true);
    pending.next({ status: 'done', httpStatus: 200 });
    pending.complete();
    await settle();
    await settleOverlay();
    expect(q('unsubscribe-spinner')).toBeNull();
    expect(snackText()).toContain('Unsubscribed');
    expect(api.unsubscribeInfo).toHaveBeenCalledTimes(2);
    expect(q('unsubscribe-fallback')).toBeNull();
  });

  it('one-click failure offers the link in a new tab and the mark button', async () => {
    const { api, q, settle } = await render(
      info({ method: 'one_click', url: 'https://lists.example.com/u/token-123' }),
    );
    api.unsubscribe.mockReturnValue(of({ status: 'failed', httpStatus: 500 }));
    q('unsubscribe')!.click();
    await settle();
    await settleOverlay();
    expect(snackText()).toContain('Unsubscribe failed (HTTP 500); open the link instead');
    const link = q('unsubscribe-fallback') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('https://lists.example.com/u/token-123');
    expect(link.target).toBe('_blank');
    expect(link.rel).toBe('noopener noreferrer');
    link.addEventListener('click', (e) => e.preventDefault());
    link.click();
    await settle();
    q('unsubscribe-mark')!.click();
    await settle();
    expect(api.markUnsubscribed).toHaveBeenCalledWith('news@example.com', 'link');
  });

  it('link: opens the URL in a new tab, never this window, then marks it', async () => {
    const { api, q, settle } = await render(
      info({ method: 'link', url: 'http://example.com/leave?id=42' }),
    );
    const link = q('unsubscribe') as HTMLAnchorElement;
    expect(link.tagName).toBe('A');
    expect(link.target).toBe('_blank');
    expect(link.rel).toBe('noopener noreferrer');
    expect(q('unsubscribe-mark')).toBeNull();
    link.addEventListener('click', (e) => e.preventDefault());
    link.click();
    await settle();
    expect(api.unsubscribe).not.toHaveBeenCalled();
    q('unsubscribe-mark')!.click();
    await settle();
    await settleOverlay();
    expect(api.markUnsubscribed).toHaveBeenCalledWith('news@example.com', 'link');
    expect(snackText()).toContain('Marked as unsubscribed');
    expect(q('unsubscribe-mark')).toBeNull();
    expect(api.unsubscribeInfo).toHaveBeenCalledTimes(2);
  });

  it('mailto: a real anchor for the mail client, then marks it as mailto', async () => {
    const { api, q, settle } = await render(
      info({ method: 'mailto', url: 'mailto:leave@lists.example.com?subject=stop' }),
    );
    const link = q('unsubscribe') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('mailto:leave@lists.example.com?subject=stop');
    expect(link.hasAttribute('target')).toBe(false);
    expect(q('unsubscribe-host')!.textContent).toContain('lists.example.com');
    link.addEventListener('click', (e) => e.preventDefault());
    link.click();
    await settle();
    q('unsubscribe-mark')!.click();
    await settle();
    expect(api.markUnsubscribed).toHaveBeenCalledWith('news@example.com', 'mailto');
  });

  it('shows the Unsubscribed chip and keeps the button available', async () => {
    const { q } = await render(
      info({
        method: 'one_click',
        url: 'https://lists.example.com/u',
        unsubscribedAt: '2026-03-04T10:00:00Z',
        unsubscribedVia: 'one_click',
      }),
    );
    expect(q('unsubscribed-chip')!.textContent).toContain('Unsubscribed Mar 4, 2026');
    expect((q('unsubscribe') as HTMLButtonElement).disabled).toBe(false);
  });

  it('treats a URL whose scheme does not fit the method as no header', async () => {
    const { q } = await render(info({ method: 'link', url: 'javascript:alert(1)' }));
    expect((q('unsubscribe') as HTMLButtonElement).disabled).toBe(true);
    expect(q('unsubscribe-none')).not.toBeNull();
  });
});
