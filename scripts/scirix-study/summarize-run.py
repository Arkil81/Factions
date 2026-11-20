#!/usr/bin/env python3
"""Summarise one study run directory written by run-arm.sh.

Writes summary.json, answer.md and scirix-calls.md next to the stream, and prints a one-line
digest. The isolation check is the part a report must not skip: a native run that saw Scirix, or a
scirix run whose hook never fired, is not a data point for its arm.
"""
import json
import sys
from collections import Counter
from pathlib import Path


def load(path):
    rows = []
    if not path.exists():
        return rows
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        try:
            rows.append(json.loads(line))
        except ValueError:
            pass
    return rows


def blocks(event):
    msg = event.get("message")
    content = msg.get("content") if isinstance(msg, dict) else None
    return content if isinstance(content, list) else []


def text_of(content):
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return "\n".join(c.get("text", "") for c in content if isinstance(c, dict))
    return ""


def main(out_dir):
    out = Path(out_dir)
    meta = dict(l.split("=", 1) for l in (out / "meta.txt").read_text().splitlines() if "=" in l)
    arm = meta.get("arm", "?")
    stream = load(out / "stream.jsonl")
    transcript_path = out / "transcript.jsonl"
    transcript_raw = transcript_path.read_text(encoding="utf-8", errors="replace") if transcript_path.exists() else ""

    tools = Counter()
    calls = {}
    results = {}
    for e in stream:
        for b in blocks(e):
            if b.get("type") == "tool_use":
                tools[b.get("name")] += 1
                calls[b.get("id")] = b
            elif b.get("type") == "tool_result":
                results[b.get("tool_use_id")] = text_of(b.get("content"))

    scirix_calls = []
    for cid, b in calls.items():
        inp = b.get("input") or {}
        cmd = inp.get("command", "") if b.get("name") == "Bash" else ""
        if "scirix" in cmd or (b.get("name") == "Skill" and "scirix" in json.dumps(inp)):
            scirix_calls.append({"tool": b.get("name"), "input": cmd or inp,
                                 "result_head": results.get(cid, "")[:600]})

    result = next((e for e in reversed(stream) if e.get("type") == "result"), {})
    usage = result.get("usage") or {}

    problems = []
    hook_fired = False
    for line in transcript_raw.splitlines():
        try:
            att = json.loads(line).get("attachment") or {}
        except ValueError:
            continue
        if att.get("hookEvent") == "UserPromptSubmit" and "<scirix-context>" in str(att.get("content", "")):
            hook_fired = True
    if not transcript_raw:
        problems.append("no session transcript: hook exposure cannot be checked")
    if arm == "native":
        if hook_fired:
            problems.append("native run received <scirix-context>")
        for c in scirix_calls:
            if "command not found" not in c["result_head"]:
                problems.append("native run got a scirix result: %s" % str(c["input"])[:120])
    elif arm == "scirix" and transcript_raw and not hook_fired:
        problems.append("scirix run: preprompt hook never injected context")
    if not result:
        problems.append("no result event: run did not finish")

    summary = {
        "arm": arm, "run": meta.get("run"), "commit": meta.get("commit"),
        "model": meta.get("model"), "effort": meta.get("effort"),
        "duration_ms": result.get("duration_ms"), "num_turns": result.get("num_turns"),
        "cost_usd": result.get("total_cost_usd"), "is_error": result.get("is_error"),
        "usage": usage,
        "tokens_total": sum(v for k, v in usage.items() if k.endswith("tokens") and isinstance(v, int)),
        # Excludes cache reads, which re-count the same context every turn and dominate the total.
        "tokens_new": sum(usage.get(k) or 0 for k in ("input_tokens", "cache_creation_input_tokens", "output_tokens")),
        "tool_calls": sum(tools.values()), "tools": dict(tools),
        "scirix_calls": len(scirix_calls), "hook_fired": hook_fired,
        "permission_denials": len(result.get("permission_denials") or []),
        "isolation_ok": not problems, "problems": problems,
    }
    (out / "summary.json").write_text(json.dumps(summary, indent=2))
    (out / "answer.md").write_text(result.get("result") or "")
    lines = ["# scirix calls — %s\n" % meta.get("run"),
             "Classify each: decisive | useful | neutral | noise | wrong | abandoned-for-grep\n"]
    for i, c in enumerate(scirix_calls, 1):
        lines += ["## %d. `%s`" % (i, str(c["input"])[:300]), "verdict: ", "",
                  "```", c["result_head"], "```", ""]
    (out / "scirix-calls.md").write_text("\n".join(lines))

    print("%s %s: %s turns, %s tool calls (%s scirix), %s new tokens (%s with cache reads), %.0fs, $%s, hook=%s, isolation=%s%s" % (
        arm, meta.get("run"), summary["num_turns"], summary["tool_calls"], summary["scirix_calls"],
        summary["tokens_new"], summary["tokens_total"], (summary["duration_ms"] or 0) / 1000.0, summary["cost_usd"],
        hook_fired, "ok" if not problems else "FAILED", "" if not problems else " — " + "; ".join(problems)))


if __name__ == "__main__":
    main(sys.argv[1])
