# Apps Script setup (time-based archiving)

`scripts/apps-script/auto-archive.gs` archives inbox mail on a daily schedule. It runs in Google's cloud under your own
Google account, so it keeps working while your PC and the portal are off. The portal only generates its settings; it
never runs or deploys the script for you.

What it archives:

- **Label rules:** inbox threads carrying a rule's label (for example `Example/Newsletters`, 30 days) whose **last**
  message is older than the rule's days. A thread with a recent reply stays.
- **Action done:** inbox threads that have a user label but no longer carry your action label (set on the Settings
  page). Mail without any user label is never touched. Threads with a keep-in-inbox label or a label-rule label are left
  out; rule labels follow only their days.

When in doubt it archives less, never more: a rule whose label doesn't exist in the account or can't be searched is
skipped, and the whole action-done step is skipped (with a warning in the log) when the action label or a keep-in-inbox
label is missing or can't be searched. Label names Gmail search matches reliably contain only letters, digits, `_`, `/`,
`-` and spaces, and don't start with `-`; the Settings page rejects other names.

The script **only archives** (removes mail from the inbox). It never trashes, marks as spam, deletes, or changes labels.

## 1. Create the project

1. Open [script.google.com](https://script.google.com/) signed in with the **Gmail account you organise** (check the
   avatar top right if you use several Google accounts).
2. Click **New project** and rename it (click "Untitled project"), for example `Gmail auto-archive`.
3. Optional: under **Project Settings** (gear icon), check the **time zone**. The daily trigger runs between 03:00 and
   04:00 in this time zone.

No Google Cloud project is needed; this is separate from the portal's OAuth client.

## 2. Paste the script and your CONFIG

1. In the editor, delete the contents of `Code.gs` and paste the whole of `scripts/apps-script/auto-archive.gs`.
2. In the portal, open **Settings → Apps Script**, set up your archive rules and keep-in-inbox labels, leave **dry run**
   on, save, and **Copy** the generated CONFIG block.
3. In the editor, replace the script's `const CONFIG = { ... };` block (near the top) with the copied block. It looks
   like this:

   ```js
   const CONFIG = {
     scriptVersion: 1,
     labelRules: [
       { label: "Example/Newsletters", days: 30 },
     ],
     actionLabel: "Example/ToDo",
     actionDoneArchive: true,
     keepInInboxLabels: [],
     pageSize: 100,
     maxRuntimeSeconds: 280,
     dryRun: true,
   };
   ```

4. Save (**Ctrl+S** / **Cmd+S**).

Edit the settings in the portal rather than in the script, so the next copy-paste doesn't lose your changes.

## 3. Authorise and do a dry run

1. In the toolbar's function list choose `runAutoArchive` and click **Run**.
2. Google shows its own consent screen: **Review permissions**, pick the same account, and allow. Because the project
   is yours and unverified, Google may first show "Google hasn't verified this app": click **Advanced**, then
   **Go to \<project name\> (unsafe)**. Google's wording covers broad Gmail access because that is the permission
   Apps Script's Gmail service asks for; the script only searches, reads labels and archives.
3. The **Execution log** opens below the editor. With `dryRun: true` nothing changes; the log lists each Gmail search
   and how many threads it would archive, for example:

   ```text
   Auto-archive v1 started (dry run: nothing is changed).
   Query: in:inbox older_than:30d label:Example/Newsletters
   Dry run: page at 0 has 42 thread(s), 42 new.
   Would archive 42 thread(s) for "in:inbox older_than:30d label:Example/Newsletters".
   Query: in:inbox has:userlabels -label:Example/ToDo -label:Example/Newsletters
   Would archive 7 thread(s) for "in:inbox has:userlabels -label:Example/ToDo -label:Example/Newsletters" (2 left: recent reply).
   Done: would archive 49 thread(s).
   ```

   Lines starting with `Warning:` name a rule or step that was skipped and why (missing label, unsearchable name).
   To check a count, paste the logged query into Gmail's search box.

## 4. Switch it on

1. In the portal, turn **dry run** off on **Settings → Apps Script**, save, and copy the CONFIG block again.
2. Replace the CONFIG block in the script with it (it now says `dryRun: false`) and save.
3. Choose `installDailyTrigger` in the function list and click **Run**. The log says
   `Installed a daily runAutoArchive trigger at 3:00.` Running it again replaces the trigger; it never adds a second.

The first live run happens at the next trigger time; to archive straight away, run `runAutoArchive` once by hand.
Trigger runs and their logs are under **Executions** (left sidebar). Google emails you if a run fails.

## Runtime, re-runs and quotas

- Each run stops between pages once `maxRuntimeSeconds` (280 s) has passed, under Apps Script's 6-minute limit per
  execution, and logs `stopped at the runtime limit, the next run continues.` Archived mail has left the inbox, so the
  next run simply picks up what is left. Running it twice, or by hand between triggers, is safe.
- Apps Script has daily quotas per account (consumer accounts allow a limited number of Gmail read/write calls and total
  trigger runtime per day); searching 100 threads per page and archiving in groups of up to 100 keeps a daily run well
  under them.

## Alongside the portal

The script can run alongside the portal's `AutoArchiveOnActionDone` setting. Both archive the same mail (labelled inbox
mail whose action label was removed); whichever runs second finds nothing to do. The portal does it immediately during
its incremental fetch while it is running; the script covers the days it isn't.

## Update the script later

When the repository's `auto-archive.gs` changes:

1. Copy your current CONFIG block out of the editor (or copy a fresh one from **Settings → Apps Script**).
2. Replace the editor's contents with the new `auto-archive.gs`, then put your CONFIG block back in place of the
   template's.
3. Save. The daily trigger keeps working (it calls `runAutoArchive` by name); run `runAutoArchive` once to check the
   log. If Google asks for permissions again, allow as in step 3.

If the portal's CONFIG block shows a different `scriptVersion` than the script's template, update both together.

## Stop it

Choose `removeTriggers` in the function list and click **Run**; the log says how many triggers it removed. The script
then only runs when you run it by hand. To remove it entirely, delete the project on
[script.google.com](https://script.google.com/) and, optionally, remove its access under your Google Account's
**Security → Third-party apps & services**.
