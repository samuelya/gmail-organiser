import {
  fetchJobsKey,
  fetchProgressKey,
  fetchView,
  humanise,
  jobTypeLabel,
  resyncDisabled,
} from './fetch.models';
import { job, status } from './fetch.testing';

describe('fetchView', () => {
  it.each([
    ['not_started', 'Not started', 'Start fetch'],
    ['inbox', 'Inbox', 'Start fetch'],
    ['all_mail', 'All mail', 'Start fetch'],
    ['completed', 'Completed', 'Fetch new mail'],
  ] as const)('%s with no job: phase %s, Start labelled %s', (phase, label, start) => {
    const v = fetchView(status({ mailboxPhase: phase }), undefined);
    expect(v.phase).toBe(label);
    expect(v.startLabel).toBe(start);
    expect(v.controls).toEqual({ pause: false, resume: false, cancel: false });
  });

  it.each([
    ['queued', { pause: true, resume: false, cancel: true }],
    ['running', { pause: true, resume: false, cancel: true }],
    ['paused', { pause: false, resume: true, cancel: true }],
  ] as const)('an active %s job hides Start and allows %o', (jobStatus, controls) => {
    const v = fetchView(status({ mailboxPhase: 'inbox', activeJob: job(jobStatus) }), undefined);
    expect(v.startLabel).toBeNull();
    expect(v.controls).toEqual(controls);
  });

  it('an unknown phase and job type are humanised, not dropped', () => {
    const active = job('running', { type: 'incremental_fetch' });
    const v = fetchView(status({ mailboxPhase: 'reconcile', activeJob: active }), undefined);
    expect(v.phase).toBe('Reconcile');
    expect(v.job!.type).toBe('incremental_fetch');
    expect(humanise('incremental_fetch')).toBe('Incremental fetch');
  });

  it('a failed job offers Resume fetch through Start and shows its error', () => {
    const failed = job('failed', { error: 'Gmail quota exceeded' });
    const v = fetchView(status({ mailboxPhase: 'all_mail', failedJob: failed }), undefined);
    expect(v.startLabel).toBe('Resume fetch');
    expect(v.error).toBe('Gmail quota exceeded');
  });

  it('shows whichever job the status names, using the newer hub copy', () => {
    const active = job('running', { type: 'incremental_fetch', version: 3 });
    const live = job('paused', { type: 'incremental_fetch', version: 4 });
    expect(fetchView(status({ activeJob: active }), live).job).toBe(live);
    expect(fetchView(status({ activeJob: live }), active).job).toBe(live);
  });

  it('an account mismatch disables Start and Resume', () => {
    const v = fetchView(status({ accountMismatch: true, activeJob: job('paused') }), undefined);
    expect(v.controls.resume).toBe(false);
    expect(fetchView(status({ accountMismatch: true }), undefined).startDisabled).toBe(true);
  });

  it('fetchJobsKey ignores progress and other queues', () => {
    const a = fetchJobsKey([job('running'), job('running', { id: 'x', queue: 'analysis' })]);
    const b = fetchJobsKey([job('running', { progress: { done: 99, total: 100, message: null } })]);
    expect(a).toBe(b);
    expect(fetchJobsKey([job('paused')])).not.toBe(a);
    const nextStep = { done: 0, total: 100, message: 'Next step' };
    expect(fetchJobsKey([job('running', { progress: nextStep })])).not.toBe(a);
  });

  it('fetchProgressKey changes on a sender fetch tick, not on other queues', () => {
    const sender = (done: number) =>
      job('running', {
        id: 's',
        type: 'sender_fetch',
        progress: { done, total: 9, message: null },
      });
    expect(fetchProgressKey([sender(1)])).not.toBe(fetchProgressKey([sender(2)]));
    expect(fetchProgressKey([job('running', { queue: 'analysis' })])).toBe('');
  });
});

describe('jobTypeLabel', () => {
  it('names label_resync and humanises the rest', () => {
    expect(jobTypeLabel('label_resync')).toBe('Resync labels');
    expect(jobTypeLabel('mailbox_fetch')).toBe('Mailbox fetch');
  });
});

describe('resyncDisabled', () => {
  it('is enabled when connected with no fetch-queue job active', () => {
    const others = [job('completed'), job('running', { id: 'a', queue: 'analysis' })];
    expect(resyncDisabled(status(), others, true)).toBe(false);
    expect(resyncDisabled(status(), [], null)).toBe(false);
  });

  it('is disabled before Gmail is connected and on an account mismatch', () => {
    expect(resyncDisabled(status(), [], false)).toBe(true);
    expect(resyncDisabled(status({ accountMismatch: true }), [], true)).toBe(true);
  });

  it.each(['queued', 'running', 'paused'] as const)(
    'is disabled while a fetch-queue job is %s',
    (jobStatus) => {
      const resync = job(jobStatus, { type: 'label_resync' });
      expect(resyncDisabled(status(), [resync], true)).toBe(true);
      expect(resyncDisabled(status({ activeJob: job(jobStatus) }), [], true)).toBe(true);
    },
  );
});
