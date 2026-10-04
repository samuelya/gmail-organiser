import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of } from 'rxjs';
import { errorInterceptor } from '../core/error.interceptor';
import { AnalysisService } from './analysis.service';
import { compareConfirmMessage, confirmCompareRun } from './compare-run';

describe('compare run', () => {
  let open: ReturnType<typeof vi.fn>;
  let confirmed: boolean;
  let dialogOpen: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    open = vi.fn();
    confirmed = true;
    dialogOpen = vi.fn(() => ({ afterClosed: () => of(confirmed) }));
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([errorInterceptor])),
        provideHttpClientTesting(),
        { provide: MatSnackBar, useValue: { open } },
        { provide: MatDialog, useValue: { open: dialogOpen } },
      ],
    });
  });

  function start(request: { suggestionIds: string[] } | { runId: string }, count: number) {
    const runs: unknown[] = [];
    const errors: unknown[] = [];
    confirmCompareRun(
      TestBed.inject(MatDialog),
      TestBed.inject(AnalysisService),
      request,
      count,
    ).subscribe({ next: (r) => runs.push(r), error: (e) => errors.push(e) });
    return { runs, errors };
  }

  it('says what goes to the LLM, with what, and that nothing changes until the user picks', () => {
    expect(compareConfirmMessage(1)).toBe(
      '1 email goes to the LLM with the current prompt and settings. ' +
        'The memory shortcut is skipped (memory hints still apply). ' +
        'Current suggestions stay as they are until you pick "Use new" or "Keep current".',
    );
    expect(compareConfirmMessage(3)).toContain('3 emails go to the LLM');
  });

  it('words a ceiling as "up to"', () => {
    expect(compareConfirmMessage(1, true)).toContain('Up to 1 email goes to the LLM');
    expect(compareConfirmMessage(12, true)).toContain('Up to 12 emails go to the LLM');
  });

  it('posts the selection or the run once confirmed', () => {
    const backend = TestBed.inject(HttpTestingController);
    const selection = start({ suggestionIds: ['s-1', 's-2'] }, 2);
    expect(dialogOpen.mock.calls[0][1].data.title).toBe('Re-analyse 2 emails?');
    const req = backend.expectOne('/api/analysis/compare-runs');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ suggestionIds: ['s-1', 's-2'] });
    req.flush({ id: 'run-2', kind: 'compare' });
    expect(selection.runs).toEqual([{ id: 'run-2', kind: 'compare' }]);

    start({ runId: 'run-1' }, 20);
    expect(backend.expectOne('/api/analysis/compare-runs').request.body).toEqual({
      runId: 'run-1',
    });
    backend.verify();
  });

  it('posts nothing when the confirm is cancelled', () => {
    confirmed = false;
    const { runs } = start({ runId: 'run-1' }, 5);
    TestBed.inject(HttpTestingController).verify();
    expect(runs).toEqual([]);
  });

  it("a 409 shows the API's problem detail in a snackbar", () => {
    const { errors } = start({ runId: 'run-1' }, 5);
    TestBed.inject(HttpTestingController)
      .expectOne('/api/analysis/compare-runs')
      .flush(
        { title: 'Analysis running', detail: 'Wait for the current run to finish.' },
        { status: 409, statusText: 'Conflict' },
      );
    expect(errors).toHaveLength(1);
    expect(open).toHaveBeenCalledWith(
      'Analysis running: Wait for the current run to finish.',
      'Dismiss',
      expect.anything(),
    );
  });
});
