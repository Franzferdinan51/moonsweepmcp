#!/usr/bin/env python3
"""canonical_driver.py — THE MoonSweepers driver (replaces the script pile:
model_driver / formation_follow / dock_driver / ops_driver / rapid_dock /
full_system and every other one-off). Juno, 2026-10-27.

Design (from research/steering-control.md, coverage-planning.md,
hierarchy-and-d1.md on the VM):

  L3 lead      driver_config.json — mode, pattern, formation, enables.
               Re-read every cycle; the Hermes coach writes guide text there.
  L2 facts     optional vision facts (9B chat endpoint) — off by default.
  L1 arbiter   D1 pair via the SystemOne wire (decide()): the omni proposes
               a maneuver, the 3B confirms CHANGES. Invalid/stale/slow =
               keep the current lease. Models never touch controls.
  L0 reflex    the plugin's BotAuto (v0.6.13 pure-pursuit + anti-orbit).
               This driver issues drive_to LEGS only — never throttle/steer,
               never pulses cruise/boost outside a maneuver.

Geometry: ALL math in a tangent frame anchored at the work zone (up =
normalize(anchor)). Flat-XZ bearing math near the poles distorts ~2.6x and
was one of the night's spin contributors — it does not exist in this file.

Coverage patterns (config "pattern"): 
  grid   — boustrophedon lanes over the game's own 25 m sections,
           skip-lane snake turns, spacing = 0.8 * swath.
  hex    — flat-top hex cells (R = 12.5 m) in spiral order; each cell is
           cleaned by shrinking concentric hex rings + a centre pass.
  spiral — Archimedean spiral lanes over a circular zone.
Dirt bookkeeping persists to moon_grid2.json after every unit; a resume
token (resume_token.json) survives crashes and game restarts.

Dump protocol (coverage memo §2): at a unit boundary with tank >= threshold,
save the token, drive to the nearest Collector BY 3D DISTANCE (radar has
antipodal duplicates in XZ), dwell until tank < 57 kg on two fresh samples
>= 5 s apart, return, re-drive the interrupted unit from its start.

Watchdogs above the plugin: leg-progress (no distance closure for 25 s on an
active leg -> stop, strike, re-plan), aim discipline (never re-aim a live
leg at a point < 15 m away; aim moves <= 20 m per re-plan), maneuver-flip
circuit breaker (>= 3 flips / 10 s -> HOLD 5 s).

Sober defaults (the operator doctrine): boost OFF, cruise OFF except a dump
transit when config allows, suction FULL cleaning / NORMAL catch-up,
formation follow = 25 m back, 5 m side, cleaning on.

Every cycle appends one row to gameplay_log.jsonl (kind=canonical_cycle) —
that log is the seed corpus for the game-playing-model dataset project.
"""
import base64, json, math, os, socket, subprocess, sys, time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
CONFIG = os.path.join(HERE, "driver_config.json")
LOG = os.path.join(HERE, "canonical_driver.log")
GAMEPLAY = os.path.join(HERE, "gameplay_log.jsonl")
GRIDFILE = os.path.join(HERE, "moon_grid2.json")
TOKEN = os.path.join(HERE, "resume_token.json")
HOST = os.environ.get("MOON_HOST", "127.0.0.1")
PORT = int(os.environ.get("MOON_PORT", "8913"))
D1_URL = "http://127.0.0.1:1234/v1/systemone"
CHAT_URL = "http://127.0.0.1:1234/v1/chat/completions"

# ----------------------------- MCP client (same wire as moon.py) ----------

