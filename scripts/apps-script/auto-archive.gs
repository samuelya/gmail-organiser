/**
 * Gmail Organiser: time-based auto-archive (Google Apps Script).
 *
 * Runs in Google's cloud on a daily trigger, so archiving works while the PC is off.
 * - Label rules: inbox threads carrying `label` whose last message is older than `days` are archived.
 * - Action done: inbox threads that have a user label but not `actionLabel`, a keep-in-inbox label
 *   or a label-rule label are archived (mail without any user label is never touched; rule labels
 *   follow only their `days`).
 * When in doubt the script archives less: a missing or unsearchable action/keep label skips the
 * action-done phase. It only archives (removes from the inbox); it never trashes, spams or deletes.
 *
 * Install: paste this file into a new Apps Script project, replace CONFIG with the block
 * generated on the portal's Settings page, run `runAutoArchive` once with `dryRun: true`
 * and check the log, then set `dryRun: false` and run `installDailyTrigger`.
 */

const CONFIG = {
  scriptVersion: 1,
  labelRules: [
    // { label: 'Example/Newsletters', days: 30 },
  ],
  actionLabel: 'Action/ToDo',
  actionDoneArchive: true,
  keepInInboxLabels: [],
  pageSize: 100,
  maxRuntimeSeconds: 280,
  dryRun: true,
};

// GmailApp.moveThreadsToArchive accepts at most 100 threads per call.
const ARCHIVE_BATCH_SIZE_ = 100;
// GmailApp.search returns at most 500 threads per call.
const MAX_PAGE_SIZE_ = 500;
const DEFAULT_MAX_RUNTIME_SECONDS_ = 280;
const TRIGGER_HOUR_ = 3;
const DAY_MS_ = 24 * 60 * 60 * 1000;
// Extra passes from the start when search still listed threads archived this run.
const MAX_RESCANS_ = 2;
// Letters, digits, `_`, `/`, `-` and spaces, not starting with `-`.
const SEARCHABLE_LABEL_ = /^[\p{L}\p{N}_/][\p{L}\p{N}_/ -]*$/u;

/** Trigger entry point: wires the real GmailApp, Logger and clock into the pure functions. */
function runAutoArchive() {
  const log = (message) => Logger.log(message);
  const now = () => Date.now();
  const startedAt = now();
  const seen = new Set(); // shared by both phases, so a thread is archived and counted once
  log(`Auto-archive v${CONFIG.scriptVersion} started${CONFIG.dryRun ? ' (dry run: nothing is changed)' : ''}.`);

  const byLabel = archiveByLabelRules_(CONFIG, now, GmailApp, log, startedAt, seen);
  let actionDone = { archived: 0, stopped: false };
  if (!byLabel.stopped) {
    actionDone = archiveActionDone_(CONFIG, now, GmailApp, log, startedAt, seen);
  }

  const verb = CONFIG.dryRun ? 'would archive' : 'archived';
  const stopped = byLabel.stopped || actionDone.stopped;
  log(`Done: ${verb} ${seen.size} thread(s)` +
    (stopped ? '; stopped at the runtime limit, the next run continues.' : '.'));
}

/** Creates one daily `runAutoArchive` trigger (03:00, script time zone), replacing existing ones. */
function installDailyTrigger() {
  removeTriggers();
  ScriptApp.newTrigger('runAutoArchive').timeBased().everyDays(1).atHour(TRIGGER_HOUR_).create();
  Logger.log(`Installed a daily runAutoArchive trigger at ${TRIGGER_HOUR_}:00.`);
}

/** Removes every `runAutoArchive` trigger of this project. */
function removeTriggers() {
  const triggers = ScriptApp.getProjectTriggers()
    .filter((trigger) => trigger.getHandlerFunction() === 'runAutoArchive');
  triggers.forEach((trigger) => ScriptApp.deleteTrigger(trigger));
  Logger.log(`Removed ${triggers.length} runAutoArchive trigger(s).`);
}

/**
 * Archives inbox threads per `{ label, days }` rule whose last message is older than `days`.
 * @return {{ archived: number, stopped: boolean }} archived = threads new to `seen` (in a dry run: would be archived)
 */
function archiveByLabelRules_(config, now, gmail, log, startedAt, seen = new Set()) {
  const run = { config, now, gmail, log, deadline: deadline_(config, startedAt), seen };
  let archived = 0;
  for (const rule of config.labelRules || []) {
    if (!isValidRule_(rule)) {
      log(`Warning: skipping invalid rule ${JSON.stringify(rule)}; expected { label: string, days: positive integer }.`);
      continue;
    }
    if (labelQueryName_(rule.label) === null) {
      log(`Warning: label "${rule.label}" has characters Gmail search can't match reliably; rule skipped.`);
      continue;
    }
    if (!gmail.getUserLabelByName(rule.label)) {
      log(`Warning: label "${rule.label}" does not exist in this account; rule skipped.`);
      continue;
    }
    const cutoff = startedAt - rule.days * DAY_MS_;
    const result = archiveQuery_(buildLabelRuleQuery_(rule), cutoff, run);
    archived += result.archived;
    if (result.stopped) return { archived, stopped: true };
  }
  return { archived, stopped: false };
}

/**
 * Archives inbox threads that have a user label but not the action label, a keep-in-inbox label
 * or a label-rule label. Skipped with a warning when the query can't be built safely.
 * @return {{ archived: number, stopped: boolean }}
 */
function archiveActionDone_(config, now, gmail, log, startedAt, seen = new Set()) {
  if (!config.actionDoneArchive) return { archived: 0, stopped: false };
  const query = buildActionDoneQuery_(config, gmail, log);
  if (query === null) return { archived: 0, stopped: false };
  return archiveQuery_(query, null, { config, now, gmail, log, deadline: deadline_(config, startedAt), seen });
}

