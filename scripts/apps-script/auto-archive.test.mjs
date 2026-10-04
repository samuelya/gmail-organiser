// Loads auto-archive.gs unmodified through node:vm with a fake GmailApp. Run: node --test scripts/apps-script
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('./auto-archive.gs', import.meta.url), 'utf8');

const searchName = (name) => name.trim().replace(/\s+/g, '-').toLowerCase();

/** In-memory GmailApp: understands exactly the search operators the script emits and throws on anything else. */
class FakeGmailApp {
  constructor(threads, labels = []) {
    this.threads = threads.map((t, i) => ({ id: `t${i}`, inbox: true, ageDays: 0, labels: [], ...t }));
    this.labels = new Set(labels);
    this.queries = [];
    this.archiveCalls = [];
  }

  getUserLabelByName(name) {
    return this.labels.has(name) ? { getName: () => name } : null;
  }

  search(query, start, max) {
    this.queries.push({ query, start, max });
    const predicates = query.split(' ').map((token) => this.#predicate(token));
    return this.threads
      .filter((thread) => predicates.every((matches) => matches(thread)))
      .slice(start, start + max)
      .map((thread) => ({ getId: () => thread.id, getFirstMessageSubject: () => thread.subject ?? '', thread }));
  }

  moveThreadsToArchive(threads) {
    assert.ok(threads.length <= 100, 'moveThreadsToArchive takes at most 100 threads');
    this.archiveCalls.push(threads.length);
    threads.forEach(({ thread }) => { thread.inbox = false; });
    return this;
  }

  inboxIds() {
    return this.threads.filter((t) => t.inbox).map((t) => t.id);
  }

  #predicate(token) {
    let match;
    if (token === 'in:inbox') return (t) => t.inbox;
    if (token === 'has:userlabels') return (t) => t.labels.length > 0;
    if ((match = /^older_than:(\d+)d$/.exec(token))) return (t) => t.ageDays > Number(match[1]);
    if ((match = /^(-?)label:(\S+)$/.exec(token))) {
      const [, negate, name] = match;
      const has = (t) => t.labels.some((label) => searchName(label) === name.toLowerCase());
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

const noClock = () => 0;

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
    noClock, gmail, () => {});

  assert.equal(result.archived, 250);
  assert.equal(result.stopped, false);
  assert.deepEqual(gmail.archiveCalls, [100, 100, 50]);
  assert.deepEqual(gmail.inboxIds(), ['t250', 't251', 't252']);
  assert.ok(gmail.queries.length > 1);
});

test('archives in groups of at most 100 when pageSize is larger', () => {
  const gmail = new FakeGmailApp(Array.from({ length: 230 }, () => ({ labels: ['Example/A'], ageDays: 5 })), ['Example/A']);
  const { archiveByLabelRules_ } = load(gmail);
  archiveByLabelRules_(config({ labelRules: [{ label: 'Example/A', days: 1 }], pageSize: 500 }), noClock, gmail, () => {});
  assert.deepEqual(gmail.archiveCalls, [100, 100, 30]);
  assert.deepEqual(gmail.inboxIds(), []);
});

test('missing label is skipped with a warning', () => {
  const gmail = new FakeGmailApp([{ labels: ['Example/Gone'], ageDays: 99 }]);
  const { archiveByLabelRules_ } = load(gmail);
  const logs = [];
  const result = archiveByLabelRules_(config({ labelRules: [{ label: 'Example/Gone', days: 1 }] }), noClock, gmail, (m) => logs.push(m));
  assert.equal(result.archived, 0);
  assert.equal(gmail.queries.length, 0);
  assert.ok(logs.some((m) => m.startsWith('Warning') && m.includes('Example/Gone')));
});

test('action done query excludes the action label and keep-in-inbox labels', () => {
  const { buildActionDoneQuery_ } = load();
  assert.equal(buildActionDoneQuery_(config({ keepInInboxLabels: ['Example/Keep Me', 'Example/VIP'] })),
    'in:inbox has:userlabels -label:Action/ToDo -label:Example/Keep-Me -label:Example/VIP');
});

