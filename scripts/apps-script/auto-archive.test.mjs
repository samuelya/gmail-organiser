// Loads auto-archive.gs unmodified through node:vm with a fake GmailApp. Run: node --test 'scripts/apps-script/*.test.mjs'
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('./auto-archive.gs', import.meta.url), 'utf8');

const NOW = Date.UTC(2026, 0, 15);
const DAY = 24 * 60 * 60 * 1000;

/**
 * In-memory GmailApp: understands exactly the search operators the script emits and throws on anything else.
 * A thread matches `older_than` through its oldest message (`ageDays`), as Gmail does; `lastAgeDays` is its
 * newest message. `label:` accepts only plain tokens (letters, digits, `_`, `/`, `-`) and matches a label
 * whose name equals the token once each `-` is read as a space or a `-`, case-insensitively.
 * `archiveLag` = number of searches that still list a thread after it was archived.
 */
class FakeGmailApp {
  constructor(threads, labels = [], { now = NOW, archiveLag = 0 } = {}) {
    this.threads = threads.map((t, i) => ({ id: `t${i}`, inbox: true, ageDays: 0, labels: [], ...t }));
    this.labels = new Set(labels);
    this.now = now;
    this.archiveLag = archiveLag;
    this.pending = [];
    this.queries = [];
    this.archiveCalls = [];
  }

  getUserLabelByName(name) {
    return this.labels.has(name) ? { getName: () => name } : null;
  }

  search(query, start, max) {
    this.pending = this.pending.filter((p) => (p.lag-- > 0 ? true : ((p.thread.inbox = false), false)));
    this.queries.push({ query, start, max });
    const predicates = query.split(' ').map((token) => this.#predicate(token));
    return this.threads
      .filter((thread) => predicates.every((matches) => matches(thread)))
      .slice(start, start + max)
      .map((thread) => ({
        getId: () => thread.id,
        getFirstMessageSubject: () => thread.subject ?? '',
        getLastMessageDate: () => new Date(this.now - (thread.lastAgeDays ?? thread.ageDays) * DAY),
        thread,
      }));
  }

  moveThreadsToArchive(threads) {
    assert.ok(threads.length <= 100, 'moveThreadsToArchive takes at most 100 threads');
    this.archiveCalls.push(threads.length);
    threads.forEach(({ thread }) => {
      if (this.archiveLag > 0) this.pending.push({ thread, lag: this.archiveLag });
      else thread.inbox = false;
    });
    return this;
  }

  inboxIds() {
    return this.threads.filter((t) => t.inbox && !this.pending.some((p) => p.thread === t)).map((t) => t.id);
  }

  #predicate(token) {
    let match;
    if (token === 'in:inbox') return (t) => t.inbox;
    if (token === 'has:userlabels') return (t) => t.labels.length > 0;
    if ((match = /^older_than:(\d+)d$/.exec(token))) return (t) => t.ageDays > Number(match[1]);
    if ((match = /^(-?)label:([A-Za-z0-9_/][A-Za-z0-9_/-]*)$/.exec(token))) {
      const [, negate, name] = match;
      const pattern = new RegExp(`^${name.split('-').map((part) => part.replace(/[/]/g, '\\/')).join('[ -]')}$`, 'i');
      const has = (t) => t.labels.some((label) => pattern.test(label));
      return negate ? (t) => !has(t) : has;
    }
    throw new Error(`FakeGmailApp: unsupported search token "${token}"`);
  }
}

function load(gmail = new FakeGmailApp([])) {
  const logs = [];
  const triggers = [];
  const scriptApp = {
    getProjectTriggers: () => [...triggers],
    deleteTrigger: (trigger) => triggers.splice(triggers.indexOf(trigger), 1),
    newTrigger: (handler) => {
      const spec = { handler };
      const builder = {
        timeBased: () => builder,
        everyDays: (days) => { spec.days = days; return builder; },
        atHour: (hour) => { spec.hour = hour; return builder; },
        create: () => { triggers.push({ getHandlerFunction: () => handler, spec }); },
      };
      return builder;
    },
  };
  const context = vm.createContext({ GmailApp: gmail, Logger: { log: (m) => logs.push(String(m)) }, ScriptApp: scriptApp, console });
  const api = vm.runInContext(`${source}
;({ CONFIG, runAutoArchive, installDailyTrigger, removeTriggers, archiveByLabelRules_, archiveActionDone_,
    buildLabelRuleQuery_, buildActionDoneQuery_, labelQueryName_ })`, context);
  return { ...api, gmail, logs, triggers };
}