/** `in:inbox older_than:<days>d label:<name>` */
function buildLabelRuleQuery_(rule) {
  return `in:inbox older_than:${rule.days}d label:${labelQueryName_(rule.label)}`;
}

/**
 * `in:inbox has:userlabels -label:<actionLabel>` plus `-label:<x>` per keep-in-inbox and label-rule label,
 * or null (with a warning) when the action label or a keep label is missing or unsearchable, or a rule
 * label is unsearchable: an exclusion that silently matches nothing would archive too much.
 */
function buildActionDoneQuery_(config, gmail, log) {
  const skip = (reason) => {
    log(`Warning: ${reason}; action-done archiving skipped.`);
    return null;
  };
  if (typeof config.actionLabel !== 'string' || config.actionLabel.trim() === '') {
    return skip('actionDoneArchive is on but actionLabel is empty');
  }
  const required = [config.actionLabel, ...(config.keepInInboxLabels || [])].filter(isNonEmpty_);
  const ruleLabels = (config.labelRules || []).map((rule) => rule && rule.label).filter(isNonEmpty_);
  const excluded = [];
  for (const name of required) {
    if (labelQueryName_(name) === null) return skip(`label "${name}" has characters Gmail search can't match reliably`);
    if (!gmail.getUserLabelByName(name)) return skip(`label "${name}" does not exist in this account`);
    excluded.push(name);
  }
  for (const name of ruleLabels) {
    if (labelQueryName_(name) === null) return skip(`rule label "${name}" has characters Gmail search can't match reliably`);
    // A rule label absent from the account is on no thread, so it needs no exclusion.
    if (gmail.getUserLabelByName(name)) excluded.push(name);
  }
  const terms = [...new Set(excluded.map(labelQueryName_))].map((name) => `-label:${name}`);
  return ['in:inbox', 'has:userlabels', ...terms].join(' ');
}

/**
 * Gmail's search form of a label name: spaces become `-`, nesting `/` is kept. Returns null for names
 * with other characters (quotes, parentheses, `:`, a leading `-`, …), whose search form isn't reliable.
 */
function labelQueryName_(name) {
  const trimmed = String(name).trim();
  return SEARCHABLE_LABEL_.test(trimmed) ? trimmed.replace(/\s+/g, '-') : null;
}

/**
 * Pages through `query` and archives the new matches in groups of ≤ 100. With a `cutoff`, a thread
 * whose last message is not older than it (a recent reply) is left alone.
 * Archived threads leave `in:inbox`, so a live run re-reads from the start after archiving and only
 * moves `start` past a page with nothing new. Search can still list just-archived threads (lagging
 * index), so a pass that met them is repeated from the start, at most MAX_RESCANS_ times, before the
 * query counts as done.
 */
function archiveQuery_(query, cutoff, run) {
  const { config, now, gmail, log, deadline, seen } = run;
  const pageSize = pageSize_(config);
  const tooRecent = new Set();
  let start = 0;
  let matched = 0;
  let staleInPass = false;
  let rescans = 0;
  log(`Query: ${query}`);
  for (;;) {
    if (now() >= deadline) {
      log(`Runtime limit reached after ${matched} match(es) for "${query}"; stopping between pages.`);
      return { archived: matched, stopped: true };
    }
    const page = gmail.search(query, start, pageSize);
    if (page.length === 0) {
      if (config.dryRun || !staleInPass || rescans >= MAX_RESCANS_) break;
      rescans += 1;
      staleInPass = false;
      start = 0;
      continue;
    }
    const fresh = [];
    for (const thread of page) {
      const id = thread.getId();
      if (seen.has(id)) {
        staleInPass = true;
      } else if (!tooRecent.has(id)) {
        if (cutoff !== null && thread.getLastMessageDate().getTime() >= cutoff) {
          tooRecent.add(id);
        } else {
          fresh.push(thread);
        }
      }
    }
    fresh.forEach((thread) => seen.add(thread.getId()));
    matched += fresh.length;
    if (config.dryRun) {
      log(`Dry run: page at ${start} has ${page.length} thread(s), ${fresh.length} new.`);
      start += page.length;
      continue;
    }
    for (let i = 0; i < fresh.length; i += ARCHIVE_BATCH_SIZE_) {
      gmail.moveThreadsToArchive(fresh.slice(i, i + ARCHIVE_BATCH_SIZE_));
    }
    if (fresh.length > 0) {
      start = 0;
      staleInPass = false;
    } else {
      start += page.length;
    }
  }
  const recent = tooRecent.size > 0 ? ` (${tooRecent.size} left: recent reply)` : '';
  log(`${config.dryRun ? 'Would archive' : 'Archived'} ${matched} thread(s) for "${query}"${recent}.`);
  return { archived: matched, stopped: false };
}

function isValidRule_(rule) {
  return !!rule && isNonEmpty_(rule.label) && Number.isInteger(rule.days) && rule.days > 0;
}

function isNonEmpty_(value) {
  return typeof value === 'string' && value.trim() !== '';
}

function pageSize_(config) {
  const size = Number(config.pageSize);
  return Number.isInteger(size) && size > 0 ? Math.min(size, MAX_PAGE_SIZE_) : ARCHIVE_BATCH_SIZE_;
}

function deadline_(config, startedAt) {
  const seconds = Number(config.maxRuntimeSeconds);
  const limit = Number.isFinite(seconds) && seconds > 0 ? seconds : DEFAULT_MAX_RUNTIME_SECONDS_;
  return startedAt + limit * 1000;
}
