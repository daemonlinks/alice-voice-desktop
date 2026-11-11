#!/usr/bin/env python
"""Alice LOCAL tool bridge for the native app.

Reads a tool-call JSON from stdin, executes it, and prints the result JSON
to stdout. Same tools as the Alice Voice web relay (honcho_search, honcho_save,
honcho_forget, time, search).

Honcho memory is the shared workspace ``hermes`` (shown as Hermes in the
Honcho UI). Alice LOCAL is the peer ``alice_local`` inside that workspace,
observing ``AHC``. She is not a root workspace. The stray root workspace
whose id is ``alice_local`` must not be used. ``HONCHO_WORKSPACE``,
``HONCHO_PEER``, and ``HONCHO_AI_PEER`` are ignored so a stale environment
cannot point this script at that stray workspace or at ``alice_voice``.

Usage:
    echo '{"tool":"honcho_search","args":{"query":"..."}}' | python alice_local_tools.py
    echo '{"tool":"honcho_search","args":{"query":"..."}}' | python alice_local_tools.py --plan
"""
import datetime
import json
import os
import sys
import urllib.request

HONCHO_BASE = os.environ.get("HONCHO_BASE", "http://localhost:8001")
# Local self-hosted Honcho runs with AUTH_USE_AUTH=false, so no key is needed.
# HONCHO_API_KEY is optional: only sent if set (env or the AliceVoice .env),
# e.g. if auth is ever enabled on the server. Never hardcode a key here.
HONCHO_API_KEY = os.environ.get("HONCHO_API_KEY", "")

# Pinned. Do not read these from the environment.
WORKSPACE = "hermes"
PEER = "AHC"
AI_PEER = "alice_local"


def _load_env() -> None:
    """Load TAVILY_API_KEY, HONCHO_BASE and optional HONCHO_API_KEY from the AliceVoice .env file.

    Workspace and peer ids are not loaded from this file. A stale
    HONCHO_WORKSPACE=alice_local would select the stray root workspace.
    """
    global HONCHO_API_KEY, HONCHO_BASE
    env_path = os.environ.get("ALICEVOICE_ENV", r"C:\ProgramData\AliceVoice\.env")
    if not os.path.exists(env_path):
        return
    for line in open(env_path, encoding="utf-8", errors="replace").read().splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        k, v = line.split("=", 1)
        v = v.strip().strip('"').strip("'")
        if not v:
            continue
        if k == "TAVILY_API_KEY" and not os.environ.get("TAVILY_API_KEY"):
            os.environ["TAVILY_API_KEY"] = v
        elif k == "HONCHO_API_KEY" and not os.environ.get("HONCHO_API_KEY"):
            HONCHO_API_KEY = v
        elif k == "HONCHO_BASE" and not os.environ.get("HONCHO_BASE"):
            HONCHO_BASE = v.rstrip("/")


_load_env()


def _check_identity() -> None:
    """Refuse to run if the pinned ids are edited into the stray workspace."""
    if WORKSPACE != "hermes" or PEER != "AHC" or AI_PEER != "alice_local":
        raise RuntimeError("Alice LOCAL Honcho identity must stay hermes / AHC / alice_local")
    if WORKSPACE == AI_PEER or WORKSPACE == "alice_local":
        raise RuntimeError("Alice LOCAL must not use a root workspace id of alice_local")


def _honcho_url(suffix: str) -> str:
    _check_identity()
    return f"{HONCHO_BASE.rstrip('/')}/v3/workspaces/{WORKSPACE}/{suffix.lstrip('/')}"


def _auth(req: urllib.request.Request) -> None:
    """Attach x-api-key only when one is configured (local Honcho needs none)."""
    if HONCHO_API_KEY:
        req.add_header("x-api-key", HONCHO_API_KEY)


def _text(args: dict, key: str) -> str:
    value = args.get(key, "")
    if value is None:
        return ""
    return str(value).strip()


def _raw_query(query: str, limit: int, distance: float) -> list:
    url = _honcho_url("conclusions/query")
    body = json.dumps({
        "query": query[:200],
        "top_k": limit,
        "distance": distance,
        "filters": {"observer": AI_PEER, "observed": PEER},
    }).encode()
    req = urllib.request.Request(url, data=body, method="POST")
    req.add_header("Content-Type", "application/json")
    _auth(req)
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read())


def honcho_search(args: dict) -> dict:
    query = _text(args, "query")
    limit = int(args.get("limit", 5) or 5)
    if not query:
        return {"error": "empty query"}
    try:
        results = _raw_query(query, limit, 0.4)
        return {"results": [
            {"content": c.get("content", ""), "created_at": c.get("created_at", "")}
            for c in results
        ]}
    except Exception as e:
        return {"error": str(e)}


