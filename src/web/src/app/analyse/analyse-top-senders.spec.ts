import { TestBed } from '@angular/core/testing';
import { MATERIAL_ANIMATIONS } from '@angular/material/core';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { of } from 'rxjs';
import { ClaudeService } from '../core/claude.service';
import { JobsService } from '../core/jobs.service';
import { SendersService } from '../senders/senders.service';
import { SettingsService } from '../settings/settings.service';
import { AnalysePage, PREVIEW_DEBOUNCE_MS } from './analyse-page.component';
import { FakeJobs, preview, run, wait } from './analyse.testing';
import { AnalysisRunDto, isScope, requestedText, usageText } from './analysis.models';
import { AnalysisService } from './analysis.service';

describe('top senders models', () => {
  it('counts senders for a top senders run and emails otherwise', () => {
    expect(isScope('top_senders')).toBe(true);
    expect(requestedText({ scope: 'top_senders', requestedCount: 10 })).toBe('10 senders');
    expect(requestedText({ scope: 'top_senders', requestedCount: 1 })).toBe('1 sender');
    expect(requestedText({ scope: 'inbox', requestedCount: 20 })).toBe('20 emails');
  });

  it('reads the model usage, or nothing before any model call', () => {
    expect(usageText({ promptTokens: 0, completionTokens: 0, llmSeconds: 0 })).toBeNull();
    expect(usageText({ promptTokens: 1200, completionTokens: 300, llmSeconds: 12.54 })).toBe(
      `${(1200).toLocaleString()} prompt + 300 completion tokens · 12.5 s LLM`,
    );
  });
});

