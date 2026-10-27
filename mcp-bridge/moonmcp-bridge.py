#!/usr/bin/env python3
"""MoonSweepers MCP stdio bridge.

MCP clients (Hermes, Claude Code, Muse) launch this as a stdio server.
It forwards line-delimited JSON-RPC to the MoonSweepers game mod's TCP MCP
(the BepInEx plugin in the Steam game, default 127.0.0.1:8913) and back.

It also repairs the plugin's known initialize-reply bug (v0.4.0 emits a raw
newline inside the instructions string, which strict JSON parsers reject):
any reply line that fails to parse gets accumulated and passed through a
string-aware escaper that escapes raw CR/LF occurring inside JSON strings.

Config (env or argv):
  MOONMCP_HOST / --host   default 127.0.0.1
  MOONMCP_PORT / --port   default 8913
For a game running on another tailnet machine, point this at the forwarder
(e.g. --host <game-pc-ip> --port 8914).
"""
import os
import socket
import sys
import threading

HOST = os.environ.get("MOONMCP_HOST", "127.0.0.1")
PORT = int(os.environ.get("MOONMCP_PORT", "8913"))
args = sys.argv[1:]
for i, a in enumerate(args):
    if a == "--host" and i + 1 < len(args):
        HOST = args[i + 1]
    if a == "--port" and i + 1 < len(args):
        PORT = int(args[i + 1])


def escape_raw_newlines_in_strings(text):
    """Escape literal CR/LF that occur inside JSON string literals."""
    out = []
    in_str = False
    esc = False
    for ch in text:
        if esc:
            out.append(ch)
            esc = False
            continue
        if ch == "\\" and in_str:
            out.append(ch)
            esc = True
            continue
        if ch == '"':
            in_str = not in_str
            out.append(ch)
            continue
        if in_str and ch == "\n":
            out.append("\\n")
            continue
        if in_str and ch == "\r":
            out.append("\\r")
            continue
        out.append(ch)
    return "".join(out)


def parses(text):
    import json
    try:
        json.loads(text)
        return True
    except Exception:
        return False


def main():
    try:
        sock = socket.create_connection((HOST, PORT), timeout=15)
    except Exception as e:
        sys.stderr.write("moonmcp-bridge: cannot reach game MCP at %s:%d (%s)\n" % (HOST, PORT, e))
        return 1
    sock.settimeout(None)
    raw = sock.makefile("rb")
    stop = threading.Event()

    def to_client():
        # game -> stdout, with framing repair
        pending = ""
        while not stop.is_set():
            line = raw.readline()
            if not line:
                stop.set()
                return
            text = line.decode("utf-8", "replace").rstrip("\r\n")
            if not text:
                continue
            candidate = pending + ("\n" + text if pending else text)
            if parses(candidate):
                sys.stdout.write(candidate + "\n")
                sys.stdout.flush()
                pending = ""
            else:
                repaired = escape_raw_newlines_in_strings(candidate)
                if parses(repaired):
                    sys.stdout.write(repaired + "\n")
                    sys.stdout.flush()
                    pending = ""
                else:
                    pending = candidate  # keep accumulating (multi-line reply)

    t = threading.Thread(target=to_client, daemon=True)
    t.start()
    try:
        for line in sys.stdin:
            if stop.is_set():
                break
            if not line.strip():
                continue
            sock.sendall(line.encode("utf-8"))
    except Exception:
        pass
    t.join(timeout=2.0)  # let in-flight replies land before exiting on stdin EOF
    stop.set()
    try:
        sock.close()
    except Exception:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