const config = (overrides = {}) => ({
  scriptVersion: 1,
  labelRules: [],
  actionLabel: 'Action/ToDo',
  actionDoneArchive: true,
  keepInInboxLabels: [],
  pageSize: 100,
  maxRuntimeSeconds: 280,
  dryRun: false,
  ...overrides,
});

const fixedClock = () => NOW;
const quiet = () => {};

test('CONFIG ships with the frozen keys and safe defaults', () => {
  const { CONFIG } = load();
  assert.deepEqual(Object.keys(CONFIG), ['scriptVersion', 'labelRules', 'actionLabel', 'actionDoneArchive',
    'keepInInboxLabels', 'pageSize', 'maxRuntimeSeconds', 'dryRun']);
  assert.equal(CONFIG.dryRun, true);
  assert.equal(CONFIG.maxRuntimeSeconds, 280);
});

test('label rule query carries days and the normalised label name', () => {
  const { buildLabelRuleQuery_, labelQueryName_ } = load();
  assert.equal(buildLabelRuleQuery_({ label: 'Example/Read Later', days: 30 }),
    'in:inbox older_than:30d label:Example/Read-Later');
  assert.equal(labelQueryName_('  Example/Two  Words '), 'Example/Two-Words');
  assert.equal(labelQueryName_('Example/Nested/Child'), 'Example/Nested/Child');
});

test('label names with search metacharacters are unsearchable', () => {
  const { labelQueryName_ } = load();
  for (const name of ['Example/Keep (VIP)', 'Example/Bills "Q1"', 'Example/{x}', '-Example', 'Example:A', 'Example/A.B']) {
    assert.equal(labelQueryName_(name), null, name);
  }
});

test('label rule archives only old threads with the label, paging over more than pageSize', () => {
  const threads = [
    ...Array.from({ length: 250 }, () => ({ labels: ['Example/Newsletters'], ageDays: 40 })),
    { labels: ['Example/Newsletters'], ageDays: 10 },
    { labels: ['Example/Other'], ageDays: 40 },
    { labels: [], ageDays: 400 },
  ];
  const gmail = new FakeGmailApp(threads, ['Example/Newsletters']);
  const { archiveByLabelRules_ } = load(gmail);

  const result = archiveByLabelRules_(config({ labelRules: [{ label: 'Example/Newsletters', days: 30 }], pageSize: 100 }),
    fixedClock, gmail, quiet, NOW);

  assert.equal(result.archived, 250);
  assert.equal(result.stopped, false);
  assert.deepEqual(gmail.archiveCalls, [100, 100, 50]);
  assert.deepEqual(gmail.inboxIds(), ['t250', 't251', 't252']);
  assert.ok(gmail.queries.length > 1);
});

test('label rule leaves a thread with a recent reply in the inbox', () => {
  const gmail = new FakeGmailApp([
    { labels: ['Example/News'], ageDays: 40, lastAgeDays: 1 },
    { labels: ['Example/News'], ageDays: 40, lastAgeDays: 35 },
  ], ['Example/News']);
  const { archiveByLabelRules_ } = load(gmail);
  const logs = [];
  const result = archiveByLabelRules_(config({ labelRules: [{ label: 'Example/News', days: 30 }] }),
    fixedClock, gmail, (m) => logs.push(m), NOW);
  assert.equal(result.archived, 1);
  assert.deepEqual(gmail.inboxIds(), ['t0']);
  assert.ok(logs.some((m) => m.includes('1 left: recent reply')));
});

test('archives in groups of at most 100 when pageSize is larger', () => {
  const gmail = new FakeGmailApp(Array.from({ length: 230 }, () => ({ labels: ['Example/A'], ageDays: 5 })), ['Example/A']);
  const { archiveByLabelRules_ } = load(gmail);
  archiveByLabelRules_(config({ labelRules: [{ label: 'Example/A', days: 1 }], pageSize: 500 }), fixedClock, gmail, quiet, NOW);
  assert.deepEqual(gmail.archiveCalls, [100, 100, 30]);
  assert.deepEqual(gmail.inboxIds(), []);
});

test('missing or unsearchable rule label is skipped with a warning', () => {
  const gmail = new FakeGmailApp([{ labels: ['Example/Gone'], ageDays: 99 }], ['Example/Keep (VIP)']);
  const { archiveByLabelRules_ } = load(gmail);
  const logs = [];
  const rules = [{ label: 'Example/Gone', days: 1 }, { label: 'Example/Keep (VIP)', days: 1 }];
  const result = archiveByLabelRules_(config({ labelRules: rules }), fixedClock, gmail, (m) => logs.push(m), NOW);
  assert.equal(result.archived, 0);
  assert.equal(gmail.queries.length, 0);
  assert.ok(logs.some((m) => m.startsWith('Warning') && m.includes('Example/Gone')));
  assert.ok(logs.some((m) => m.startsWith('Warning') && m.includes('Example/Keep (VIP)')));
});