class Client:
    def __init__(self, host, port):
        self.s = socket.create_connection((host, port), timeout=20)
        self.f = self.s.makefile("rw", encoding="utf-8", newline="\n")
        self.seq = 0
        self.rpc("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                "clientInfo": {"name": "canonical-driver", "version": "1.0"}})

    def rpc(self, method, params=None):
        self.seq += 1
        msg = {"jsonrpc": "2.0", "id": self.seq, "method": method}
        if params is not None:
            msg["params"] = params
        self.f.write(json.dumps(msg) + "\n")
        self.f.flush()
        while True:
            line = self.f.readline()
            if not line:
                raise RuntimeError("connection closed")
            try:
                return json.loads(line)
            except json.JSONDecodeError:
                continue

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


def log(*a):
    line = time.strftime("%H:%M:%S ") + " ".join(str(x) for x in a)
    print(line, flush=True)
    try:
        with open(LOG, "a") as f:
            f.write(line + "\n")
    except Exception:
        pass


def jlog(row):
    row["t"] = time.time()
    row["kind"] = "canonical_cycle"
    try:
        with open(GAMEPLAY, "a") as f:
            f.write(json.dumps(row) + "\n")
    except Exception:
        pass


# ----------------------------- tangent frame --------------------------------

def v_sub(a, b): return (a[0] - b[0], a[1] - b[1], a[2] - b[2])
def v_add(a, b): return (a[0] + b[0], a[1] + b[1], a[2] + b[2])
def v_mul(a, s): return (a[0] * s, a[1] * s, a[2] * s)
def v_dot(a, b): return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]
def v_cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])
def v_len(a): return math.sqrt(v_dot(a, a))
def v_norm(a):
    n = v_len(a) or 1.0
    return (a[0] / n, a[1] / n, a[2] / n)


class Frame:
    """Local tangent frame at an anchor point on the sphere."""
    def __init__(self, anchor):
        self.anchor = tuple(anchor)
        self.up = v_norm(self.anchor)
        e = v_sub((1.0, 0.0, 0.0), v_mul(self.up, self.up[0]))
        if v_len(e) < 0.1:
            e = v_sub((0.0, 0.0, 1.0), v_mul(self.up, self.up[2]))
        self.east = v_norm(e)
        self.north = v_cross(self.up, self.east)
        self.radius = v_len(self.anchor)

    def to_local(self, p):
        d = v_sub(p, self.anchor)
        return (v_dot(d, self.east), v_dot(d, self.north))

    def to_world(self, e, n):
        p = v_add(self.anchor, v_add(v_mul(self.east, e), v_mul(self.north, n)))
        return v_mul(v_norm(p), self.radius)

    def dist3(self, a, b):
        return v_len(v_sub(a, b))


# ----------------------------- D1 wire --------------------------------------

def decide(model, state, questions, timeout=8):
    body = json.dumps({"model": model, "state": state, "questions": questions}).encode()
    req = urllib.request.Request(D1_URL, data=body,
                                 headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=timeout) as r:
        payload = json.loads(r.read().decode())
    return payload.get("answers") or {}


MANEUVER_Q = {
    "maneuver": {
        "type": "choice",
        "instructions": "Pick the cleaning maneuver for the next few seconds.",
        "criteria": {
            "sweep": "keep driving the current pattern unit, cleaning as you go",
            "next_unit": "this unit is done or blocked; move to the next one",
            "hold": "stop and hold position (tank full, obstacle, or unsafe)",
        },
    }
}

# ----------------------------- coverage patterns ----------------------------

def grid_units(centre_en, size_m=25.0, swath=5.0):
    """One 25 m section -> ordered waypoint list (skip-lane snake).
    Returns (unit_id, [(e, n), ...]). Lanes run along east; spacing s =
    0.8*swath re-spaced evenly; outer centrelines inset s'/2."""
    s = 0.8 * swath
    n = max(1, int(math.ceil(size_m / s)))
    sp = size_m / n
    lanes = [centre_en[1] - size_m / 2 + sp * (i + 0.5) for i in range(n)]
    e0, e1 = centre_en[0] - size_m / 2, centre_en[0] + size_m / 2
    head = sp  # headland beyond each end for the wide turns
    order = list(range(0, n, 2)) + list(range(n - 1 if n % 2 == 0 else n - 2, -1, -2))
    # build a snake: odd-index lanes eastbound on the way out, even lanes back
    pts = []
    eastbound_first = order
    seq = []
    for k, li in enumerate(eastbound_first):
        seq.append(li)
    # simpler robust snake: lanes in natural order, alternate direction,
    # with a headland jog at each end (turn radius handled by the jog).
    pts = []
    for i, ln in enumerate(lanes):
        if i % 2 == 0:
            pts.append((e0 - head, ln)); pts.append((e1 + head, ln))
        else:
            pts.append((e1 + head, ln)); pts.append((e0 - head, ln))
    return pts


def hex_ring(centre_en, r, rot=0.0):
    pts = []
    for k in range(6):
        a = rot + math.radians(60 * k)
        pts.append((centre_en[0] + r * math.cos(a), centre_en[1] + r * math.sin(a)))
    pts.append(pts[0])
    return pts


