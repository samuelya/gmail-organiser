#!/usr/bin/env bash
# Usage: scripts/gh/team-metrics.sh [--weeks N | --since YYYY-MM-DDTHH:MMZ]   (default --weeks 2)
#   --since starts the window at the previous review's timestamp, so a review measures exactly
#   what changed since the last one.
# One read-only call that prints the numbers an ecosystem review (/eco-review) needs, so the review
# never rediscovers them:
#   1. Tokens by model and by role, and the most expensive agent runs (turns, context per turn),
#      from the local Claude Code transcripts in ~/.claude/projects/<this project>/.
#   2. Merged PRs in the window with their round history (failed-round comments, escalation
#      comments and labels on the linked issues, tester bugs filed against the PR). Rounds are
#      counted from the first line scripts/gh/round.sh writes.
#   3. Derived rates: merged PRs/week, input tokens processed per merged PR, failed-round rate,
#      escalation rate, bugs per PR, average CI minutes.
# "Input processed" = uncached + cache write + cache read tokens: what every turn re-sends. It is
# the number to drive down (fewer turns, smaller context); cache reads are cheaper per token but
# dominate the volume.
set -euo pipefail
source "$(dirname "$0")/_lib.sh"

weeks=2; since=
while (( $# )); do
  case "$1" in
    --weeks) weeks="${2:?--weeks needs a number}"; require_number "$weeks"; shift 2 ;;
    --since) since="${2:?--since needs a timestamp}"; shift 2
      [[ "$since" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}(T[0-9]{2}:[0-9]{2}(:[0-9]{2})?Z?)?$ ]] || { echo "--since wants YYYY-MM-DD[THH:MM[:SS]Z]" >&2; exit 64; } ;;
    *) echo "Unknown argument: $1" >&2; exit 64 ;;
  esac
done
if [[ -n "$since" ]]; then
  [[ "$since" == *T* ]] || since="${since}T00:00:00Z"
  [[ "$since" == *Z ]] || since="${since}Z"
  weeks=$(python3 -c "import datetime as d, sys; s=d.datetime.fromisoformat(sys.argv[1].replace('Z','+00:00')); print(round((d.datetime.now(d.timezone.utc)-s).total_seconds()/604800, 2))" "$since")
else
  since=$(date -u -v-"$((weeks*7))"d +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -d "$((weeks*7)) days ago" +%Y-%m-%dT%H:%M:%SZ)
fi
day=${since%%T*}
# The main checkout, even when run from a worktree: transcripts and guard.log live under it.
root=$(dirname "$(git rev-parse --path-format=absolute --git-common-dir)")
proj="$HOME/.claude/projects/$(sed 's#[/.]#-#g' <<<"$root")"
echo "Window: $weeks week(s), since $since"
echo

