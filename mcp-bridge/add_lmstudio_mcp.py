#!/usr/bin/env python3
"""Merge the MoonSweepers MCP entry into an LM Studio mcp.json (idempotent).
Usage: add_lmstudio_mcp.py <mcp.json path> <command> <arg1> [arg2 ...]
Prints only OK + server names count; never prints other entries' contents."""
import json, sys

path = sys.argv[1]
command = sys.argv[2]
args = sys.argv[3:]

with open(path, "r", encoding="utf-8") as f:
    data = json.load(f)

servers = data.setdefault("mcpServers", {})
servers["moonsweepers"] = {"command": command, "args": args}

with open(path, "w", encoding="utf-8") as f:
    json.dump(data, f, indent=2)
    f.write("\n")

# verify round-trip
with open(path, "r", encoding="utf-8") as f:
    check = json.load(f)
assert check["mcpServers"]["moonsweepers"]["command"] == command
print("OK moonsweepers added; total servers:", len(check["mcpServers"]))
