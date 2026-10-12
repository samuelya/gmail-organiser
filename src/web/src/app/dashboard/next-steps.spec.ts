import { AnalysisSummaryDto } from '../analyse/analysis.models';
import { job, status } from './fetch.testing';
import { MAX_NEXT_STEPS, nextSteps, NextStepsInput } from './next-steps';

const empty: AnalysisSummaryDto = {
  notAnalysed: 0,
  analysed: 0,
  approved: 0,
  rejected: 0,
  applied: 0,
  actionCount: 0,
  toBeDeletedCount: 0,
  totalLlmCalls: 0,
  totalMessagesCovered: 0,
  savedPercent: 0,
  labelledNotAnalysed: 0,
  alternatives: 0,
  policiesProposed: 0,
  promptTokens: 0,
  completionTokens: 0,
  llmSeconds: 0,
  triageCalls: 0,
  escalatedCalls: 0,
  packedMessages: 0,
  packRetries: 0,
};

const done = status({ mailboxPhase: 'completed' });
const input = (over: Partial<NextStepsInput> = {}): NextStepsInput => ({
  gmailConnected: true,
  status: done,
  summary: empty,
  ...over,
});
const ids = (over: Partial<NextStepsInput> = {}) => nextSteps(input(over)).map((s) => s.id);

describe('nextSteps', () => {
  it('asks to connect Gmail first, linking to Settings, and leaves out the fetch step', () => {
    const steps = nextSteps(input({ gmailConnected: false, status: status() }));
    expect(steps.map((s) => s.id)).toEqual(['connect']);
    expect(steps[0]).toMatchObject({
      icon: 'link_off',
      text: 'Connect Gmail to start.',
      link: '/settings',
    });
  });

  it('adds no step for an account mismatch, nor the fetch step or "caught up"', () => {
    expect(ids({ status: status({ accountMismatch: true }) })).toEqual([]);
    expect(
      ids({ status: status({ accountMismatch: true }), summary: { ...empty, analysed: 2 } }),
    ).toEqual(['review']);
  });

  it('asks to fetch when the mailbox was never fetched and no fetch runs, without a link', () => {
    const steps = nextSteps(input({ status: status() }));
    expect(steps).toEqual([
      {
        id: 'fetch',
        icon: 'download',
        text: 'Fetch your mailbox: use Start fetch below.',
        link: null,
      },
    ]);
    expect(ids({ status: status({ activeJob: job('running') }) })).toEqual([]);
  });

  it('links pending suggestions to Review with the status filter', () => {
    const [step] = nextSteps(input({ summary: { ...empty, analysed: 37, notAnalysed: 5 } }));
    expect(step).toEqual({
      id: 'review',
      icon: 'fact_check',
      text: '37 suggestions waiting for review.',
      link: '/review',
      queryParams: { status: 'pending' },
      linkLabel: 'Review',
    });
  });

  it('links approved suggestions to Review filtered by approved', () => {
    const [step] = nextSteps(input({ summary: { ...empty, approved: 4 } }));
    expect(step).toMatchObject({
      id: 'apply',
      icon: 'done_all',
      text: '4 approved suggestions not applied to Gmail yet.',
      queryParams: { status: 'approved' },
      linkLabel: 'Apply',
    });
  });

  it('links marked messages to Clean-up', () => {
    const [step] = nextSteps(input({ summary: { ...empty, toBeDeletedCount: 9 } }));
    expect(step).toMatchObject({
      id: 'clean-up',
      icon: 'delete',
      text: '9 messages marked for deletion.',
      link: '/clean-up',
      linkLabel: 'Clean-up',
    });
  });

  it('asks to analyse only when nothing waits for review', () => {
    const [step] = nextSteps(input({ summary: { ...empty, notAnalysed: 1200 } }));
    expect(step).toMatchObject({
      id: 'analyse',
      icon: 'psychology',
      text: `${(1200).toLocaleString()} messages not analysed yet.`,
      link: '/analyse',
    });
    expect(ids({ summary: { ...empty, notAnalysed: 1200, analysed: 1 } })).toEqual(['review']);
  });

  it('uses the singular for one', () => {
    const one = { ...empty, analysed: 1, approved: 1, toBeDeletedCount: 1 };
    expect(nextSteps(input({ summary: one })).map((s) => s.text)).toEqual([
      '1 suggestion waiting for review.',
      '1 approved suggestion not applied to Gmail yet.',
      '1 message marked for deletion.',
    ]);
    expect(nextSteps(input({ summary: { ...empty, notAnalysed: 1 } }))[0].text).toBe(
      '1 message not analysed yet.',
    );
  });

  it('keeps the first three in priority order', () => {
    const all = { ...empty, analysed: 3, approved: 2, toBeDeletedCount: 1 };
    expect(ids({ gmailConnected: false, status: status(), summary: all })).toEqual([
      'connect',
      'review',
      'apply',
    ]);
    expect(
      ids({
        status: status(),
        summary: { ...empty, approved: 2, toBeDeletedCount: 1, notAnalysed: 5 },
      }),
    ).toHaveLength(MAX_NEXT_STEPS);
  });

  it("says you're all caught up when nothing matches", () => {
    expect(nextSteps(input())).toEqual([
      { id: 'caught-up', icon: 'task_alt', text: "You're all caught up.", link: null },
    ]);
  });

  it('computes only what it can when an input is missing, never "caught up"', () => {
    expect(ids({ status: null })).toEqual([]);
    expect(ids({ summary: null })).toEqual([]);
    expect(ids({ status: null, summary: { ...empty, approved: 1 } })).toEqual(['apply']);
    expect(ids({ status: status(), summary: null })).toEqual(['fetch']);
    expect(ids({ gmailConnected: null, status: null, summary: null })).toEqual([]);
  });
});