describe('AnalysePage top senders', () => {
  let api: {
    preview: ReturnType<typeof vi.fn>;
    start: ReturnType<typeof vi.fn>;
    listRuns: ReturnType<typeof vi.fn>;
  };
  let active: AnalysisRunDto[];
  let finished: AnalysisRunDto[];

  async function render() {
    active = [];
    finished = [];
    api = {
      preview: vi.fn(() => of(preview())),
      start: vi.fn(() => of(run({ id: 'run-new', status: 'queued', scope: 'top_senders' }))),
      listRuns: vi.fn((isActive: boolean) => of(isActive ? active : finished)),
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'analyse', component: AnalysePage }]),
        { provide: JobsService, useValue: new FakeJobs() },
        { provide: AnalysisService, useValue: api },
        {
          provide: SendersService,
          useValue: { list: vi.fn(() => of({ items: [], page: 1, pageSize: 10, total: 0 })) },
        },
        {
          provide: SettingsService,
          useValue: {
            getSettings: () =>
              of({ analysisDefaultCount: 20, claudeReviewerMode: 'claude_desktop' }),
          },
        },
        { provide: ClaudeService, useValue: { createReviews: vi.fn() } },
        { provide: MATERIAL_ANIMATIONS, useValue: { animationsDisabled: true } },
      ],
    });
    const harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl('/analyse', AnalysePage);
    const el = harness.routeNativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    const all = (id: string) => [...el.querySelectorAll<HTMLElement>(`[data-testid="${id}"]`)];
    const text = (id: string) => q(id)?.textContent?.replace(/\s+/g, ' ').trim() ?? null;
    const pickScope = async (label: string) => {
      [...el.querySelectorAll<HTMLElement>('[data-testid="scope"] button')]
        .find((b) => b.textContent?.trim() === label)!
        .click();
      await harness.fixture.whenStable();
    };
    return { harness, component, el, q, all, text, pickScope };
  }

  it('switching to Top senders counts senders from 10, and back restores the email count', async () => {
    const { harness, component, q, text, pickScope } = await render();
    expect(text('count-label')).toBe('Emails to analyse');
    expect(q('senders-hint')).toBeNull();

    await pickScope('Top senders');
    expect(text('count-label')).toBe('Senders to analyse');
    expect(text('senders-hint')).toBe('1–100 senders, one model call each.');
    expect(component.form.getRawValue().preset).toBe(10);
    expect(q('sender')).not.toBeNull();
    expect(component.selection()).toEqual({ scope: 'top_senders', count: 10 });

    component.form.patchValue({ preset: 'custom', customCount: 150 });
    await harness.fixture.whenStable();
    expect(component.selection()).toBeNull();
    expect(text('count-hint')).toBe('1–100');
    component.form.patchValue({ customCount: 40, sender: 'news@example.com' });
    expect(component.selection()).toEqual({
      scope: 'top_senders',
      senderAddress: 'news@example.com',
      count: 40,
    });

    await pickScope('Inbox');
    expect(text('count-label')).toBe('Emails to analyse');
    expect(component.form.getRawValue().preset).toBe(20);
    expect(component.selection()).toEqual({ scope: 'inbox', count: 20 });

    await pickScope('Top senders');
    expect(component.form.getRawValue()).toMatchObject({ preset: 'custom', customCount: 40 });
  });

  it('Start posts the top senders selection without a sender', async () => {
    const { harness, q, pickScope } = await render();
    await pickScope('Top senders');
    active = [run({ id: 'run-new', status: 'queued', scope: 'top_senders', requestedCount: 10 })];
    q('start')!.click();
    await harness.fixture.whenStable();
    expect(api.start).toHaveBeenCalledWith({ scope: 'top_senders', count: 10 });
    expect(q('active-run')!.textContent).toContain('Top senders · 10 senders');
  });

  it('the preview lists the candidate senders and lists', async () => {
    const { harness, all, text, pickScope } = await render();
    api.preview.mockReturnValue(
      of(
        preview({
          messages: 70,
          groups: 2,
          estimatedLlmCalls: 2,
          largestGroups: [],
          senders: [
            {
              scope: 'sender',
              scopeKey: 'news@example.com',
              displayName: 'Weekly news',
              count: 50,
            },
            { scope: 'list', scopeKey: 'offers.example.com', displayName: null, count: 20 },
          ],
        }),
      ),
    );
    await pickScope('Top senders');
    await wait(PREVIEW_DEBOUNCE_MS + 50);
    await harness.fixture.whenStable();
    expect(api.preview).toHaveBeenLastCalledWith({ scope: 'top_senders', count: 10 });
    expect(text('preview-summary')).toBe('2 senders · 70 emails → ≈ 2 LLM calls');
    const items = all('preview-sender').map((li) => li.textContent!.replace(/\s+/g, ' ').trim());
    expect(items).toEqual([
      'Weekly news news@example.com 50 emails',
      'offers.example.com List: offers.example.com 20 emails',
    ]);
  });

  it('a finished run shows policies, tokens, LLM seconds and the context warning', async () => {
    const { harness, component, q, all, text } = await render();
    finished.push(
      run({
        id: 'run-p',
        scope: 'top_senders',
        status: 'completed',
        messagesCovered: 70,
        groups: 2,
        llmCalls: 2,
        policiesProposed: 2,
        promptTokens: 900,
        completionTokens: 80,
        llmSeconds: 4,
        nearContextLimit: 1,
      }),
      run({ id: 'run-0', scope: 'top_senders', status: 'completed' }),
    );
    component.loadRuns();
    await harness.fixture.whenStable();

    expect(all('run-policies').map((s) => s.textContent!.replace(/\s+/g, ' ').trim())).toEqual([
      '· 2 policies proposed',
      '· 0 policies proposed',
    ]);
    expect(text('run-usage')).toContain('900 prompt + 80 completion tokens · 4 s LLM');
    expect(all('run-usage')).toHaveLength(1);
    expect(q('run-near-context')!.getAttribute('aria-label')).toContain(
      '1 model call filled at least 90 %',
    );
    const links = all('run-review-policies');
    expect(links).toHaveLength(1);
    expect(links[0].getAttribute('href')).toBe('/policies?status=proposed');
    // A policy run writes no suggestions: nothing to re-analyse or send to Claude.
    expect(q('run-reanalyse')).toBeNull();
    expect(q('run-claude')).toBeNull();
  });
});
