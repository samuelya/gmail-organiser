import { AnalysisRunDto } from '../analyse/analysis.models';
import { RUN_COST_RUNS, durationText, runCostSummary, runCostTable, runCosts } from './run-cost';

let nextId = 0;
const run = (over: Partial<AnalysisRunDto>) =>
  ({
    id: `r${nextId++}`,
    kind: 'analyse',
    scope: 'sender',
    senderAddress: 'news@example.com',
    requestedCount: 10,
    status: 'completed',
    messagesLlm: 2,
    messagesDerived: 5,
    messagesFromMemory: 3,
    llmSeconds: 30,
    savedPercent: 0.8,
    finishedAt: '2026-03-04T10:15:00Z',
    ...over,
  }) as AnalysisRunDto;

describe('runCosts', () => {
  it('drops compare and unfinished runs and orders the rest oldest first', () => {
    const rows = runCosts([
      run({ id: 'new', finishedAt: '2026-03-05T09:00:00Z' }),
      run({ id: 'compare', kind: 'compare', finishedAt: '2026-03-05T08:00:00Z' }),
      run({ id: 'running', status: 'running', finishedAt: null }),
      run({ id: 'old', finishedAt: '2026-03-04T09:00:00Z', status: 'failed' }),
    ]);
    expect(rows.map((r) => r.id)).toEqual(['old', 'new']);
    expect(rows[1]).toEqual({
      id: 'new',
      finishedAt: '2026-03-05T09:00:00Z',
      target: 'Sender: news@example.com',
      llm: 2,
      derived: 5,
      memory: 3,
      llmSeconds: 30,
      savedPercent: 0.8,
    });
  });

  it('keeps the newest twenty', () => {
    const runs = Array.from({ length: 25 }, (_, i) =>
      run({ id: `d${i}`, finishedAt: new Date(Date.UTC(2026, 0, i + 1)).toISOString() }),
    ).reverse();
    const rows = runCosts(runs);
    expect(rows).toHaveLength(RUN_COST_RUNS);
    expect(rows[0].id).toBe('d5');
    expect(rows.at(-1)?.id).toBe('d24');
  });
});

describe('durationText', () => {
  it.each([
    [0, '0 s'],
    [44.6, '45 s'],
    [60, '1 min'],
    [840, '14 min'],
    [3600, '1 h'],
    [7500, '2 h 5 min'],
  ])('%s seconds reads %s', (seconds, text) => {
    expect(durationText(seconds)).toBe(text);
  });
});

describe('runCostSummary', () => {
  it('states the share no model call was needed for and the model time', () => {
    const rows = runCosts([
      run({ messagesLlm: 19, messagesDerived: 20, messagesFromMemory: 11, llmSeconds: 600 }),
      run({ messagesLlm: 0, messagesDerived: 0, messagesFromMemory: 0, llmSeconds: 240 }),
    ]);
    expect(runCostSummary(rows, 'en-US')).toBe(
      'Over the last 2 runs, 62% of messages needed no model call; model time 14 min.',
    );
  });

  it('reads well for one run, no messages and no runs', () => {
    expect(runCostSummary(runCosts([run({})]), 'en-US')).toBe(
      'In the last run, 80% of messages needed no model call; model time 30 s.',
    );
    const empty = run({ messagesLlm: 0, messagesDerived: 0, messagesFromMemory: 0, llmSeconds: 0 });
    expect(runCostSummary(runCosts([empty, empty]), 'en-US')).toBe(
      'Over the last 2 runs, no messages were covered; model time 0 s.',
    );
    expect(runCostSummary([], 'en-US')).toBe('No analysis runs yet.');
  });
});

describe('runCostTable', () => {
  it('lists each run with its target, counts and model seconds', () => {
    const table = runCostTable(
      runCosts([run({ scope: 'inbox', senderAddress: null, llmSeconds: 12.345 })]),
      'en-US',
    );
    expect(table.columns).toEqual([
      'Finished',
      'Target',
      'By model',
      'Derived',
      'From memory',
      'Model seconds',
    ]);
    expect(table.rows[0].slice(2)).toEqual([2, 5, 3, 12.3]);
    expect(table.rows[0][0]).toMatch(/^\d{1,2} Mar \d{2}:\d{2}$/);
    expect(typeof table.rows[0][1]).toBe('string');
  });
});