def honcho_save(args: dict) -> dict:
    content = _text(args, "content")
    if not content:
        return {"error": "empty content"}
    try:
        # Dedup: skip if a near-identical conclusion already exists.
        existing = _raw_query(content[:200], 3, 0.3)
        for c in existing:
            if c.get("content", "").strip().lower() == content.strip().lower():
                return {"saved": False, "reason": "already exists"}
        url = _honcho_url("conclusions")
        body = json.dumps({
            "conclusions": [{
                "content": content,
                "observer_id": AI_PEER,
                "observed_id": PEER,
            }]
        }).encode()
        req = urllib.request.Request(url, data=body, method="POST")
        req.add_header("Content-Type", "application/json")
        _auth(req)
        with urllib.request.urlopen(req, timeout=30) as resp:
            return {"saved": True, "status": resp.status}
    except Exception as e:
        return {"error": str(e)}


def _forget_target(args: dict) -> tuple:
    """Web contract: content (exact fact) or query (search terms). Either is enough."""
    content = _text(args, "content")
    query = _text(args, "query") or content
    return query, content


def honcho_forget(args: dict) -> dict:
    query, match_content = _forget_target(args)
    if not query:
        return {"error": "empty query"}
    try:
        results = _raw_query(query, 5, 0.3)
        deleted = 0
        for c in results:
            content_text = c.get("content", "")
            if match_content and match_content.strip().lower() != content_text.strip().lower():
                continue
            cid = c.get("id", "")
            if not cid:
                continue
            url = _honcho_url(f"conclusions/{cid}")
            req = urllib.request.Request(url, method="DELETE")
            _auth(req)
            with urllib.request.urlopen(req, timeout=30):
                deleted += 1
        return {"deleted": deleted}
    except Exception as e:
        return {"error": str(e)}


def tool_time(_args: dict) -> dict:
    now = datetime.datetime.now().astimezone()
    zone = now.strftime("%Z").strip()
    clock = now.strftime("%Y-%m-%d %H:%M:%S")
    if zone:
        clock = f"{clock} {zone}"
    return {"time": clock, "iso": now.isoformat(timespec="seconds")}


def search(args: dict) -> dict:
    query = _text(args, "query")
    if not query:
        return {"error": "empty query"}
    tavily = os.environ.get("TAVILY_API_KEY", "")
    if not tavily:
        return {"error": "no TAVILY_API_KEY configured"}
    try:
        body = json.dumps({"query": query, "max_results": 5}).encode()
        req = urllib.request.Request(
            "https://api.tavily.com/search", data=body, method="POST")
        req.add_header("Content-Type", "application/json")
        req.add_header("Authorization", f"Bearer {tavily}")
        with urllib.request.urlopen(req, timeout=30) as resp:
            data = json.loads(resp.read())
        return {"results": [
            {"title": r.get("title", ""), "url": r.get("url", ""),
             "content": r.get("content", "")}
            for r in data.get("results", [])
        ]}
    except Exception as e:
        return {"error": str(e)}


def plan_call(tool: str, args: dict) -> dict:
    """Describe the call without contacting Honcho or the web. Used by tests."""
    if not isinstance(args, dict):
        args = {}
    if tool == "honcho_search":
        query = _text(args, "query")
        return {
            "tool": tool,
            "method": "POST",
            "url": _honcho_url("conclusions/query"),
            "workspace": WORKSPACE,
            "peer": PEER,
            "ai_peer": AI_PEER,
            "body": {
                "query": query[:200],
                "filters": {"observer": AI_PEER, "observed": PEER},
            },
        }
    if tool == "honcho_save":
        content = _text(args, "content")
        return {
            "tool": tool,
            "method": "POST",
            "url": _honcho_url("conclusions"),
            "workspace": WORKSPACE,
            "peer": PEER,
            "ai_peer": AI_PEER,
            "body": {
                "conclusions": [{
                    "content": content,
                    "observer_id": AI_PEER,
                    "observed_id": PEER,
                }]
            },
        }
    if tool == "honcho_forget":
        query, content = _forget_target(args)
        return {
            "tool": tool,
            "method": "POST",
            "url": _honcho_url("conclusions/query"),
            "workspace": WORKSPACE,
            "peer": PEER,
            "ai_peer": AI_PEER,
            "query": query,
            "content": content,
            "error": None if query else "empty query",
        }
    if tool == "time":
        return {"tool": "time", "honcho": False, "workspace": WORKSPACE, "ai_peer": AI_PEER}
    if tool == "search":
        return {"tool": "search", "honcho": False, "query": _text(args, "query")}
    return {"error": f"unknown tool: {tool}"}


TOOLS = {
    "honcho_search": honcho_search,
    "honcho_save": honcho_save,
    "honcho_forget": honcho_forget,
    "time": tool_time,
    "search": search,
}


def main() -> int:
    plan_only = len(sys.argv) > 1 and sys.argv[1] == "--plan"
    raw = sys.stdin.read()
    if not raw.strip():
        print(json.dumps({"error": "empty tool call"}))
        return 0
    try:
        call = json.loads(raw)
        tool = call.get("tool", "")
        args = call.get("args", {})
        if not isinstance(args, dict):
            args = {}
        if plan_only:
            print(json.dumps(plan_call(tool, args)))
            return 0
        handler = TOOLS.get(tool)
        if not handler:
            print(json.dumps({"error": f"unknown tool: {tool}"}))
            return 0
        result = handler(args)
        print(json.dumps(result))
    except Exception as e:
        print(json.dumps({"error": str(e)}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
