#!/usr/bin/env python3
"""moon.py — fast CLI for the MoonSweepers game MCP (v0.6.3, 26 tools).

Runs anywhere that can reach the plugin's TCP endpoint (default
127.0.0.1:8913, the game machine itself; pass --host/--port first for a
remote endpoint, e.g. a Tailscale Serve forward). Control loops (drive-to
--watch, follow) poll LOCALLY at a few seconds per step — no per-call SSH.

Usage:
  moon.py tools
  moon.py state | vehicle [action] | sense | crew | radar [radius]
  moon.py call <tool> '<json-args>'
  moon.py shot [out.png]
  moon.py drive-to X Z [--cleaning] [--cruise] [--suction] [--timeout 1500] [--watch]
  moon.py follow-veh <name-or-steamid-substr> [--cleaning] [--cruise]
  moon.py follow-crew <name-substr> [--cleaning] [--cruise]
"""
import socket, json, sys, time, base64

HOST, PORT = "127.0.0.1", 8913


class Client:
    def __init__(self, host, port):
        self.host, self.port = host, port
        self.seq = 0
        self.s = None
        self.f = None
        self._connect()

    def _connect(self):
        self.s = socket.create_connection((self.host, self.port), timeout=20)
        self.s.settimeout(15)
        self.f = self.s.makefile("rw", encoding="utf-8", newline="\n")
        self._rpc_once("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                      "clientInfo": {"name": "moon-cli", "version": "1.0"}})

    def close(self):
        for obj in (self.f, self.s):
            try:
                if obj is not None:
                    obj.close()
            except Exception:
                pass
        self.f = None
        self.s = None

    def reconnect(self):
        """Drop the current socket and re-initialize once (audit H-12:
        a dropped bridge socket used to be forever)."""
        self.close()
        self._connect()

    def rpc(self, method, params=None):
        if self.f is None:
            self._connect()  # previous call declared this socket dead
        try:
            return self._rpc_once(method, params)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass  # socket is dead: fall through to the reconnect-once below
        except RuntimeError as e:
            if "connection closed" not in str(e):
                raise
        except TimeoutError:
            # Do NOT resend the timed-out request: a slow-but-alive plugin
            # may still execute it, and a blind retry could duplicate a
            # side-effecting call (audit C-01). Do discard the socket so
            # the NEXT call reconnects instead of reusing a dead one
            # forever (audit H-12), then surface the timeout.
            self.close()
            raise
        # Reconnect ONCE and retry the request ONCE.
        self.reconnect()
        return self._rpc_once(method, params)

    def _rpc_once(self, method, params=None):
        self.seq += 1
        msg = {"jsonrpc": "2.0", "id": self.seq, "method": method}
        if params is not None:
            msg["params"] = params
        self.f.write(json.dumps(msg) + "\n")
        self.f.flush()
        deadline = time.time() + 30
        while True:
            if time.time() > deadline:
                raise TimeoutError("rpc %s: no response within 30 s" % method)
            line = self.f.readline()
            if not line:
                raise RuntimeError("connection closed")
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue
            if isinstance(obj, dict) and obj.get("id") == self.seq:
                return obj

    def call(self, name, args=None):
        r = self.rpc("tools/call", {"name": name, "arguments": args or {}})
        res = r.get("result", {})
        blk = (res.get("content") or [{}])[0]
        if blk.get("type") == "image":
            return ("IMAGE", blk["data"])
        return ("TEXT", ("ERR " if res.get("isError") else "") + blk.get("text", ""))

    def jcall(self, name, args=None):
        t, v = self.call(name, args)
        if t != "TEXT":
            raise RuntimeError("unexpected image result")
        return json.loads(v)


def watch_drive(c, budget_s=1500):
    start = time.time()
    last = None
    while time.time() - start < budget_s:
        ds = c.jcall("drive_status")
        line = "dist=%s status=%s cleaning=%s recoveries=%s" % (
            ds.get("dist_m"), ds.get("status"), ds.get("cleaning"), ds.get("recoveries"))
        if line != last:
            print(line, flush=True)
            last = line
        if not ds.get("active"):
            print("DRIVE END:", ds.get("status"), flush=True)
            return ds
        time.sleep(5)
    print("WATCH BUDGET END", flush=True)
    return last


def find_contact(c, kind, needle, radius=6000):
    r = c.jcall("radar", {"radius": radius})
    for ct in r.get("contacts", []):
        if ct.get("kind") == kind and needle.lower() in str(ct.get("name", "")).lower():
            return ct
    return None


def follow(c, kind, needle, opts):
    print("FOLLOW %s '%s' — Ctrl-C to stop" % (kind, needle), flush=True)
    lost = 0
    arrived = 0
    while True:
        ct = find_contact(c, kind, needle)
        if ct is None:
            lost += 1
            print("target not on radar (%d)" % lost, flush=True)
            if lost >= 6:
                print("FOLLOW END: target lost", flush=True)
                return
            time.sleep(6)
            continue
        lost = 0
        x, z = ct["pos"][0], ct["pos"][2]
        ds = c.jcall("drive_status")
        # Re-target when the drive is idle/arrived or the target moved away.
        if not ds.get("active") or (ds.get("dist_m") or 0) > 25:
            print("target %s at %.0f m — driving" % (ct.get("name"), ct.get("dist", 0)), flush=True)
            c.jcall("drive_to", {"x": x, "z": z, "cleaning": opts.get("cleaning", False),
                                 "cruise": opts.get("cruise", False), "timeout_s": 300})
            arrived = 0
        else:
            if (ds.get("dist_m") or 99) <= 8:
                arrived += 1
                if arrived >= 3:
                    print("FOLLOW: holding near target", flush=True)
            time.sleep(6)


def main(argv):
    global HOST, PORT
    args = list(argv)
    while args and args[0].startswith("--"):
        if args[0] == "--host":
            HOST = args[1]; args = args[2:]
        elif args[0] == "--port":
            PORT = int(args[1]); args = args[2:]
        else:
            break
    if not args:
        print(__doc__)
        return 1
    cmd = args[0]
    c = Client(HOST, PORT)

    if cmd == "tools":
        r = c.rpc("tools/list")
        for t in r["result"]["tools"]:
            print(t["name"])
    elif cmd in ("state", "sense", "crew", "drive_status", "walk_status"):
        t, v = c.call(cmd)
        print(v)
    elif cmd == "vehicle":
        t, v = c.call("vehicle", {"action": args[1]} if len(args) > 1 else {})
        print(v)
    elif cmd == "radar":
        t, v = c.call("radar", {"radius": float(args[1]) if len(args) > 1 else 300})
        print(v)
    elif cmd == "call":
        t, v = c.call(args[1], json.loads(args[2]) if len(args) > 2 else {})
        print(v)
    elif cmd == "shot":
        # scale 1.0 = the plugin's direct-capture path. The Blit/rescale
        # path (any scale in 0.01-0.99) returned a frozen frame on
        # 2026-10-26; downscale locally with sips instead.
        t, v = c.call("screenshot", {"scale": 1.0})
        out = args[1] if len(args) > 1 else "moon-shot.png"
        if t == "IMAGE":
            open(out + ".full.png", "wb").write(base64.b64decode(v))
            import subprocess as _sp
            _sp.run(["sips", "-Z", "1600", out + ".full.png", "--out", out],
                    capture_output=True)
            print("saved", out)
        else:
            print(v)
    elif cmd == "drive-to":
        x, z = float(args[1]), float(args[2])
        rest = args[3:]
        res = c.jcall("drive_to", {"x": x, "z": z,
                                   "cleaning": "--cleaning" in rest,
                                   "cruise": "--cruise" in rest,
                                   "suction_full": "--suction" in rest,
                                   "timeout_s": 1500})
        print(res, flush=True)
        if "--no-watch" not in rest:
            watch_drive(c)
    elif cmd in ("follow-veh", "follow-crew"):
        kind = "vehicle" if cmd == "follow-veh" else "crew"
        follow(c, kind, args[1], {"cleaning": "--cleaning" in args, "cruise": "--cruise" in args})
    else:
        print("unknown command:", cmd)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
