#!/usr/bin/env bash
# Runs one arm of a native-vs-Scirix study as an isolated headless Claude Code session.
# Protocol and scoring: docs/case-studies/scirix-study-protocol.md
#
#   run-arm.sh <native|scirix> <study-dir> <run-id> <commit> <prompt-file> [read|edit]
#
# Both arms get the same model, effort, commit, tool whitelist and CLAUDE.md, except:
#   native  no Scirix hook, no skills, `scirix` shadowed on PATH, CLAUDE.md's Scirix section removed
#   scirix  the UserPromptSubmit preprompt hook, the skill, and read-only `scirix rm`
# User settings are never loaded (--setting-sources project,local), so neither arm inherits the
# operator's hooks, permissions or auto-memory; the worktree path gives it an empty memory dir.
set -euo pipefail

ARM=$1 STUDY=$2 RUN=$3 COMMIT=$4 PROMPT=$5 MODE=${6:-read}
MODEL=${STUDY_MODEL:-claude-opus-5-5}
EFFORT=${STUDY_EFFORT:-high}
REPO=${STUDY_REPO:-$HOME/Factions}
SCIRIX_BIN=${SCIRIX_BIN:-$HOME/.local/bin/scirix}

case $ARM in native|scirix) ;; *) echo "arm must be native or scirix" >&2; exit 2;; esac
case $MODE in read|edit) ;; *) echo "mode must be read or edit" >&2; exit 2;; esac

WT=$STUDY/wt/$RUN
OUT=$STUDY/runs/$RUN
mkdir -p "$STUDY/wt" "$OUT"
[ -e "$WT" ] && { echo "$WT exists; one worktree per run" >&2; exit 2; }
git -C "$REPO" worktree add -q --detach "$WT" "$COMMIT"
cp "$PROMPT" "$OUT/prompt.md"

ALLOW=(Read Grep Glob
  "Bash(git log:*)" "Bash(git show:*)" "Bash(git grep:*)" "Bash(git blame:*)" "Bash(git diff:*)"
  "Bash(grep:*)" "Bash(rg:*)" "Bash(find:*)" "Bash(ls:*)" "Bash(cat:*)" "Bash(head:*)"
  "Bash(tail:*)" "Bash(sed -n:*)" "Bash(wc:*)" "Bash(sort:*)" "Bash(uniq:*)" "Bash(awk:*)"
  "Bash(echo:*)" "Bash(cd:*)" "Bash(which:*)")
DENY=("Bash(git push:*)" "Bash(ssh:*)" "Bash(docker:*)")
TOOLS=(Bash Read Grep Glob)
if [ "$MODE" = edit ]; then ALLOW+=("Edit(./**)" "Write(./**)"); TOOLS+=(Edit Write); fi

# dontAsk refuses anything not allowed above; --tools removes every other built-in, including
# the ones that reach outside the run (SendMessage, Workflow, Cron*, Agent, Web*).
ARGS=(-p --model "$MODEL" --effort "$EFFORT" --setting-sources project,local
  --permission-mode dontAsk --output-format stream-json --verbose --strict-mcp-config)

if [ "$ARM" = native ]; then
  python3 - "$WT/CLAUDE.md" <<'PY'
import re, sys
p = sys.argv[1]
s = open(p, encoding="utf-8").read()
s2 = re.sub(r"(?ms)^## Investigation tooling — use Scirix\n.*?(?=^## )", "", s)
if s2 == s:
    sys.exit("CLAUDE.md: Scirix section not found; update run-arm.sh before running")
open(p, "w", encoding="utf-8").write(s2)
PY
  SHIM=$STUDY/shim
  mkdir -p "$SHIM"
  printf '#!/bin/sh\necho "scirix: command not found" >&2\nexit 127\n' > "$SHIM/scirix"
  chmod +x "$SHIM/scirix"
  export PATH="$SHIM:$PATH"
  ARGS+=(--disable-slash-commands)
else
  HOOK=$(printf '{"hooks":{"UserPromptSubmit":[{"hooks":[{"type":"command","command":"%s preprompt --host claude"}]}]}}' "$SCIRIX_BIN")
  ARGS+=(--settings "$HOOK" --add-dir "$HOME/.cache/scirix/preprompt" "$HOME/.scirix/skills/scirix")
  # User skills are not loaded without the user settings source, so the skill is linked as a
  # project skill of this worktree only.
  mkdir -p "$WT/.claude/skills"
  ln -s "$HOME/.scirix/skills/scirix" "$WT/.claude/skills/scirix"
  TOOLS+=(Skill)
  ALLOW+=(Skill "Bash(scirix rm:*)" "Bash($SCIRIX_BIN rm:*)")
  for v in reanalyze resolve-drift acknowledge-advisory "conventions add" "conventions set" \
           "conventions approve" "conventions dismiss" "conventions delete"; do
    DENY+=("Bash(scirix rm $v:*)" "Bash($SCIRIX_BIN rm $v:*)")
  done
fi

{
  printf 'arm=%s\nrun=%s\ncommit=%s\nmode=%s\nmodel=%s\neffort=%s\n' \
    "$ARM" "$RUN" "$(git -C "$WT" rev-parse HEAD)" "$MODE" "$MODEL" "$EFFORT"
  echo "started_utc=$(date -u +%FT%TZ)"
  echo "scirix_version=$("$SCIRIX_BIN" --version 2>&1)"
} > "$OUT/meta.txt"

(cd "$WT" && claude "${ARGS[@]}" --tools "${TOOLS[@]}" --allowedTools "${ALLOW[@]}" --disallowedTools "${DENY[@]}" \
   < "$PROMPT") > "$OUT/stream.jsonl" 2> "$OUT/stderr.txt" || echo "exit=$?" >> "$OUT/meta.txt"
echo "finished_utc=$(date -u +%FT%TZ)" >> "$OUT/meta.txt"

# The persisted session transcript carries what stream-json does not: injected hook context.
SID=$(python3 -c 'import json,sys
for l in open(sys.argv[1]):
    try: e=json.loads(l)
    except ValueError: continue
    if e.get("session_id"): print(e["session_id"]); break' "$OUT/stream.jsonl")
SLUG=$(printf '%s' "$WT" | sed 's#[^A-Za-z0-9]#-#g')
cp "$HOME/.claude/projects/$SLUG/$SID.jsonl" "$OUT/transcript.jsonl" 2>/dev/null \
  || echo "transcript_missing=$HOME/.claude/projects/$SLUG/$SID.jsonl" >> "$OUT/meta.txt"

python3 "$(dirname "$0")/summarize-run.py" "$OUT"