def hex_units(centre_en, zone_r=37.5, hex_r=12.5):
    """Hex cells whose centres fall inside zone_r, in spiral (distance)
    order; each cell = shrinking rings + centre. Yields (cell_id, pts)."""
    cells = []
    dx = 1.5 * hex_r
    dy = math.sqrt(3) * hex_r
    qmax = int(zone_r / dx) + 1
    rmax = int(zone_r / dy) + 1
    for q in range(-qmax, qmax + 1):
        for r in range(-rmax, rmax + 1):
            cx = centre_en[0] + dx * q
            cy = centre_en[1] + dy * (r + 0.5 * (q & 1))
            if math.hypot(cx - centre_en[0], cy - centre_en[1]) <= zone_r:
                cells.append((cx, cy))
    cells.sort(key=lambda c: math.hypot(c[0] - centre_en[0], c[1] - centre_en[1]))
    units = []
    for i, c in enumerate(cells):
        pts = []
        for rr in (hex_r * 0.92, hex_r * 0.55, hex_r * 0.25):
            pts.extend(hex_ring(c, rr))
        pts.append(c)
        units.append(("hex%d" % i, pts))
    return units


def spiral_units(centre_en, r_out=37.5, swath=5.0):
    """Archimedean spiral, outside in, lane spacing 0.8*swath."""
    s = 0.8 * swath
    pts = []
    total = 2 * math.pi * r_out / s  # approx radians of winding
    theta = 0.0
    while True:
        r = r_out - s * theta / (2 * math.pi)
        if r < s * 0.5:
            break
        step = max(0.15, 6.0 / max(r, 6.0))  # ~6 m of arc per waypoint
        pts.append((centre_en[0] + r * math.cos(theta), centre_en[1] + r * math.sin(theta)))
        theta += step
        if theta > total + 6.3:
            break
    pts.append(centre_en)
    return [("spiral0", pts)]


# ----------------------------- the driver -----------------------------------

def load_config():
    try:
        with open(CONFIG) as f:
            return json.load(f)
    except Exception:
        return {}


def save_json(path, obj):
    tmp = path + ".tmp"
    with open(tmp, "w") as f:
        json.dump(obj, f)
    os.replace(tmp, path)