echo "=== 1. Tokens (transcripts under $proj)"
input_total=$(python3 - "$proj" "$since" "$root" <<'PY'
import sys, json, glob, os, re, collections, statistics
proj, since, root = sys.argv[1], sys.argv[2], sys.argv[3]
files = glob.glob(os.path.join(proj, "*.jsonl")) + glob.glob(os.path.join(proj, "*", "subagents", "*.jsonl"))
# Order matters: the first pattern that matches wins. Agent ids are the names the lead gives a
# subagent, so these cover the spellings in use; anything unmatched lands in unnamed-agent, which
# the review reads as "an instrument gap", not "a mystery cost" (#207). A skill run as a fork
# (/code-review) gets a bare hex id nobody named; the record of its launch in the parent transcript
# (toolUseResult: status "forked", commandName, agentId) is the only link back, so it is read here.
ROLES = ((r"business|^aba-", "business-analyst"),
         (r"^aangle-", "code-review"),
         (r"tester|^aqa-|^atest", "tester"),
         (r"frontend|^afe\b|^afc\b|^afe-|^afc-", "frontend-coder"),
         (r"backend|^abe-|^abc-", "backend-coder"),
         (r"^acoder-", "coder"), ("guide", "claude-code-guide"), ("review", "code-review"))
forked = {}  # agentId -> command name, from the launch records
fork_src = {}  # agentId -> uuid of the assistant record that invoked the skill
skill_args = {}  # assistant uuid -> args of its /code-review call ("high 234", "medium pull/170", "165")
first_prompt = {}  # agentId -> the task the run was started with
def role(agent):
    if not agent: return "lead"
    if agent in forked: return "code-review" if forked[agent] == "code-review" else f"skill:{forked[agent]}"
    for pat, name in ROLES:
        if re.search(pat, agent): return name
    # An agent spawned without a name has a bare hex id; its task says what it was (#207 review 4:
    # 13 of 13 first-round coders were unnamed, so every feature PR read "coder runs 0").
    p = first_prompt.get(agent, "")
    if re.match(r"\s*Implement issues? #\d+", p): return "coder"
    if p.lstrip().startswith("Review target"): return "code-review"
    return "unnamed-agent"
# A coder or tester run's agent id carries the issue(s) or the PR it works: afe-212, afe-212-r4,
# acoder-49-50, afe-265-fix (PR #265), atest-259b, atester-53. An unnamed coder's task names its issues in the
# first sentence. Issues and PRs share one number space, so section 2 looks a number up both as a
# linked issue and as the PR itself. Counting runs this way gives the review a number the round
# comments can be checked against: 4 coder runs against 0 round comments is the gap (#207).
RUN_ID = re.compile(r"^a(?:frontend-coder|backend-coder|coder|fe|fc|be|bc|tester|test|qa)((?:-\d+)+)(?:[a-z]?-|[a-z]?$)")
def numbers_of(agent, rl):
    if rl not in ("frontend-coder", "backend-coder", "coder", "tester"): return []
    m3 = RUN_ID.match(agent or "")
    if m3: return [int(n) for n in m3.group(1).split("-") if n]
    p = first_prompt.get(agent, "")
    return [int(n) for n in re.findall(r"#(\d+)", re.split(r"\.\s", p, maxsplit=1)[0])] if rl == "coder" else []
# One API request is stored as several streamed records sharing requestId; keep the max per field.
req = {}
for f in files:
    try: fh = open(f, encoding="utf-8", errors="replace")
    except OSError: continue
    with fh:
        want_prompt = "subagents" in f
        for line in fh:
            if want_prompt and '"user"' in line:
                try: u0 = json.loads(line)
                except ValueError: u0 = {}
                if u0.get("type") == "user" and u0.get("agentId"):
                    c = (u0.get("message") or {}).get("content")
                    if isinstance(c, list): c = " ".join(b.get("text", "") for b in c if isinstance(b, dict))
                    first_prompt[u0["agentId"]] = c or ""; want_prompt = False
            if '"toolUseResult"' in line and '"forked"' in line:
                try: t = json.loads(line).get("toolUseResult") or {}
                except ValueError: t = {}
                if isinstance(t, dict) and t.get("status") == "forked" and t.get("agentId"):
                    forked[t["agentId"]] = t.get("commandName") or "?"
                    fork_src[t["agentId"]] = json.loads(line).get("sourceToolAssistantUUID")
                continue
            if '"usage"' not in line: continue
            try: r = json.loads(line)
            except ValueError: continue
            if r.get("type") != "assistant" or (r.get("timestamp") or "") < since: continue
            m = r.get("message") or {}; u = m.get("usage") or {}
            for b in (m.get("content") or []) if isinstance(m.get("content"), list) else []:
                if isinstance(b, dict) and b.get("type") == "tool_use" and b.get("name") == "Skill" and (b.get("input") or {}).get("skill") == "code-review":
                    skill_args[r.get("uuid")] = (b["input"].get("args") or "").strip()
            k = r.get("requestId") or r.get("uuid")
            vals = [u.get("input_tokens", 0), u.get("cache_creation_input_tokens", 0), u.get("cache_read_input_tokens", 0), u.get("output_tokens", 0)]
            cur = req.setdefault(k, {"model": m.get("model", "?"), "agent": r.get("agentId") or "", "v": [0, 0, 0, 0], "sid": r.get("sessionId") or "", "ts": r.get("timestamp") or ""})
            cur["v"] = [max(a, b) for a, b in zip(cur["v"], vals)]
by_model = collections.defaultdict(lambda: [0, 0, 0, 0]); by_role = collections.defaultdict(lambda: [0, 0, 0, 0, 0])
runs = collections.defaultdict(lambda: {"ctx": [], "out": 0})
for x in req.values():
    v = x["v"]; rl = role(x["agent"])
    for i in range(4): by_model[x["model"]][i] += v[i]; by_role[rl][i] += v[i]
    by_role[rl][4] += 1
    run = runs[x["agent"] or "lead sessions"]; run["ctx"].append(v[0] + v[1] + v[2]); run["out"] += v[3]; run["role"] = rl
    run["sid"] = x["sid"]; run["t0"] = min(run.get("t0") or x["ts"], x["ts"]); run["t1"] = max(run.get("t1") or x["ts"], x["ts"])
def k(n): return f"{n/1000:,.0f}k"
print(f"{'model':<26}{'uncached in':>13}{'cache write':>13}{'cache read':>14}{'output':>10}")
for mdl, v in sorted(by_model.items(), key=lambda x: -sum(x[1])):
    print(f"{mdl:<26}{k(v[0]):>13}{k(v[1]):>13}{k(v[2]):>14}{k(v[3]):>10}")
print(); print(f"{'role':<26}{'turns':>7}{'input processed':>17}{'cache write':>13}{'output':>10}{'avg ctx/turn':>14}")
for rl, v in sorted(by_role.items(), key=lambda x: -(x[1][0] + x[1][1] + x[1][2])):
    inp = v[0] + v[1] + v[2]
    print(f"{rl:<26}{v[4]:>7}{k(inp):>17}{k(v[1]):>13}{k(v[3]):>10}{k(inp / max(v[4], 1)):>14}")
print(); print("Most expensive runs (by input processed):")
print(f"{'agent':<40}{'role':<17}{'turns':>6}{'median ctx':>12}{'max ctx':>10}{'input processed':>17}{'output':>9}")
for a, r in sorted(runs.items(), key=lambda x: -sum(x[1]["ctx"]))[:8]:
    c = r["ctx"]; print(f"{a[:39]:<40}{r['role']:<17}{len(c):>6}{k(statistics.median(c)):>12}{k(max(c)):>10}{k(sum(c)):>17}{k(r['out']):>9}")
# Turn caps from the agent definitions' frontmatter (maxTurns), so the review sees runs that hit them.
caps = {}
for f in glob.glob(os.path.join(root, ".claude", "agents", "custom", "*.md")):
    m2 = re.search(r"^maxTurns:\s*(\d+)", open(f, encoding="utf-8").read(), re.M)
    if m2: caps[os.path.basename(f)[:-3]] = int(m2.group(1))
print(); print("Runs per role (agents only): count, median turns, runs at or over maxTurns")
for rl, cap_role in (("frontend-coder", "frontend-coder"), ("backend-coder", "backend-coder"), ("coder", "frontend-coder"), ("tester", "tester"), ("business-analyst", "business-analyst")):
    t = [len(r["ctx"]) for a, r in runs.items() if a != "lead sessions" and r["role"] == rl]
    if not t: continue
    cap = caps.get(cap_role)
    hit = sum(1 for n in t if cap and n >= cap)
    print(f"{rl:<20} runs {len(t):>3}  median turns {statistics.median(t):>5.0f}  max {max(t):>4}  at cap ({cap or 'none'}): {hit}")
per_issue = collections.defaultdict(list)
for a, r in runs.items():
    for n in numbers_of(a, r["role"]): per_issue[n].append(a)
print("ISSUE_RUNS " + json.dumps({str(n): ids for n, ids in sorted(per_issue.items())}))
print("RUN_INFO " + json.dumps({a: ["tester" if runs[a]["role"] == "tester" else "coder", sum(runs[a]["ctx"])] for ids in per_issue.values() for a in ids}))
# /code-review cost per PR: a top-level review fork carries its /code-review args (level and PR);
# the dimension forks it spawns (aangle-*) carry neither, so each is placed in the review fork that
# was active in the same session when it started. Level "default" = the call named none.
reviews = []
tops = {a: r for a, r in runs.items() if a in forked and forked[a] == "code-review"}
for a, r in tops.items():
    args = skill_args.get(fork_src.get(a), "")
    lvl = re.search(r"\b(low|medium|high|xhigh|max|ultra)\b", args)
    pr = re.search(r"(?:pull/|#)(\d+)", args) or re.search(r"\b(\d{2,4})\b", args)
    r["review"] = {"pr": int(pr.group(1)) if pr else None, "level": lvl.group(1) if lvl else "default", "turns": len(r["ctx"]), "input": sum(r["ctx"]), "agents": 1}
for a, r in runs.items():
    if r["role"] != "code-review" or a in tops: continue
    host = [t for t in tops.values() if t["sid"] == r["sid"] and t["t0"] <= r["t0"] <= t["t1"]]
    if host:
        h = host[0]["review"]; h["turns"] += len(r["ctx"]); h["input"] += sum(r["ctx"]); h["agents"] += 1
for r in tops.values(): reviews.append(r["review"])
print("REVIEW_RUNS " + json.dumps(reviews))
print(f"TOTAL_INPUT_PROCESSED {sum(sum(r['ctx']) for r in runs.values())}")
PY
)
echo "$input_total" | grep -vE '^(TOTAL_INPUT_PROCESSED|ISSUE_RUNS|RUN_INFO|REVIEW_RUNS)'
run_info=$(grep '^RUN_INFO' <<<"$input_total" | cut -d' ' -f2-)
run_info=${run_info:-'{}'}
issue_runs=$(grep '^ISSUE_RUNS' <<<"$input_total" | cut -d' ' -f2-)
issue_runs=${issue_runs:-'{}'}
review_runs=$(grep '^REVIEW_RUNS' <<<"$input_total" | cut -d' ' -f2-)
review_runs=${review_runs:-'[]'}
input_total=$(grep '^TOTAL_INPUT_PROCESSED' <<<"$input_total" | cut -d' ' -f2)
echo