test('action done archives labelled threads, skipping the action label, keep labels and unlabelled mail', () => {
  const gmail = new FakeGmailApp([
    { labels: ['Example/Invoices'] },
    { labels: ['Example/Invoices', 'Action/ToDo'] },
    { labels: ['Example/Keep'] },
    { labels: [] },
    { labels: ['Example/Receipts'], inbox: false },
  ]);
  const { archiveActionDone_ } = load(gmail);
  const result = archiveActionDone_(config({ keepInInboxLabels: ['Example/Keep'] }), gmail, () => {});
  assert.equal(result.archived, 1);
  assert.deepEqual(gmail.inboxIds(), ['t1', 't2', 't3']);
});

test('action done does nothing when switched off', () => {
  const gmail = new FakeGmailApp([{ labels: ['Example/Invoices'] }]);
  const { archiveActionDone_ } = load(gmail);
  assert.equal(archiveActionDone_(config({ actionDoneArchive: false }), gmail, () => {}).archived, 0);
  assert.equal(gmail.queries.length, 0);
});

test('dry run logs queries and counts, archives nothing and never logs subjects', () => {
  const threads = Array.from({ length: 150 }, (_, i) => ({ labels: ['Example/Newsletters'], ageDays: 60, subject: `Synthetic subject ${i}` }));
  const gmail = new FakeGmailApp(threads, ['Example/Newsletters']);
  const { archiveByLabelRules_, archiveActionDone_ } = load(gmail);
  const logs = [];
  const dry = config({ labelRules: [{ label: 'Example/Newsletters', days: 30 }], dryRun: true });

  assert.equal(archiveByLabelRules_(dry, noClock, gmail, (m) => logs.push(m)).archived, 150);
  assert.equal(archiveActionDone_(dry, gmail, (m) => logs.push(m)).archived, 150);

  assert.deepEqual(gmail.archiveCalls, []);
  assert.equal(gmail.inboxIds().length, 150);
  assert.ok(logs.includes('Query: in:inbox older_than:30d label:Example/Newsletters'));
  assert.ok(logs.some((m) => m.includes('Would archive 150 thread(s)')));
  assert.ok(!logs.some((m) => m.includes('Synthetic subject')));
});

test('runtime guard stops cleanly between pages and the next run continues', () => {
  const gmail = new FakeGmailApp(Array.from({ length: 300 }, () => ({ labels: ['Example/A'], ageDays: 9 })), ['Example/A']);
  const { archiveByLabelRules_ } = load(gmail);
  const cfg = config({ labelRules: [{ label: 'Example/A', days: 1 }], maxRuntimeSeconds: 280 });
  let clock = 0;
  const now = () => { const t = clock; clock += 100_000; return t; }; // each check costs 100 s

  const first = archiveByLabelRules_(cfg, now, gmail, () => {});
  assert.equal(first.stopped, true);
  assert.equal(first.archived, 200);
  assert.deepEqual(gmail.archiveCalls, [100, 100]);

  clock = 0;
  const second = archiveByLabelRules_(cfg, now, gmail, () => {});
  assert.equal(second.archived, 100);
  assert.deepEqual(gmail.inboxIds(), []);
});

test('a page that archives nothing advances instead of looping', () => {
  const gmail = new FakeGmailApp(Array.from({ length: 120 }, () => ({ labels: ['Example/A'], ageDays: 9 })), ['Example/A']);
  gmail.moveThreadsToArchive = (threads) => { gmail.archiveCalls.push(threads.length); return gmail; }; // lagging index
  const { archiveByLabelRules_ } = load(gmail);
  const result = archiveByLabelRules_(config({ labelRules: [{ label: 'Example/A', days: 1 }] }), noClock, gmail, () => {});
  assert.equal(result.archived, 120);
  assert.equal(result.stopped, false);
});

test('runAutoArchive wires GmailApp and Logger and runs both phases', () => {
  const gmail = new FakeGmailApp([{ labels: ['Example/Invoices'] }]);
  const { runAutoArchive, logs } = load(gmail);
  runAutoArchive();
  assert.ok(gmail.queries.some((q) => q.query === 'in:inbox has:userlabels -label:Action/ToDo'));
  assert.deepEqual(gmail.archiveCalls, []); // shipped CONFIG is a dry run
  assert.ok(logs.at(-1).startsWith('Done: would archive 1 thread(s)'));
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
