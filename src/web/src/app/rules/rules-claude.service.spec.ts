import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, Subject, throwError } from 'rxjs';
import { ExternalReviewDto } from '../core/claude.models';
import { ClaudeService } from '../core/claude.service';
import { JobsService } from '../core/jobs.service';
import { SettingsService } from '../settings/settings.service';
import { RulesClaude } from './rules-claude.service';
import { ruleReview } from './rules-claude.testing';

describe('RulesClaude', () => {
  let changes: Subject<ExternalReviewDto>;
  let reconnects: ReturnType<typeof signal<number>>;
  let list: ReturnType<typeof vi.fn>;

  function setup(
    items: ExternalReviewDto[],
    settings = of({ claudeReviewerMode: 'claude_desktop' }),
  ) {
    changes = new Subject();
    reconnects = signal(0);
    list = vi.fn(() => of({ items, page: 1, pageSize: 100, total: items.length }));
    TestBed.configureTestingModule({
      providers: [
        RulesClaude,
        { provide: ClaudeService, useValue: { list } },
        { provide: SettingsService, useValue: { getSettings: () => settings } },
        { provide: JobsService, useValue: { externalReviewChanges: changes, reconnects } },
      ],
    });
    const service = TestBed.inject(RulesClaude);
    TestBed.tick();
    return service;
  }

  it('reads the mode from the settings, and is off when they fail', () => {
    expect(setup([]).mode()).toBe('claude_desktop');
    TestBed.resetTestingModule();
    expect(
      setup(
        [],
        throwError(() => new Error('x')),
      ).mode(),
    ).toBe('off');
  });

  it('keeps the newest loaded item per finding and plan, and ignores review targets', () => {
    const service = setup([
      ruleReview({ id: 'new', createdAt: '2026-01-02T00:00:00Z' }),
      ruleReview({ id: 'old', status: 'cancelled' }),
      ruleReview({ id: 'p', targetType: 'label_plan', findingId: null, labelPlanId: 'p1' }),
      ruleReview({ id: 's', targetType: 'suggestion', findingId: null, suggestionId: 's1' }),
    ]);
    expect(service.forFinding('f1')?.id).toBe('new');
    expect(service.forPlan('p1')?.id).toBe('p');
    expect(service.forFinding('s1')).toBeNull();
  });

  it('follows live changes but not an older item', () => {
    const service = setup([ruleReview({ id: 'a', createdAt: '2026-01-02T00:00:00Z' })]);
    changes.next(ruleReview({ id: 'old', createdAt: '2026-01-01T00:00:00Z', status: 'reviewed' }));
    expect(service.forFinding('f1')?.id).toBe('a');
    changes.next(ruleReview({ id: 'a', createdAt: '2026-01-02T00:00:00Z', status: 'reviewed' }));
    expect(service.forFinding('f1')?.status).toBe('reviewed');
  });

  it('reloads after a reconnect', () => {
    setup([]);
    expect(list).toHaveBeenCalledTimes(1);
    reconnects.set(1);
    TestBed.tick();
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('keeps a live reviewed item when the reconnect reload returns it still running', () => {
    const reload = new Subject<{
      items: ExternalReviewDto[];
      page: number;
      pageSize: number;
      total: number;
    }>();
    const service = setup([ruleReview({ id: 'a', status: 'running' })]);
    list.mockReturnValue(reload);
    reconnects.set(1);
    TestBed.tick();
    changes.next(ruleReview({ id: 'a', status: 'reviewed', reviewedAt: '2026-01-01T00:05:00Z' }));
    reload.next({
      items: [ruleReview({ id: 'a', status: 'running' })],
      page: 1,
      pageSize: 100,
      total: 1,
    });
    expect(service.forFinding('f1')?.status).toBe('reviewed');
  });

  it('keeps running over a late queued copy, and a resolved item over a reviewed one', () => {
    const service = setup([]);
    changes.next(ruleReview({ id: 'a', status: 'running' }));
    service.patch(ruleReview({ id: 'a', status: 'queued' }));
    expect(service.forFinding('f1')?.status).toBe('running');
    const resolved = { status: 'reviewed' as const, resolution: 'dismissed' as const };
    changes.next(ruleReview({ id: 'a', ...resolved, resolvedAt: '2026-01-01T00:06:00Z' }));
    changes.next(ruleReview({ id: 'a', status: 'reviewed' }));
    expect(service.forFinding('f1')?.resolution).toBe('dismissed');
  });

  it('follows a retry from unavailable back to queued', () => {
    const service = setup([]);
    changes.next(ruleReview({ id: 'a', status: 'unavailable' }));
    changes.next(ruleReview({ id: 'a', status: 'queued' }));
    expect(service.forFinding('f1')?.status).toBe('queued');
  });
});
