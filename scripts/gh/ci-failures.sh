#!/usr/bin/env bash
# Usage: scripts/gh/ci-failures.sh <pr-number> [--post] [--run <run-id>]
# Prints the failures of the PR head's latest failed CI run as a short Markdown list: failed step,
# then each failing test (name, file:line, first error line and the source line it points at), or
# the lint/type/build errors. Capped at 60 lines. With --post it also posts that list as a PR
# comment, which is the whole hand-off for a red-CI review-fix round: no tester run is needed just
# to read CI (#207 review 4: #218 spent 4 tester runs, #220 3, on reading red CI).
# A step that hit its timeout-minutes, or a run cancelled by hand, is reported as "timed out" or
# "cancelled" with the last spec file it logged (#207 review 5: #282's unit step hung 30 min).
# Exit 0: failures printed. Exit 3: no failed run on the PR head (green or still running: use
# scripts/gh/wait-ci.sh).
set -euo pipefail
source "$(dirname "$0")/_lib.sh"

pr="${1:?PR number required}"
require_number "$pr"
shift
post=0; run=
while (( $# )); do
  case "$1" in
    --post) post=1; shift ;;
    --run) require_number "${2:?--run needs a run id}"; run="$2"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 64 ;;
  esac
done
repo="$OWNER/$REPO"

if [[ -n "$run" ]]; then
  sha=$(gh run view "$run" -R "$repo" --json headSha --jq .headSha)
  run="$run $(gh run view "$run" -R "$repo" --json url --jq .url)"
else
  sha=$(gh pr view "$pr" -R "$repo" --json headRefOid --jq .headRefOid)
  run=$(gh run list -R "$repo" --commit "$sha" --limit 10 --json databaseId,url,conclusion \
  --jq '[.[] | select(.conclusion == "failure" or .conclusion == "cancelled" or .conclusion == "timed_out")][0] // empty | "\(.databaseId) \(.url)"')
fi
if [[ -z "$run" ]]; then
  echo "No failed CI run on PR #$pr head ${sha:0:7}. Green or still running: scripts/gh/wait-ci.sh $pr"
  exit 3
fi
run_id=${run%% *}; run_url=${run#* }

log=$(gh run view "$run_id" -R "$repo" --log-failed 2>/dev/null || true)
if [[ -z "$log" ]]; then
  # A cancelled run has no --log-failed output: read the cancelled jobs' logs; the last step is the hung one.
  jobs=$(gh run view "$run_id" -R "$repo" --json jobs --jq '.jobs[] | select(.conclusion == "cancelled" or .conclusion == "failure") | .databaseId')
  for j in $jobs; do
    log+=$(gh run view -R "$repo" --job "$j" --log 2>/dev/null \
      | awk -F'\t' '$2 ~ /^(Complete job|Set up job|Post )/ { next } { if ($2 != s) { buf = ""; s = $2 } buf = buf $0 "\n" } END { printf "%s", buf }' || true)
  done
  log+=$'\ncancelled\tcancelled\t##[error]The run was canceled.'
fi

report=$(printf '%s\n' "$log" | python3 -c '
import re, sys
lines = []
steps = []
for raw in sys.stdin:
    parts = raw.rstrip("\n").split("\t", 2)
    if len(parts) < 3: continue
    job, step, rest = parts
    text = re.sub(r"^\d{4}-\d\d-\d\dT[\d:.]+Z ?", "", rest)
    text = re.sub(r"\x1b\[[0-9;]*m", "", text)
    if (job, step) not in steps and job != "cancelled": steps.append((job, step))
    lines.append(text)
out = []
seen = set()
def first_after(i, pat, span=14):
    for t in lines[i + 1:i + span]:
        if re.search(pat, t): return t.strip()
    return ""
for i, t in enumerate(lines):
    # Playwright summary: "  1) [mobile-en] › e2e/x.spec.ts:98:9 › title" (the retry repeats it).
    m = re.match(r"\s+\d+\) (\[[\w-]+\] › \S+:\d+:\d+ › .+?)\s*(?:─+)?$", t)
    if m and m.group(1) not in seen:
        seen.add(m.group(1))
        err = first_after(i, r"Error:|expect\(")
        src = first_after(i, r"^\s*> +\d+ \|", 30)
        out.append(f"- `{m.group(1)}`\n  {err}" + (f"\n  `{src}`" if src else ""))
        continue
    # Vitest: " FAIL  src/app/x.spec.ts > suite > test"
    m = re.match(r"\s*(?:FAIL|×)\s+(\S+\.spec\.ts\s+>.+)", t)
    if m and m.group(1) not in seen:
        seen.add(m.group(1))
        err = first_after(i, r"Error|expected")
        out.append(f"- `{m.group(1).strip()}`\n  {err}")
        continue
    # ESLint "  12:5  error  message  rule" (file on an earlier line), TypeScript / Angular build errors.
    if re.match(r"\s+\d+:\d+\s+error\s", t) or re.search(r"error TS\d+|\[ERROR\]|^Error: ", t):
        key = t.strip()
        if key not in seen:
            seen.add(key)
            fl = next((p.strip() for p in reversed(lines[max(0, i - 20):i]) if re.match(r"^/\S+\.(ts|html|scss)$", p.strip())), "")
            out.append(f"- {fl + ": " if fl and re.match(r"\s+\d+:\d+", t) else ""}`{key[:220]}`")
# Vitest in CI reports each failure as a GitHub annotation, "##[error]AssertionError: ..." followed
# by "❯ src/...spec.ts:142:25"; used when nothing above matched, so Playwright is not listed twice.
if not out:
    for i, t in enumerate(lines):
        m = re.match(r"##\[error\](.+)", t.strip())
        if m and not m.group(1).startswith("Process completed"):
            loc = first_after(i, r"❯ \S+:\d+", 12).lstrip("❯ ")
            out.append(f"- `{loc}`: {m.group(1)[:200]}" if loc else f"- {m.group(1)[:200]}")
tail = [t for t in lines if t.strip()][-25:]
timed_out = any(re.search(r"has timed out|operation was canceled|run was canceled", t) for t in lines)
if timed_out:
    specs = [m.group(0) for t in lines for m in [re.search(r"\S+\.spec\.ts", t)] if m]
    last = f"; last spec file logged: `{specs[-1]}`" if specs else ""
    out = [o for o in out if not re.search(r"has timed out|was canceled", o)]
    out.insert(0, f"- **Timed out or cancelled**, likely a hang{last}")
print("Failed step(s): " + "; ".join(f"{j} › {s}" for j, s in steps))
if out:
    print(f"{len(out)} failure(s):"); print("\n".join(out))
else:
    print("No test or compiler error recognised; last 25 log lines:"); print("```"); print("\n".join(tail)); print("```")
' | head -60)

body="**CI failed** on \`${sha:0:7}\` ([run]($run_url)), read by \`scripts/gh/ci-failures.sh\`:

$report"
echo "$body"
if (( post )); then
  gh pr comment "$pr" -R "$repo" --body "$body" | tail -1
fi