echo "=== 2. Merged PRs since $day"
json=$(gql -f q="repo:$OWNER/$REPO is:pr is:merged merged:>=$since" -f query='
  query($q: String!) { search(query: $q, type: ISSUE, first: 100) { nodes { ... on PullRequest {
    number title createdAt mergedAt comments { totalCount } files { totalCount }
    closingIssuesReferences(first: 5) { nodes { number labels(first: 20) { nodes { name } }
      comments(last: 60) { nodes { body } } } } } } } }')
bugs=$(gh issue list -R "$OWNER/$REPO" --state all --label type:bug --limit 200 --search "created:>=$since" --json body --jq '[.[].body | [scan("Found while testing PR #([0-9]+)")[]] | unique[]] ' 2>/dev/null || echo '[]')
jq -r --argjson bugs "$bugs" --argjson runs "$issue_runs" --argjson info "$run_info" --argjson reviews "$review_runs" '
  .data.search.nodes[] | select(.number != null)
  | .number as $n
  | (.closingIssuesReferences.nodes) as $is
  # Per issue: failed-round comments (any n/m, scripts/gh/round.sh format) or, if higher, the count
  # an "Escalation: n/m" / "Escalation: n+" comment states; hand-written variants used both.
  | ([$is[] | ([.comments.nodes[].body | select(test("^\\**Round [0-9]+/[0-9]+ failed"; "i"))] | length) as $rf
      | ([.comments.nodes[].body | capture("^\\**Escalation:\\s*(?<n>[0-9]+)"; "i") | .n | tonumber] | max // 0) as $en
      | ([$rf, $en] | max)] | add // 0) as $rounds
  | ([$reviews[] | select(.pr == $n)]) as $rv
  | ([$rv[].input] | add // 0) as $rvin
  | ([($is[] | .number), $n] | map($runs[tostring] // []) | add // [] | unique) as $ids
  | ([$ids[] | select($info[.][0] == "coder")] | length) as $cr
  | ([$ids[] | select($info[.][0] == "tester")] | length) as $tr
  | (([$ids[] | $info[.][1]] | add // 0) + $rvin) as $tok
  | ([$is[] | ([.comments.nodes[].body | select(test("^\\**(Round [0-9]+/[0-9]+|Escalation:)"; "i"))] | length)] | add // 0) as $rc
  | ([$is[].labels.nodes[].name | select(startswith("escalated:") or . == "needs-owner")] | unique | join(" ")) as $esc
  | ([$bugs[] | select(. == ($n | tostring))] | length) as $b
  | "#\(.number)  \(.title[0:80])\n    issues: \([$is[].number] | map(tostring) | join(",") | if . == "" then "-" else . end) | files \(.files.totalCount) | comments \(.comments.totalCount) | \(((((.mergedAt|fromdate)-(.createdAt|fromdate))/360)|round)/10)h open | failed rounds \($rounds) | bugs \($b)\(if $esc != "" then " | \($esc)" else "" end)\(if $cr > 0 or $rc > 0 then "\n    coder runs \($cr) (transcripts) vs \($rc) round comments\(if $cr > $rc then "  <- unrecorded rounds" else "" end) | tester runs \($tr) | coder+tester+review \((($tok / 1e6 * 10) | round) / 10)M" else "" end)\(if ($rv | length) > 0 then "\n    /code-review \($rv | map("\(.level) \((.input / 1e6 * 10 | round) / 10)M") | join(", ")) = \((($rvin / 1e6 * 10) | round) / 10)M" else "" end)",
    "ROW\t\($rounds)\t\($b)\t\(if $esc != "" then 1 else 0 end)\t\($cr)\t\($rc)\t\($rv | length)\t\($rvin)"
' <<<"$json" > /tmp/team-metrics.$$ || true
grep -v '^ROW' /tmp/team-metrics.$$
count=$(grep -c '^ROW' /tmp/team-metrics.$$ || true)
rounds_total=$(awk -F'\t' '/^ROW/{s+=$2} END{print s+0}' /tmp/team-metrics.$$)
bugs_total=$(awk -F'\t' '/^ROW/{s+=$3} END{print s+0}' /tmp/team-metrics.$$)
escalated=$(awk -F'\t' '/^ROW/{s+=$4} END{print s+0}' /tmp/team-metrics.$$)
coder_runs=$(awk -F'\t' '/^ROW/{s+=$5} END{print s+0}' /tmp/team-metrics.$$)
round_comments=$(awk -F'\t' '/^ROW/{s+=$6} END{print s+0}' /tmp/team-metrics.$$)
reviewed_prs=$(awk -F'\t' '/^ROW/ && $7 > 0 {n++} END{print n+0}' /tmp/team-metrics.$$)
review_input=$(awk -F'\t' '/^ROW/{s+=$8} END{print s+0}' /tmp/team-metrics.$$)
# Review runs by level, over every merged PR in the window (input tokens per run, so effort has a price).
review_levels=$(jq -r '[group_by(.level)[] | "\(.[0].level) \(length)x avg \(((map(.input) | add) / length / 1e6 * 10 | round) / 10)M"] | join(", ")' <<<"$review_runs")
review_orphans=$(jq -r '[.[] | select(.pr == null)] | "\(length) runs, \(((map(.input) | add // 0) / 1e6 * 10 | round) / 10)M"' <<<"$review_runs")
rm -f /tmp/team-metrics.$$
echo

echo "=== 3. Guard hook blocks since $day (.claude/guard.log: count, rule [role])"
if [[ -f "$root/.claude/guard.log" ]]; then
  awk -F'\t' -v s="$since" '$1 >= s {r[$3" ["$2"]"]++} END {for (k in r) printf "%4d  %s\n", r[k], k}' "$root/.claude/guard.log" | sort -rn | head -20
else
  echo "(no blocks logged)"
fi
echo

echo "=== 4. Rates"
ci=$(gh run list -R "$OWNER/$REPO" --workflow app.yml --event pull_request --limit 40 --json createdAt,updatedAt \
  --jq "[.[] | select(.createdAt >= \"$since\") | ((.updatedAt|fromdate)-(.createdAt|fromdate))/60] | if length>0 then (add/length*10|round)/10 else 0 end")
# Arithmetic in python: an awk program inside $(...) loses its braces under some shells and then
# blocks reading stdin.
calc() { python3 -c "import sys; print($1)"; }
if (( count > 0 )); then
  per_pr=$(calc "f'{$input_total/$count/1e6:.1f}M'"); rounds_pr=$(calc "f'{$rounds_total/$count:.2f}'"); bugs_pr=$(calc "f'{$bugs_total/$count:.2f}'")
else
  per_pr=n/a; rounds_pr=n/a; bugs_pr=n/a
fi
echo "merged PRs: $count ($(calc "f'{$count/$weeks:.1f}'")/week)"
echo "input tokens processed per merged PR: $per_pr"
echo "failed rounds per PR: $rounds_pr   escalated PRs: $escalated   tester bugs per PR: $bugs_pr"
echo "average PR CI run: ${ci} min"
if (( count > 0 )); then
  echo "/code-review on merged PRs: $reviewed_prs of $count reviewed, $(calc "f'{$review_input/1e6:.1f}M'") input tokens ($(calc "f'{$review_input/max($reviewed_prs,1)/1e6:.1f}M'") per reviewed PR); by level: ${review_levels:-none}; no PR in the call: ${review_orphans}"
fi
# Defects the pipeline missed: filed after merge with the found-after-merge label (CLAUDE.md).
escaped=$(gh issue list -R "$OWNER/$REPO" --state all --label found-after-merge --limit 100 --search "created:>=$since" \
  --json number,body --jq '"\(length) (" + ([.[] | "#\(.number)" + ((.body | capture("Escaped from #(?<p>[0-9]+)") | " from PR #\(.p)") // "")] | join(", ")) + ")"' 2>/dev/null || echo "n/a")
echo "defects found after merge: $escaped"
echo "coder runs on merged PRs' issues: $coder_runs (transcripts) vs $round_comments round comments — every run should post one (scripts/gh/round.sh)"
