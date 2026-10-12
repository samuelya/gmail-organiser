import { HttpErrorResponse } from '@angular/common/http';
import { Injector } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router } from '@angular/router';
import { of, Subject, throwError } from 'rxjs';
import { run } from './analyse.testing';
import { AnalysisFinishNotice, FINISH_NOTICE_MS, noticeOf } from './analysis-finish-notice';
import { AnalysisRunDto, TOP_SENDERS } from './analysis.models';
import { AnalysisService } from './analysis.service';

interface FakeRef {
  message: string;
  action: string | undefined;
  act: Subject<void>;
  dismissed: Subject<void>;
  dismiss: ReturnType<typeof vi.fn>;
}

describe('noticeOf', () => {
  it('words a completed analyse run by its count and links Review only when it covered messages', () => {
    expect(noticeOf(run({ status: 'completed', messagesCovered: 1 }))).toEqual({
      message: 'Analysis finished: 1 message covered.',
      action: 'Review',
      link: { path: '/review', queryParams: { status: 'pending' } },
    });
    expect(noticeOf(run({ status: 'completed', messagesCovered: 0 }))).toEqual({
      message: 'Analysis finished: no messages covered.',
    });
  });

  it('narrows Review to the sender of a sender run, not of a top senders run', () => {
    const sender = 'news@example.com';
    const done = { status: 'completed' as const, messagesCovered: 2, senderAddress: sender };
    expect(noticeOf(run({ ...done, scope: 'sender' }))!.link!.queryParams).toEqual({
      status: 'pending',
      sender,
    });
    expect(noticeOf(run({ ...done, scope: TOP_SENDERS }))!.link!.queryParams).toEqual({
      status: 'pending',
    });
  });

  it('a failed run links to the failed runs; cancelled and compare runs say nothing', () => {
    expect(noticeOf(run({ status: 'failed' }))!.link).toEqual({
      path: '/analyse',
      queryParams: { status: 'failed' },
    });
    expect(noticeOf(run({ status: 'cancelled' }))).toBeNull();
    expect(noticeOf(run({ kind: 'compare', status: 'completed', messagesCovered: 3 }))).toBeNull();
  });
});

describe('AnalysisFinishNotice', () => {
  let refs: FakeRef[];
  let getRun: ReturnType<typeof vi.fn>;
  let navigate: ReturnType<typeof vi.fn>;
  let notice: AnalysisFinishNotice;
  let injector: Injector & { destroy(): void };

  beforeEach(() => {
    refs = [];
    getRun = vi.fn();
    navigate = vi.fn(() => Promise.resolve(true));
    TestBed.configureTestingModule({
      providers: [
        { provide: AnalysisService, useValue: { getRun } },
        { provide: Router, useValue: { navigate } },
        {
          provide: MatSnackBar,
          useValue: {
            open: (message: string, action: string | undefined, config: { duration: number }) => {
              expect(config.duration).toBe(FINISH_NOTICE_MS);
              const ref: FakeRef = {
                message,
                action,
                act: new Subject(),
                dismissed: new Subject(),
                dismiss: vi.fn(),
              };
              refs.push(ref);
              return {
                onAction: () => ref.act,
                afterDismissed: () => ref.dismissed,
                dismiss: ref.dismiss,
              };
            },
          },
        },
      ],
    });
    // A child injector stands in for the page that provides the service.
    injector = Injector.create({
      providers: [{ provide: AnalysisFinishNotice }],
      parent: TestBed.inject(Injector),
    }) as Injector & { destroy(): void };
    notice = injector.get(AnalysisFinishNotice);
  });

  const ids = (...list: string[]) => new Set(list);
  const done = (id: string, over: Partial<AnalysisRunDto> = {}) =>
    run({ id, status: 'completed', messagesCovered: 2, ...over });

  it('a run found finished gets one notice, and its action opens Review', () => {
    notice.update(ids('a'), false, []);
    expect(notice.update(ids(), false, [done('a')])).toBe(true);
    expect(refs.map((r) => r.message)).toEqual(['Analysis finished: 2 messages covered.']);
    refs[0].act.next();
    expect(navigate).toHaveBeenCalledWith(['/review'], { queryParams: { status: 'pending' } });
    notice.update(ids(), false, [done('a')]);
    expect(refs).toHaveLength(1);
  });

  it('runs already finished, or never seen active, say nothing', () => {
    expect(notice.update(ids(), false, [done('a')])).toBe(false);
    expect(refs).toHaveLength(0);
  });

  it('a run in neither list is looked up by id rather than dropped', () => {
    getRun.mockReturnValue(of(run({ id: 'a', status: 'failed' })));
    notice.update(ids('a'), false, []);
    expect(notice.update(ids(), false, [])).toBe(true);
    expect(getRun).toHaveBeenCalledWith('a');
    expect(refs[0].message).toBe('Analysis failed.');
    refs[0].act.next();
    expect(navigate).toHaveBeenCalledWith(['/analyse'], { queryParams: { status: 'failed' } });
  });

  it('a lookup that finds the run still active keeps watching it', () => {
    getRun.mockReturnValue(of(run({ id: 'a', status: 'running' })));
    notice.update(ids('a'), false, []);
    notice.update(ids(), false, []);
    expect(refs).toHaveLength(0);
    notice.update(ids(), false, [done('a')]);
    expect(refs).toHaveLength(1);
  });

  it('a failed lookup retries on the next reload; a 404 drops the run', () => {
    getRun.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 500 })));
    notice.update(ids('a'), false, []);
    notice.update(ids(), false, []);
    getRun.mockReturnValueOnce(throwError(() => new HttpErrorResponse({ status: 404 })));
    notice.update(ids(), false, []);
    notice.update(ids(), false, []);
    expect(getRun).toHaveBeenCalledTimes(2);
  });

  it('with a truncated queue a missing run is unknown, not ended', () => {
    notice.update(ids('a'), false, []);
    expect(notice.update(ids(), true, [])).toBe(false);
    expect(getRun).not.toHaveBeenCalled();
    expect(notice.update(ids(), true, [done('a')])).toBe(true);
    expect(refs).toHaveLength(1);
  });

  it('several runs ending together queue their notices', () => {
    notice.update(ids('a', 'b'), false, []);
    notice.update(ids(), false, [done('a'), run({ id: 'b', status: 'failed' })]);
    expect(refs.map((r) => r.message)).toEqual(['Analysis finished: 2 messages covered.']);
    refs[0].dismissed.next();
    expect(refs.map((r) => r.message)).toEqual([
      'Analysis finished: 2 messages covered.',
      'Analysis failed.',
    ]);
  });

  it('a run the page just started is watched before any reload lists it', () => {
    notice.watch('a');
    notice.update(ids(), false, [done('a')]);
    expect(refs).toHaveLength(1);
  });

  it('the page ending dismisses the notice and drops the queue', () => {
    notice.update(ids('a', 'b'), false, []);
    notice.update(ids(), false, [done('a'), done('b')]);
    injector.destroy();
    expect(refs[0].dismiss).toHaveBeenCalled();
    refs[0].dismissed.next();
    refs[0].act.next();
    expect(refs).toHaveLength(1);
    expect(navigate).not.toHaveBeenCalled();
  });
});