const actionLabels = ['Action/ToDo', 'Example/Keep Me', 'Example/VIP', 'Example/News'];

test('action done query excludes the action label, keep-in-inbox labels and rule labels', () => {
  const { buildActionDoneQuery_ } = load();
  const gmail = new FakeGmailApp([], actionLabels);
  const cfg = config({ keepInInboxLabels: ['Example/Keep Me', 'Example/VIP'],
    labelRules: [{ label: 'Example/News', days: 30 }, { label: 'Example/Absent', days: 5 }] });
  assert.equal(buildActionDoneQuery_(cfg, gmail, quiet),
    'in:inbox has:userlabels -label:Action/ToDo -label:Example/Keep-Me -label:Example/VIP -label:Example/News');
});

test('action done is skipped when the action label or a keep label is missing or unsearchable', () => {
  const cases = [
    [config({ actionLabel: 'Action/Renamed' }), 'Action/Renamed'],
    [config({ keepInInboxLabels: ['Example/Absent'] }), 'Example/Absent'],
    [config({ keepInInboxLabels: ['Example/Keep (VIP)'] }), 'Example/Keep (VIP)'],
    [config({ labelRules: [{ label: 'Example/Bills "Q1"', days: 3 }] }), 'Example/Bills "Q1"'],
    [config({ actionLabel: '' }), 'actionLabel is empty'],
  ];
  for (const [cfg, mention] of cases) {
    const gmail = new FakeGmailApp([{ labels: ['Example/Invoices'] }], ['Action/ToDo', 'Example/Keep (VIP)', 'Example/Bills "Q1"']);
    const { archiveActionDone_ } = load(gmail);
    const logs = [];
    assert.equal(archiveActionDone_(cfg, fixedClock, gmail, (m) => logs.push(m), NOW).archived, 0, mention);
    assert.equal(gmail.queries.length, 0, mention);
    assert.deepEqual(gmail.inboxIds(), ['t0'], mention);
    assert.ok(logs.some((m) => m.startsWith('Warning') && m.includes(mention) && m.includes('skipped')), mention);
  }
});

test('action done archives labelled threads, skipping the action label, keep labels and unlabelled mail', () => {
  const gmail = new FakeGmailApp([
    { labels: ['Example/Invoices'] },
    { labels: ['Example/Invoices', 'Action/ToDo'] },
    { labels: ['Example/Keep'] },
    { labels: [] },
    { labels: ['Example/Receipts'], inbox: false },
  ], ['Action/ToDo', 'Example/Keep']);
  const { archiveActionDone_ } = load(gmail);
  const result = archiveActionDone_(config({ keepInInboxLabels: ['Example/Keep'] }), fixedClock, gmail, quiet, NOW);
  assert.equal(result.archived, 1);
  assert.deepEqual(gmail.inboxIds(), ['t1', 't2', 't3']);
});

test('action done does nothing when switched off', () => {
  const gmail = new FakeGmailApp([{ labels: ['Example/Invoices'] }], ['Action/ToDo']);
  const { archiveActionDone_ } = load(gmail);
  assert.equal(archiveActionDone_(config({ actionDoneArchive: false }), fixedClock, gmail, quiet, NOW).archived, 0);
  assert.equal(gmail.queries.length, 0);
});

test('both phases together: rule labels follow only their days, the action label and plain mail stay', () => {
  const threads = [
    { labels: ['Example/News'], ageDays: 3 }, // t0 rule label, too young
    { labels: ['Example/News'], ageDays: 40 }, // t1 rule label, old: archived by the rule
    { labels: ['Example/News'], ageDays: 40, lastAgeDays: 2 }, // t2 old thread, recent reply
    { labels: ['Example/News', 'Example/Invoices'], ageDays: 3 }, // t3 rule label wins over action done
    { labels: ['Example/Invoices'], ageDays: 3 }, // t4 action done
    { labels: ['Example/Invoices', 'Action/ToDo'], ageDays: 50 }, // t5 still to do
    { labels: [], ageDays: 400 }, // t6 no user label
  ];
  for (const dryRun of [true, false]) {
    const gmail = new FakeGmailApp(threads, ['Example/News', 'Example/Invoices', 'Action/ToDo'], { now: Date.now() });
    const { CONFIG, runAutoArchive, logs } = load(gmail);
    CONFIG.labelRules = [{ label: 'Example/News', days: 30 }];
    CONFIG.dryRun = dryRun;
    runAutoArchive();
    assert.ok(gmail.queries.some((q) => q.query === 'in:inbox has:userlabels -label:Action/ToDo -label:Example/News'));
    assert.ok(logs.at(-1).startsWith(`Done: ${dryRun ? 'would archive' : 'archived'} 2 thread(s)`), logs.at(-1));
    assert.deepEqual(gmail.inboxIds(), dryRun ? ['t0', 't1', 't2', 't3', 't4', 't5', 't6'] : ['t0', 't2', 't3', 't5', 't6']);
  }
});

