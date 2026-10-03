import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { of, Subject, throwError } from 'rxjs';
import { isActiveJob, JobDto, JobsConnectionState } from '../core/jobs.models';
import { JobsService } from '../core/jobs.service';
import { SettingsService } from '../settings/settings.service';
import { SendersPage } from './senders-page.component';
import { SenderDto } from './senders.models';
import { SendersService } from './senders.service';

const sender = (over: Partial<SenderDto> = {}): SenderDto => ({
  address: 'news@example.com',
  domain: 'example.com',
  displayName: 'Example News',
  totalCount: 10,
  analysedCount: 4,
  appliedCount: 1,
  lastSeenAt: '2026-01-01T00:00:00Z',
  allowlisted: false,
  activeFetchJob: null,
  unsubscribedAt: null,
  ...over,
});

class FakeJobs {
  readonly held = signal<JobDto[]>([]);
  readonly jobs = this.held.asReadonly();
  readonly activeJobs = computed(() => this.held().filter(isActiveJob));
  readonly connectionState = signal<JobsConnectionState>('connected');
  readonly reconnects = signal(0);
  job(id: string) {
    return this.held().find((j) => j.id === id);
  }
}

describe('SendersPage allowlist toggle', () => {
  let api: {
    list: ReturnType<typeof vi.fn>;
    fetchFromSender: ReturnType<typeof vi.fn>;
    setAllowlisted: ReturnType<typeof vi.fn>;
  };

  async function render(row: SenderDto) {
    api = {
      list: vi.fn(() => of({ items: [row], page: 1, pageSize: 50, total: 1 })),
      fetchFromSender: vi.fn(),
      setAllowlisted: vi.fn(),
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'senders', component: SendersPage }]),
        { provide: JobsService, useValue: new FakeJobs() },
        { provide: SendersService, useValue: api },
        { provide: SettingsService, useValue: { getSettings: () => of({}) } },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/senders', SendersPage);
    const el = harness.routeNativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    return { harness, q };
  }

  it('allowlists a sender and shows the shield chip once saved', async () => {
    const { harness, q } = await render(sender());
    const saved = new Subject<SenderDto>();
    api.setAllowlisted.mockReturnValue(saved);
    expect(q('allowlisted-chip')).toBeNull();

    q('allowlist-sender')!.click();
    await harness.fixture.whenStable();
    expect(api.setAllowlisted).toHaveBeenCalledWith('news@example.com', true);
    expect((q('allowlist-sender') as HTMLButtonElement).disabled).toBe(true);

    saved.next(sender({ allowlisted: true }));
    saved.complete();
    await harness.fixture.whenStable();
    expect(q('allowlisted-chip')?.textContent).toContain('Allowlisted');
    expect(q('allowlist-sender')!.getAttribute('aria-pressed')).toBe('true');
  });

  it('removes an allowlisted sender and drops the chip', async () => {
    const { harness, q } = await render(sender({ allowlisted: true }));
    api.setAllowlisted.mockReturnValue(of(sender({ allowlisted: false })));
    expect(q('allowlisted-chip')).not.toBeNull();

    q('allowlist-sender')!.click();
    await harness.fixture.whenStable();
    expect(api.setAllowlisted).toHaveBeenCalledWith('news@example.com', false);
    expect(q('allowlisted-chip')).toBeNull();
  });

  it('keeps the row as it was when the save fails', async () => {
    const { harness, q } = await render(sender());
    api.setAllowlisted.mockReturnValue(throwError(() => new Error('failed')));

    q('allowlist-sender')!.click();
    await harness.fixture.whenStable();
    expect(q('allowlisted-chip')).toBeNull();
    expect((q('allowlist-sender') as HTMLButtonElement).disabled).toBe(false);
  });
});
