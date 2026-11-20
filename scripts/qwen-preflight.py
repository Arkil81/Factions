#!/usr/bin/env python3
"""
Fast preflight for the shared Ollama Qwen worker.

Purpose:
- prove qwen3.6:35b is loaded/responding now;
- avoid sending delegated triage into a shared Ollama queue;
- provide machine-readable evidence for loop-run-log.md.

Exit codes:
  0 = model responded with exact PROBE_OK within latency budget
  1 = endpoint/model responded but output was wrong or too slow
  2 = endpoint error / timeout / unavailable
"""

import argparse
import json
import sys
import time
import urllib.error
import urllib.request


def main() -> int:
    parser = argparse.ArgumentParser(description="Preflight shared Ollama Qwen before delegation")
    parser.add_argument("--model", default="qwen3.6:35b")
    parser.add_argument("--base-url", default="http://127.0.0.1:11434")
    parser.add_argument("--timeout", type=float, default=20.0, help="HTTP timeout seconds")
    parser.add_argument("--max-latency", type=float, default=20.0, help="fail if elapsed seconds exceeds this")
    parser.add_argument("--prompt", default="Reply with exactly: PROBE_OK")
    args = parser.parse_args()

    body = {
        "model": args.model,
        "messages": [{"role": "user", "content": args.prompt}],
        "stream": False,
        "options": {
            "temperature": 0,
            "num_predict": 1500,
        },
    }
    url = args.base_url.rstrip("/") + "/api/chat"
    request = urllib.request.Request(
        url,
        data=json.dumps(body).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )

    started = time.monotonic()
    try:
        with urllib.request.urlopen(request, timeout=args.timeout) as response:
            raw = response.read().decode("utf-8", "replace")
    except (urllib.error.URLError, TimeoutError) as exc:
        elapsed = time.monotonic() - started
        print(json.dumps({
            "ok": False,
            "reason": "endpoint_error_or_timeout",
            "model": args.model,
            "elapsed_seconds": round(elapsed, 3),
            "error": str(exc),
        }, sort_keys=True))
        return 2

    elapsed = time.monotonic() - started
    try:
        parsed = json.loads(raw)
    except json.JSONDecodeError as exc:
        print(json.dumps({
            "ok": False,
            "reason": "bad_json",
            "model": args.model,
            "elapsed_seconds": round(elapsed, 3),
            "error": str(exc),
            "raw_prefix": raw[:200],
        }, sort_keys=True))
        return 2

    content = (parsed.get("message") or {}).get("content", "").strip()
    ok = content == "PROBE_OK" and elapsed <= args.max_latency
    print(json.dumps({
        "ok": ok,
        "model": args.model,
        "elapsed_seconds": round(elapsed, 3),
        "response": content,
        "max_latency_seconds": args.max_latency,
    }, sort_keys=True))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