test('dry run total counts a thread matched by two rules once and never logs subjects', () => {
  const threads = Array.from({ length: 150 }, (_, i) => ({ labels: ['Example/Newsletters', 'Example/Promo'], ageDays: 60, subject: `Synthetic subject ${i}` }));
  const gmail = new FakeGmailApp(threads, ['Example/Newsletters', 'Example/Promo', 'Action/ToDo'], { now: Date.now() });
  const { CONFIG, runAutoArchive, logs } = load(gmail);
  CONFIG.labelRules = [{ label: 'Example/Newsletters', days: 30 }, { label: 'Example/Promo', days: 30 }];

  runAutoArchive();

  assert.deepEqual(gmail.archiveCalls, []);
  assert.equal(gmail.inboxIds().length, 150);
  assert.ok(logs.includes('Query: in:inbox older_than:30d label:Example/Newsletters'));
  assert.ok(logs.some((m) => m.includes('Would archive 150 thread(s)')));
  assert.ok(logs.at(-1).startsWith('Done: would archive 150 thread(s)'), logs.at(-1));
  assert.ok(!logs.some((m) => m.includes('Synthetic subject')));
});

test('runtime guard stops cleanly between pages and the next run continues', () => {
  const gmail = new FakeGmailApp(Array.from({ length: 300 }, () => ({ labels: ['Example/A'], ageDays: 9 })), ['Example/A']);
  const { archiveByLabelRules_ } = load(gmail);
  const cfg = config({ labelRules: [{ label: 'Example/A', days: 1 }], maxRuntimeSeconds: 180 });
  let clock = NOW;
  const now = () => { const t = clock; clock += 100_000; return t; }; // each check costs 100 s

  const first = archiveByLabelRules_(cfg, now, gmail, quiet, NOW);
  assert.equal(first.stopped, true);
  assert.equal(first.archived, 200);
  assert.deepEqual(gmail.archiveCalls, [100, 100]);

  clock = NOW;
  const second = archiveByLabelRules_(cfg, now, gmail, quiet, NOW);
  assert.equal(second.archived, 100);
  assert.deepEqual(gmail.inboxIds(), []);
});

test('a search index that never drops archived threads ends instead of looping', () => {
  const gmail = new FakeGmailApp(Array.from({ length: 120 }, () => ({ labels: ['Example/A'], ageDays: 9 })), ['Example/A']);
  gmail.moveThreadsToArchive = (threads) => { gmail.archiveCalls.push(threads.length); return gmail; };
  const { archiveByLabelRules_ } = load(gmail);
  const result = archiveByLabelRules_(config({ labelRules: [{ label: 'Example/A', days: 1 }] }), fixedClock, gmail, quiet, NOW);
  assert.equal(result.archived, 120);
  assert.equal(result.stopped, false);
  assert.deepEqual(gmail.archiveCalls, [100, 20]);
});

test('a lagging index that catches up mid-pass does not skip unarchived threads', () => {
  const gmail = new FakeGmailApp(Array.from({ length: 200 }, () => ({ labels: ['Example/A'], ageDays: 9 })), ['Example/A'],
    { archiveLag: 1 });
  const { archiveByLabelRules_ } = load(gmail);
  const result = archiveByLabelRules_(config({ labelRules: [{ label: 'Example/A', days: 1 }] }), fixedClock, gmail, quiet, NOW);
  assert.equal(result.archived, 200);
  assert.deepEqual(gmail.inboxIds(), []);
});

test('installDailyTrigger replaces existing triggers instead of doubling up', () => {
  const { installDailyTrigger, removeTriggers, triggers } = load();
  installDailyTrigger();
  installDailyTrigger();
  assert.equal(triggers.length, 1);
  assert.deepEqual(triggers[0].spec, { handler: 'runAutoArchive', days: 1, hour: 3 });
  removeTriggers();
  assert.equal(triggers.length, 0);
});

test('source never trashes, spams or deletes', () => {
  for (const forbidden of ['moveToTrash', 'moveThreadsToTrash', 'moveToSpam', 'deleteThread', 'deleteMessage']) {
    assert.ok(!source.includes(forbidden), `source must not contain ${forbidden}`);
  }
});
