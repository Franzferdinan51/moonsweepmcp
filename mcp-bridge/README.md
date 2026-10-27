# mcp-bridge

Agent-side tooling for the MoonSweepers MCP plugin (see the top-level
README for the full install).

- **`moonmcp-bridge.py`** — stdio ↔ TCP bridge. MCP clients launch it as a
  stdio server; it forwards line-delimited JSON-RPC to the game plugin
  (default `127.0.0.1:8913`). Configure with `--host` / `--port` or the
  `MOONMCP_HOST` / `MOONMCP_PORT` environment variables — e.g. to reach a
  game on another machine through a Tailscale Serve forward. It also
  repairs a known plugin quirk: the `initialize` reply's instructions
  string can contain raw newlines that strict JSON parsers reject; the
  bridge re-escapes them string-aware.

- **`moon.py`** — fast CLI over the same wire (`tools`, `state`, `vehicle`,
  `sense`, `crew`, `radar`, `shot`, `call`, `drive-to --watch`,
  `follow-veh`, `follow-crew`). Default endpoint is the game machine's
  loopback; pass `--host` / `--port` first to aim it elsewhere.

- **`canonical_driver.py`** — the autonomous driver (coverage patterns,
  dirt map, dump-and-resume, formation follow, optional D1 decision-model
  arbitration through LM Studio's `/v1/systemone` endpoint). Copy
  `driver_config.example.json` to `driver_config.json` and edit the guide
  and toggles before running it.

- **`add_lmstudio_mcp.py`** — idempotently merge the `moonsweepers` entry
  into an LM Studio `mcp.json`:

  ```bash
  python3 add_lmstudio_mcp.py ~/.lmstudio/mcp.json python3 /path/to/moonmcp-bridge.py
  ```
