/**
 * Gmail Organiser: time-based auto-archive (Google Apps Script).
 *
 * Runs in Google's cloud on a daily trigger, so archiving works while the PC is off.
 * - Label rules: inbox threads carrying `label` and older than `days` are archived.
 * - Action done: inbox threads that have a user label but not `actionLabel` are archived
 *   (mail without any user label is never touched).
 * The script only archives (removes from the inbox). It never trashes, spams or deletes.
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

/** Trigger entry point: wires the real GmailApp, Logger and clock into the pure functions. */
function runAutoArchive() {
  const log = (message) => Logger.log(message);
  const now = () => Date.now();
  const startedAt = now();
  log(`Auto-archive v${CONFIG.scriptVersion} started${CONFIG.dryRun ? ' (dry run: nothing is changed)' : ''}.`);

  const byLabel = archiveByLabelRules_(CONFIG, now, GmailApp, log, startedAt);
  let actionDone = { archived: 0, stopped: false };
  if (!byLabel.stopped) {
    actionDone = archiveActionDone_(CONFIG, GmailApp, log, now, startedAt);
  }

  const verb = CONFIG.dryRun ? 'would archive' : 'archived';
  const stopped = byLabel.stopped || actionDone.stopped;
  log(`Done: ${verb} ${byLabel.archived + actionDone.archived} thread(s)` +
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
 * Archives inbox threads per `{ label, days }` rule.
 * @return {{ archived: number, stopped: boolean }} archived = matched threads (in a dry run: would be archived)
 */
function archiveByLabelRules_(config, now, gmail, log, startedAt = now()) {
  const deadline = deadline_(config, startedAt);
  let archived = 0;
  for (const rule of config.labelRules || []) {
    if (!isValidRule_(rule)) {
      log(`Warning: skipping invalid rule ${JSON.stringify(rule)}; expected { label: string, days: positive integer }.`);
      continue;
    }
    if (!gmail.getUserLabelByName(rule.label)) {
      log(`Warning: label "${rule.label}" does not exist in this account; rule skipped.`);
      continue;
    }
    const result = archiveQuery_(buildLabelRuleQuery_(rule), config, now, deadline, gmail, log);
    archived += result.archived;
    if (result.stopped) return { archived, stopped: true };
  }
  return { archived, stopped: false };
}

/**
 * Archives inbox threads that have a user label but not the action label (nor a keep-in-inbox label).
 * @return {{ archived: number, stopped: boolean }}
 */
function archiveActionDone_(config, gmail, log, now = () => 0, startedAt = now()) {
  if (!config.actionDoneArchive) return { archived: 0, stopped: false };
  if (!config.actionLabel || typeof config.actionLabel !== 'string') {
    log('Warning: actionDoneArchive is on but actionLabel is empty; action-done archiving skipped.');
    return { archived: 0, stopped: false };
  }
  return archiveQuery_(buildActionDoneQuery_(config), config, now, deadline_(config, startedAt), gmail, log);
}

/** `in:inbox older_than:<days>d label:<name>` */
function buildLabelRuleQuery_(rule) {
  return `in:inbox older_than:${rule.days}d label:${labelQueryName_(rule.label)}`;
}

/** `in:inbox has:userlabels -label:<actionLabel>` plus `-label:<x>` per keep-in-inbox label. */
function buildActionDoneQuery_(config) {
  const excluded = [config.actionLabel, ...(config.keepInInboxLabels || [])]
    .filter((name) => typeof name === 'string' && name.trim() !== '');
  return ['in:inbox', 'has:userlabels', ...excluded.map((name) => `-label:${labelQueryName_(name)}`)].join(' ');
}

/** Gmail's search form of a label name: spaces become `-`, nesting `/` is kept. */
function labelQueryName_(name) {
  return String(name).trim().replace(/\s+/g, '-');
}

/**
 * Pages through `query` and archives the matches in groups of ≤ 100.
 * Archived threads leave `in:inbox`, so a live run re-reads from the start after archiving;
 * it only moves `start` on in a dry run, or past a page holding nothing new (lagging index).
 */
function archiveQuery_(query, config, now, deadline, gmail, log) {
  const pageSize = pageSize_(config);
  const seen = new Set();
  let start = 0;
  let matched = 0;
  log(`Query: ${query}`);
  for (;;) {
    if (now() >= deadline) {
      log(`Runtime limit reached after ${matched} match(es) for "${query}"; stopping between pages.`);
      return { archived: matched, stopped: true };
    }
    const page = gmail.search(query, start, pageSize);
    if (page.length === 0) break;
    const fresh = page.filter((thread) => !seen.has(thread.getId()));
    fresh.forEach((thread) => seen.add(thread.getId()));
    matched += fresh.length;
    if (config.dryRun) {
      log(`Dry run: page at ${start} has ${page.length} thread(s), ${fresh.length} new.`);
    } else {
      for (let i = 0; i < fresh.length; i += ARCHIVE_BATCH_SIZE_) {
        gmail.moveThreadsToArchive(fresh.slice(i, i + ARCHIVE_BATCH_SIZE_));
      }
    }
    start = config.dryRun || fresh.length === 0 ? start + page.length : 0;
  }
  log(`${config.dryRun ? 'Would archive' : 'Archived'} ${matched} thread(s) for "${query}".`);
  return { archived: matched, stopped: false };
}

function isValidRule_(rule) {
  return !!rule && typeof rule.label === 'string' && rule.label.trim() !== '' &&
    Number.isInteger(rule.days) && rule.days > 0;
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