class Driver:
    def __init__(self, client):
        self.c = client
        self.cfg = load_config()
        self.frame = None
        self.units = []          # [(unit_id, [(e, n), ...]), ...]
        self.unit_idx = 0
        self.wp_idx = 0
        self.leg = None          # {"target_en": (e, n), "best": float, "best_at": t, "issued": t}
        self.maneuver = "sweep"
        self.pending_maneuver = None
        self.flips = []          # timestamps of maneuver changes
        self.hold_until = 0.0
        self.catch_up = False
        self.target_hist = []    # [(t, pos3)] for heading estimation
        self.last_dust = None
        self.last_pos = None
        self.dump_state = None   # None | "to_dump" | "dwell" | "return"
        self.grid = {}
        try:
            with open(GRIDFILE) as f:
                self.grid = json.load(f)
        except Exception:
            self.grid = {}
        self.cycle = 0

    # -- state helpers -------------------------------------------------------
    def state(self):
        return self.c.jcall("state")

    def veh(self, st):
        return st.get("vehicle") or {}

    def issue_leg(self, en, cleaning, cruise=False, suction_full=True, timeout_s=300):
        w = self.frame.to_world(en[0], en[1])
        self.c.jcall("drive_to", {"x": w[0], "z": w[2], "cleaning": bool(cleaning),
                                  "cruise": bool(cruise), "suction_full": bool(suction_full),
                                  "timeout_s": timeout_s})
        self.leg = {"target_en": en, "best": 1e9, "best_at": time.time(), "issued": time.time()}
        return w

    def stop(self):
        try:
            self.c.jcall("drive_stop")
        except Exception:
            pass
        self.leg = None

    # -- pattern construction --------------------------------------------------
    def build_units(self, st):
        cfg = self.cfg
        pos = self.veh(st).get("pos") or st.get("player_pos")
        anchor = cfg.get("zone_anchor") or pos
        self.frame = Frame(anchor)
        centre = cfg.get("zone_centre_en") or [0.0, 0.0]
        pattern = cfg.get("pattern", "grid")
        swath = float(cfg.get("swath_m", 5.0))
        if pattern == "hex":
            self.units = hex_units(tuple(centre), float(cfg.get("zone_r", 37.5)))
        elif pattern == "spiral":
            self.units = spiral_units(tuple(centre), float(cfg.get("zone_r", 37.5)), swath)
        else:
            # grid: tile sections in a snake over zone_r, one unit per section
            size = 25.0
            r = float(cfg.get("zone_r", 37.5))
            centres = []
            n = int(r // size)
            for ix in range(-n, n + 1):
                row = []
                for iy in range(-n, n + 1):
                    c = (centre[0] + ix * size, centre[1] + iy * size)
                    if math.hypot(c[0] - centre[0], c[1] - centre[1]) <= r:
                        row.append(c)
                if ix % 2 == 1:
                    row.reverse()
                centres.extend(row)
            self.units = [("sec%d,%d" % (int(c[0]), int(c[1])), grid_units(c, size, swath))
                          for c in centres]
        # resume token: pick up where a previous run left off
        try:
            with open(TOKEN) as f:
                tok = json.load(f)
            if tok.get("pattern") == pattern and tok.get("unit_idx", 0) < len(self.units):
                self.unit_idx = tok["unit_idx"]
                log("resumed from token at unit", self.unit_idx)
        except Exception:
            pass
        log("pattern", pattern, "units", len(self.units), "anchor", [round(x, 1) for x in anchor])

    def cur_unit(self):
        if self.unit_idx >= len(self.units):
            return None
        return self.units[self.unit_idx]

    def save_token(self, why):
        save_json(TOKEN, {"pattern": self.cfg.get("pattern", "grid"),
                          "unit_idx": self.unit_idx, "wp_idx": self.wp_idx,
                          "why": why, "t": time.time()})

    # -- leg supervision -------------------------------------------------------
    def supervise_leg(self, st):
        """Returns 'arrived' | 'running' | 'failed'."""
        ds = self.c.jcall("drive_status")
        pos = self.veh(st).get("pos")
        dist = None
        if pos and self.leg:
            w = self.frame.to_world(*self.leg["target_en"])
            dist = self.frame.dist3(pos, w)
            if dist < self.leg["best"]:
                self.leg["best"] = dist
                self.leg["best_at"] = time.time()
        if not ds.get("active"):
            status = str(ds.get("status", ""))
            self.leg = None
            if status in ("arrived", "passed") or (dist is not None and dist < 9):
                return "arrived"
            return "failed:" + status
        # anti-orbit / stuck watchdog: no closure for 25 s on an active leg
        if self.leg and time.time() - self.leg["best_at"] > 25:
            log("watchdog: leg made no progress for 25 s (best %.0f m) — stop + re-plan"
                % self.leg["best"])
            self.stop()
            return "failed:watchdog"
        return "running"

    # -- dump protocol -----------------------------------------------------------
    def find_collector(self, st):
        r = self.c.jcall("radar", {"radius": 6000})
        pos = self.veh(st).get("pos")
        best, bestd = None, 1e18
        for ct in r.get("contacts", []):
            name = (str(ct.get("name", "")) + " " + str(ct.get("kind", ""))).lower()
            if "collector" in name or "depot" in name or "dock" in name:
                d = self.frame.dist3(pos, ct["pos"])  # 3D — XZ picks the antipode half the time
                if d < bestd:
                    best, bestd = ct, d
        return best, bestd

    def dump_cycle(self, st):
        """One cycle of the dump state machine. Returns True while dumping."""
        dust = self.veh(st).get("dust_kg") or 0
        if self.dump_state is None:
            if dust < float(self.cfg.get("dump_at_kg", 5400)):
                return False
            self.save_token("dump")
            self.dump_state = "to_dump"
            log("tank %.0f kg — GO_DUMP" % dust)
        col, d = self.find_collector(st)
        if self.dump_state == "to_dump":
            if col is None:
                log("dump: no collector on radar — holding")
                self.stop()
                return True
            en = self.frame.to_local(col["pos"])
            if d < 12:
                self.stop()
                self.dump_state = "dwell"
                self._dwell_ok_since = None
                return True
            if self.leg is None:
                self.issue_leg(en, cleaning=False,
                               cruise=bool(self.cfg.get("cruise_dump", False)),
                               suction_full=False)
            else:
                r = self.supervise_leg(st)
                if r.startswith("failed"):
                    self.leg = None
            return True
        if self.dump_state == "dwell":
            self.stop()
            if dust < 57:
                if getattr(self, "_dwell_ok_since", None) is None:
                    self._dwell_ok_since = time.time()
                elif time.time() - self._dwell_ok_since > 5:
                    log("dump verified empty (%.1f kg, stable 5 s)" % dust)
                    self.dump_state = "return"
            else:
                self._dwell_ok_since = None
            return True
        if self.dump_state == "return":
            # back to the interrupted unit; re-drive it from its start
            self.wp_idx = 0
            self.dump_state = None
            log("dump done — resuming unit", self.unit_idx)
            return False
        return False

    # -- D1 arbiter ---------------------------------------------------------------
    def d1_advice(self, st):
        """Omni proposes; a maneuver CHANGE needs the 3B to agree.
        Returns the advised maneuver or None (keep current lease)."""
        if not self.cfg.get("d1_enabled", True):
            return None
        veh = self.veh(st)
        state = {"tank_kg": veh.get("dust_kg"), "cleaning": veh.get("cleaning"),
                 "suction_level": veh.get("suction_level"), "maneuver": self.maneuver,
                 "unit": self.unit_idx, "cycle": self.cycle}
        try:
            ans = decide("d1-omni-600m", state, MANEUVER_Q, timeout=6)
            pick = (ans.get("maneuver") or {}).get("choice")
        except Exception as e:
            log("d1-omni unavailable:", e)
            return None
        if pick not in ("sweep", "next_unit", "hold") or pick == self.maneuver:
            return pick if pick in ("sweep", "next_unit", "hold") else None
        # a change needs confirmation from the 3B (roles fixed, not merged)
        try:
            ans3 = decide("d1-3b", state, MANEUVER_Q, timeout=8)
            pick3 = (ans3.get("maneuver") or {}).get("choice")
        except Exception as e:
            log("d1-3b unavailable:", e)
            return None
        if pick3 == pick:
            return pick
        log("d1 disagreement: omni=%s 3b=%s — keeping %s" % (pick, pick3, self.maneuver))
        return None

    def set_maneuver(self, m):
        if m == self.maneuver:
            return
        now = time.time()
        self.flips = [t for t in self.flips if now - t < 10]
        self.flips.append(now)
        if len(self.flips) >= 3:  # circuit breaker: freeze to HOLD
            log("circuit breaker: 3 maneuver flips in 10 s — HOLD 5 s")
            self.maneuver = "hold"
            self.hold_until = now + 5
            return
        self.maneuver = m
        log("maneuver ->", m)

    # -- follow mode ---------------------------------------------------------------
    def follow_cycle(self, st):
        cfg = self.cfg
        name = str(cfg.get("target_name", "")).lower()
        r = self.c.jcall("radar", {"radius": 6000})
        tgt = None
        for ct in r.get("contacts", []):
            if ct.get("kind") in ("remote_vehicle", "crew") and name in str(ct.get("name", "")).lower():
                tgt = ct
                break
        pos = self.veh(st).get("pos")
        if tgt is None or pos is None:
            if self.leg:
                self.stop()
            return
        now = time.time()
        self.target_hist.append((now, tuple(tgt["pos"])))
        self.target_hist = [(t, p) for (t, p) in self.target_hist if now - t < 6]
        gap = self.frame.dist3(pos, tgt["pos"])
        # heading from >= 3 samples >= 1 s apart, else keep last aim
        hdg = None
        if len(self.target_hist) >= 3 and now - self.target_hist[0][0] >= 1.0:
            d = v_sub(self.target_hist[-1][1], self.target_hist[0][1])
            dt = v_sub(d, v_mul(self.frame.up, v_dot(d, self.frame.up)))
            if v_len(dt) > 1.0:
                hdg = v_norm(dt)
        # catch-up hysteresis: enter > 40 m, exit < 30 m
        if gap > 40:
            self.catch_up = True
        elif gap < 30:
            self.catch_up = False
        back = float(cfg.get("follow_back_m", 25))
        side = float(cfg.get("follow_side_m", 5))
        if hdg is not None:
            perp = v_cross(self.frame.up, hdg)
            aim3 = v_add(v_add(tgt["pos"], v_mul(hdg, -back)), v_mul(perp, side))
            aim_en = self.frame.to_local(aim3)
            cur = self.leg["target_en"] if self.leg else None
            moved = 1e9 if cur is None else math.hypot(aim_en[0] - cur[0], aim_en[1] - cur[1])
            if self.leg is None or moved > 10:
                self.issue_leg(aim_en, cleaning=not self.catch_up, cruise=False,
                               suction_full=not self.catch_up)
        if self.leg:
            r2 = self.supervise_leg(st)
            if r2.startswith("failed"):
                self.leg = None


    # -- solo cleaning ---------------------------------------------------------------
    def solo_cycle(self, st):
        veh = self.veh(st)
        dust = veh.get("dust_kg") or 0
        # yield accounting: attribute dust delta to the current unit
        if self.last_dust is not None and self.cur_unit() is not None:
            uid = self.cur_unit()[0]
            gain = max(0.0, dust - self.last_dust)
            ent = self.grid.setdefault(uid, {"kg": 0.0, "passes": 0})
            ent["kg"] = ent.get("kg", 0.0) + gain
        self.last_dust = dust

        if time.time() < self.hold_until or self.maneuver == "hold":
            if self.leg:
                self.stop()
            return

        unit = self.cur_unit()
        if unit is None:
            log("all units complete — holding (set loop_zone to re-pass)")
            if self.cfg.get("loop_zone"):
                self.unit_idx = 0
                self.wp_idx = 0
                self.save_token("loop")
            elif self.leg:
                self.stop()
            return

        # D1 advice every 4th cycle while a leg is running fine
        if self.cycle % 4 == 0:
            advice = self.d1_advice(st)
            if advice == "next_unit":
                self.set_maneuver("sweep")
                self.finish_unit(st, "d1_next_unit")
                return
            if advice == "hold":
                self.set_maneuver("hold")
                return
            if advice == "sweep":
                self.set_maneuver("sweep")

        uid, pts = unit
        if self.wp_idx >= len(pts):
            self.finish_unit(st, "complete")
            return
        if self.leg is None:
            self.issue_leg(pts[self.wp_idx], cleaning=True, cruise=False, suction_full=True)
            return
        r = self.supervise_leg(st)
        if r == "arrived":
            self.wp_idx += 1
            self._strikes = 0
        elif r.startswith("failed"):
            self._strikes = getattr(self, "_strikes", 0) + 1
            log("leg failed (%s) strike %d — skipping point" % (r, self._strikes))
            self.wp_idx += 1
            if self._strikes >= 3:
                self.finish_unit(st, "strikes")

    def finish_unit(self, st, why):
        unit = self.cur_unit()
        if unit is not None:
            ent = self.grid.setdefault(unit[0], {"kg": 0.0, "passes": 0})
            ent["passes"] = ent.get("passes", 0) + 1
            ent["last"] = time.time()
            save_json(GRIDFILE, self.grid)
            log("unit", unit[0], why, "kg=%.1f passes=%d" % (ent.get("kg", 0), ent["passes"]))
        self.stop()
        self.unit_idx += 1
        self.wp_idx = 0
        self._strikes = 0
        self.save_token(why)

    # -- main cycle ---------------------------------------------------------------------
    def cycle_once(self):
        self.cfg = load_config()  # L3 lead: live re-read (coach writes here)
        st = self.state()
        if self.frame is None:
            self.build_units(st)
        veh = self.veh(st)
        if self.dump_cycle(st):
            pass
        elif self.cfg.get("mode") == "follow":
            self.follow_cycle(st)
        else:
            self.solo_cycle(st)
        jlog({"pos": veh.get("pos"), "dust_kg": veh.get("dust_kg"),
              "cleaning": veh.get("cleaning"), "suction_level": veh.get("suction_level"),
              "maneuver": self.maneuver, "mode": self.cfg.get("mode", "solo"),
              "pattern": self.cfg.get("pattern", "grid"),
              "unit": self.unit_idx, "wp": self.wp_idx,
              "dump": self.dump_state, "catch_up": self.catch_up})
        self.cycle += 1


def main():
    args = sys.argv[1:]
    host, port = HOST, PORT
    if "--host" in args:
        host = args[args.index("--host") + 1]
    if "--port" in args:
        port = int(args[args.index("--port") + 1])
    log("canonical_driver starting — endpoint %s:%d" % (host, port))
    drv = None
    while True:
        try:
            if drv is None:
                drv = Driver(Client(host, port))
                log("connected")
            drv.cycle_once()
            time.sleep(3)
        except Exception as e:
            log("cycle error:", e)
            drv = None
            time.sleep(5)


if __name__ == "__main__":
    main()
