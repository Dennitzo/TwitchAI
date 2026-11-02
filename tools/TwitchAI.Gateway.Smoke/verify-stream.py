"""Local SSE acceptance; sends only a fixed greeting and never contacts Twitch."""
import argparse
import json
from pathlib import Path
import time
import urllib.request


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--output", default="artifacts/validation/gateway-stream.json")
    args = parser.parse_args()
    payload = {
        "model": args.model,
        "stream": True,
        "temperature": 0,
        "reasoning_effort": "none",
        "max_tokens": 128,
        "messages": [
            {"role": "system", "content": "Reply with the required JSON object containing ok=true."},
            {"role": "user", "content": "Guten Abend, einen schönen Stream zusammen!"},
        ],
        "response_format": {"type": "json_schema", "json_schema": {
            "name": "stream_probe", "strict": True,
            "schema": {"type": "object", "properties": {"ok": {"const": True}},
                       "required": ["ok"], "additionalProperties": False},
        }},
    }
    started = time.monotonic()
    request = urllib.request.Request("http://127.0.0.1:8180/v1/chat/completions",
        data=json.dumps(payload).encode(), headers={"Content-Type": "application/json"})
    text, events, done, first_event = "", [], False, None
    with urllib.request.urlopen(request, timeout=180) as response:
        content_type = response.headers.get_content_type()
        for line in response:
            if not line.startswith(b"data:"):
                continue
            if first_event is None:
                first_event = time.monotonic() - started
            data = line[5:].strip()
            if data == b"[DONE]":
                done = True
                break
            event = json.loads(data)
            events.append(event)
            for choice in event.get("choices", []):
                text += choice.get("delta", {}).get("content") or ""
    parse_error = None
    try:
        parsed = json.loads(text)
    except json.JSONDecodeError as error:
        parsed, parse_error = None, str(error)
    passed = done and len(events) > 0 and content_type == "text/event-stream" and parsed == {"ok": True}
    report = {"passed": passed, "model": args.model, "contentType": content_type,
              "events": events, "done": done, "content": text, "parseError": parse_error,
              "firstEventSeconds": first_event, "elapsedSeconds": time.monotonic() - started,
              "twitchRequestsSent": 0}
    target = Path(args.output)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False))
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
