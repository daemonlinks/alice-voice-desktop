#!/usr/bin/env python
"""Alice LOCAL tool bridge for the native app.

Reads a tool-call JSON from stdin, executes it against Alice LOCAL's OWN
Honcho workspace (alice_local) and the web search API, and prints the result
JSON to stdout. Mirrors the web relay's tool layer (honcho_search/save/forget
+ search) but scoped to Alice LOCAL's private workspace, not alice_voice's.

Usage:
    echo '{"tool":"honcho_search","args":{"query":"...","limit":5}}' | python alice_local_tools.py
"""
import json
import os
import sys
import urllib.request

HONCHO_BASE = os.environ.get("HONCHO_BASE", "http://localhost:8001")
# Local self-hosted Honcho runs with AUTH_USE_AUTH=false, so no key is needed.
# HONCHO_API_KEY is optional: only sent if set (env or the AliceVoice .env),
# e.g. if auth is ever enabled on the server. Never hardcode a key here.
HONCHO_API_KEY = os.environ.get("HONCHO_API_KEY", "")
HONCHO_WORKSPACE = os.environ.get("HONCHO_WORKSPACE", "alice_local")
HONCHO_PEER = os.environ.get("HONCHO_PEER", "AHC")
HONCHO_AI_PEER = os.environ.get("HONCHO_AI_PEER", "alice_local")


def _load_env() -> None:
    """Load TAVILY_API_KEY, HONCHO_BASE and optional HONCHO_API_KEY from the AliceVoice .env file."""
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


def _auth(req: urllib.request.Request) -> None:
    """Attach x-api-key only when one is configured (local Honcho needs none)."""
    if HONCHO_API_KEY:
        req.add_header("x-api-key", HONCHO_API_KEY)


def _raw_query(query: str, limit: int, distance: float) -> list:
    url = f"{HONCHO_BASE}/v3/workspaces/{HONCHO_WORKSPACE}/conclusions/query"
    body = json.dumps({
        "query": query[:200],
        "top_k": limit,
        "distance": distance,
        "filters": {"observer": HONCHO_AI_PEER, "observed": HONCHO_PEER},
    }).encode()
    req = urllib.request.Request(url, data=body, method="POST")
    req.add_header("Content-Type", "application/json")
    _auth(req)
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read())


def honcho_search(args: dict) -> dict:
    query = args.get("query", "")
    limit = int(args.get("limit", 5))
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
    content = args.get("content", "")
    if not content:
        return {"error": "empty content"}
    try:
        # Dedup: skip if a near-identical conclusion already exists.
        existing = _raw_query(content[:200], 3, 0.3)
        for c in existing:
            if c.get("content", "").strip().lower() == content.strip().lower():
                return {"saved": False, "reason": "already exists"}
        url = f"{HONCHO_BASE}/v3/workspaces/{HONCHO_WORKSPACE}/conclusions"
        body = json.dumps({
            "conclusions": [{
                "content": content,
                "observer_id": HONCHO_AI_PEER,
                "observed_id": HONCHO_PEER,
            }]
        }).encode()
        req = urllib.request.Request(url, data=body, method="POST")
        req.add_header("Content-Type", "application/json")
        _auth(req)
        with urllib.request.urlopen(req, timeout=30) as resp:
            return {"saved": True, "status": resp.status}
    except Exception as e:
        return {"error": str(e)}


def honcho_forget(args: dict) -> dict:
    query = args.get("query", "")
    match_content = args.get("content", "")
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
            url = f"{HONCHO_BASE}/v3/workspaces/{HONCHO_WORKSPACE}/conclusions/{cid}"
            req = urllib.request.Request(url, method="DELETE")
            _auth(req)
            with urllib.request.urlopen(req, timeout=30):
                deleted += 1
        return {"deleted": deleted}
    except Exception as e:
        return {"error": str(e)}


def search(args: dict) -> dict:
    query = args.get("query", "")
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


TOOLS = {
    "honcho_search": honcho_search,
    "honcho_save": honcho_save,
    "honcho_forget": honcho_forget,
    "search": search,
}


def main() -> int:
    raw = sys.stdin.read()
    if not raw.strip():
        print(json.dumps({"error": "empty tool call"}))
        return 0
    try:
        call = json.loads(raw)
        tool = call.get("tool", "")
        args = call.get("args", {})
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
