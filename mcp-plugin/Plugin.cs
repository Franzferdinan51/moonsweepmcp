// MoonSweepersMcp — a BepInEx IL2CPP plugin that lets AI agents play the
// real Steam game as crew, through an MCP server (line-delimited JSON-RPC
// over TCP 127.0.0.1:8913). All game API calls are marshalled onto the
// Unity main thread via an injected MonoBehaviour's Update loop.
//
// Tools (v1): state, host_info, teleport, face, sweep, equip, deposit.
using System;
using System.Reflection;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MoonCleaners;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MoonSweepersMcp
{
    [BepInPlugin("duckets.moonsweepers.mcp", "MoonSweepers MCP", "0.6.13")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource Log;
        public const int Port = 8913;

        public override void Load()
        {
            Log = base.Log;
            Log.LogInfo("MoonSweepers MCP v0.6.13 loading — MCP on 127.0.0.1:" + Port);
            // NOTE: no ClassInjector here — Il2CppInterop 1.5.3 mis-reads class
            // flags on this Unity 6000.6 build (every class reports as a value
            // type), so type injection is broken. Main-thread work is pumped
            // through Harmony postfixes on the game's own Update methods.
            var harmony = new Harmony("duckets.moonsweepers.mcp");
            var patched = 0;
            foreach (var (type, method) in new[]
            {
                (typeof(MoonCrewSession), "Update"),
                (typeof(MoonWorld), "Update"),
            })
            {
                var m = AccessTools.Method(type, method);
                if (m == null) { Log.LogWarning("no method " + type.Name + "." + method); continue; }
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(MainThreadPump), nameof(MainThreadPump.Drain)));
                patched++;
                Log.LogInfo("pumped main thread via " + type.Name + "." + method);
            }
            if (patched == 0) Log.LogError("no Update pump could be patched — tools will time out");
            // Bot input injection (v0.6): override the game's own input getters
            // while a bot drive is active, so the character/vehicle moves with
            // the game's own physics — no teleporting, no ClassInjector.
            foreach (var (type, method, post) in new[]
            {
                (typeof(MoonInput), "get_LeftStick", nameof(BotInput.LeftStickPostfix)),
                (typeof(MoonInput), "get_Throttle", nameof(BotInput.ThrottlePostfix)),
                (typeof(MoonInput), "get_Steer", nameof(BotInput.SteerPostfix)),
                (typeof(MoonInput), "get_SuctionStep", nameof(BotInput.SuctionStepPostfix)),
            })
            {
                var m = AccessTools.Method(type, method);
                if (m == null) { Log.LogWarning("no input method " + type.Name + "." + method); continue; }
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(BotInput), post));
                Log.LogInfo("bot input hook: " + type.Name + "." + method);
            }
            try
            {
                var bindPost = new HarmonyMethod(typeof(BotInput), nameof(BotInput.BindingBoolPostfix));
                foreach (var mName in new[] { "get_Pressed", "get_KeyPressed", "get_Held" })
                {
                    var m = AccessTools.Method(typeof(MoonInput.Binding), mName);
                    if (m != null) { harmony.Patch(m, postfix: bindPost); Log.LogInfo("bot input hook: MoonInput.Binding." + mName); }
                    else Log.LogWarning("no binding method MoonInput.Binding." + mName);
                }
            }
            catch (Exception e) { Log.LogWarning("binding hook failed: " + e.Message); }
            try
            {
                MethodInfo rvGen = null;
                foreach (var cand in typeof(InputAction).GetMethods())
                {
                    if (cand.Name == "ReadValue" && cand.IsGenericMethodDefinition && cand.GetParameters().Length == 0) { rvGen = cand; break; }
                }
                if (rvGen != null)
                {
                    var rv = rvGen.MakeGenericMethod(typeof(Vector2));
                    harmony.Patch(rv, postfix: new HarmonyMethod(typeof(BotInput), nameof(BotInput.MoveReadPostfix)));
                    Log.LogInfo("bot input hook: InputAction.ReadValue<Vector2>");
                }
                else Log.LogWarning("no InputAction.ReadValue generic");
                var wp = AccessTools.Method(typeof(InputAction), "WasPressedThisFrame");
                if (wp != null)
                {
                    harmony.Patch(wp, postfix: new HarmonyMethod(typeof(BotInput), nameof(BotInput.InteractPressedPostfix)));
                    Log.LogInfo("bot input hook: InputAction.WasPressedThisFrame");
                }
                else Log.LogWarning("no InputAction.WasPressedThisFrame");
            }
            catch (Exception e) { Log.LogWarning("inputaction hook failed: " + e.Message); }
            var server = new McpServer();
            var thread = new Thread(server.Run) { IsBackground = true, Name = "MoonMcpTcp" };
            thread.Start();
            Log.LogInfo("MoonSweepers MCP ready.");
        }
    }

    // Bot input injection state. The Harmony postfixes below run inside the
    // game's own input polling (main thread); they only override while a bot
    // drive is active. Vehicle overrides are time-boxed so a dead agent can
    // never leave the throttle stuck open.
    public static class BotInput
    {
        public static volatile bool WalkOverride;
        public static Vector2 Stick;
        public static volatile bool VehicleOverride;
        public static float ThrottleVal;
        public static float SteerVal;
        public static DateTime VehicleUntilUtc = DateTime.MinValue;
        public static int SuctionStepVal;
        public static DateTime SuctionStepUntilUtc = DateTime.MinValue;
        public static DateTime CruisePulseUntilUtc = DateTime.MinValue;
        public static DateTime BoostPulseUntilUtc = DateTime.MinValue;

        public static void PulseSuctionStep(int steps)
        {
            SuctionStepVal = steps;
            SuctionStepUntilUtc = DateTime.UtcNow.AddSeconds(0.3);
        }

        public static void PulseCruise()
        {
            CruisePulseUntilUtc = DateTime.UtcNow.AddSeconds(0.45);
        }

        public static void PulseBoost()
        {
            BoostPulseUntilUtc = DateTime.UtcNow.AddSeconds(0.45);
        }

        // v0.6.3: the walker (MoonWalker) moves from a Unity InputAction, not
        // MoonInput.LeftStick. We cache the native pointers of the walker's
        // own move/interact actions and override their reads while walking.
        public static IntPtr MoveActionPtr = IntPtr.Zero;
        public static IntPtr InteractActionPtr = IntPtr.Zero;
        public static DateTime InteractPulseUntilUtc = DateTime.MinValue;

        public static void PulseInteract()
        {
            InteractPulseUntilUtc = DateTime.UtcNow.AddSeconds(0.6);
        }

        public static void RefreshActionPointers()
        {
            if (MoveActionPtr != IntPtr.Zero && InteractActionPtr != IntPtr.Zero) return;
            try
            {
                var w = GameTools.WalkerPub();
                if (w == null) return;
                if (MoveActionPtr == IntPtr.Zero)
                {
                    var f = AccessTools.Field(typeof(MoonWalker), "move");
                    if (f != null && f.GetValue(w) is InputAction a) MoveActionPtr = a.Pointer;
                }
                if (InteractActionPtr == IntPtr.Zero)
                {
                    var f = AccessTools.Field(typeof(MoonWalker), "interact");
                    if (f != null && f.GetValue(w) is InputAction a) InteractActionPtr = a.Pointer;
                }
            }
            catch { }
        }

        public static void MoveReadPostfix(InputAction __instance, ref Vector2 __result)
        {
            try
            {
                if (!WalkOverride || __instance == null) return;
                if (MoveActionPtr != IntPtr.Zero)
                {
                    if (__instance.Pointer == MoveActionPtr) __result = Stick;
                }
                else if (string.Equals(__instance.name, "move", StringComparison.OrdinalIgnoreCase))
                {
                    __result = Stick;
                }
            }
            catch { }
        }

        public static void InteractPressedPostfix(InputAction __instance, ref bool __result)
        {
            try
            {
                if (__result || DateTime.UtcNow > InteractPulseUntilUtc || __instance == null) return;
                if (InteractActionPtr != IntPtr.Zero)
                {
                    if (__instance.Pointer == InteractActionPtr) __result = true;
                }
                else if (string.Equals(__instance.name, "interact", StringComparison.OrdinalIgnoreCase))
                {
                    __result = true;
                }
            }
            catch { }
        }

        public static void SuctionStepPostfix(ref int __result)
        {
            if (DateTime.UtcNow <= SuctionStepUntilUtc && SuctionStepVal != 0)
                __result += SuctionStepVal;
        }

        // For MoonInput.Binding getters: while a cruise pulse is armed, the
        // Cruise binding (identified by native pointer) reads as pressed.
        public static void BindingBoolPostfix(Il2CppSystem.Object __instance, ref bool __result)
        {
            try
            {
                if (__result || __instance == null) return;
                if (DateTime.UtcNow <= CruisePulseUntilUtc)
                {
                    var cruise = MoonInput.Cruise;
                    if (cruise != null && __instance.Pointer == cruise.Pointer) { __result = true; return; }
                }
                if (DateTime.UtcNow <= BoostPulseUntilUtc)
                {
                    var boost = MoonInput.Boost;
                    if (boost != null && __instance.Pointer == boost.Pointer) __result = true;
                }
            }
            catch { }
        }

        public static void LeftStickPostfix(ref Vector2 __result)
        {
            if (WalkOverride) __result = Stick;
        }
        public static void ThrottlePostfix(ref float __result)
        {
            if (VehicleOverride && DateTime.UtcNow <= VehicleUntilUtc) __result = ThrottleVal;
        }
        public static void SteerPostfix(ref float __result)
        {
            if (VehicleOverride && DateTime.UtcNow <= VehicleUntilUtc) __result = SteerVal;
        }
        public static void StopVehicle()
        {
            VehicleOverride = false; ThrottleVal = 0f; SteerVal = 0f;
        }
    }

    // Walk-to state machine, ticked once per frame from MainThreadPump.Drain.
    // Steers the local walker by facing the target and holding the stick
    // forward through BotInput; self-calibrates the stick sign once if no
    // progress is made. Deposit rides the same drive with a dwell phase at
    // the station so the game's own deposit logic can run.
    public static class BotDrive
    {
        public static bool Active;
        public static bool Arrived;
        public static string Status = "idle";
        public static Vector3 Target;
        public static float DeadlineAt;
        public static float LastDist = float.MaxValue;
        public static float LastProgressAt;
        public static bool Flipped;
        public static float StickSign = 1f;
        public static bool DepositIntent;
        public static MoonDustStation DepositStation;
        public static float DwellUntil;
        public static bool Dwelling;
        public static double CargoBefore = -1;
        public static string LastReport;

        public static void Start(Vector3 target, float timeoutSeconds, bool deposit, MoonDustStation station, double cargoBefore)
        {
            Active = true; Arrived = false; Dwelling = false;
            Status = deposit ? "walking_to_collector" : "walking";
            Target = target;
            DeadlineAt = Time.realtimeSinceStartup + Math.Max(10f, timeoutSeconds);
            LastDist = float.MaxValue; LastProgressAt = Time.realtimeSinceStartup;
            Flipped = false; StickSign = 1f;
            DepositIntent = deposit; DepositStation = station; CargoBefore = cargoBefore;
            BotInput.WalkOverride = true; BotInput.Stick = Vector2.zero;
        }

        public static void Cancel(string why)
        {
            Active = false; Dwelling = false;
            BotInput.WalkOverride = false; BotInput.Stick = Vector2.zero;
            Status = why;
        }

        public static void Tick()
        {
            // Vehicle override expiry also zeroes the vehicle's DriveInput once.
            if (BotInput.VehicleOverride && DateTime.UtcNow > BotInput.VehicleUntilUtc)
                BotInput.StopVehicle();
            if (!Active) return;
            var walker = GameTools.WalkerPub();
            if (walker == null) { Cancel("no_walker"); return; }
            var now = Time.realtimeSinceStartup;
            if (Dwelling)
            {
                BotInput.Stick = Vector2.zero;
                if (now >= DwellUntil)
                {
                    double after = -1;
                    try { var inv = GameTools.InventoryPub(); if (inv != null) after = inv.Kilograms; } catch { }
                    LastReport = "{\"cargo_before_kg\":" + CargoBefore.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                        + ",\"cargo_after_kg\":" + after.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "}";
                    Active = false; Arrived = true; Status = "deposited";
                    BotInput.WalkOverride = false; BotInput.Stick = Vector2.zero;
                }
                return;
            }
            var pos = walker.transform.position;
            var dx = Target.x - pos.x; var dz = Target.z - pos.z;
            var dist = (float)Math.Sqrt(dx * dx + dz * dz);
            if (dist <= 1.3f)
            {
                BotInput.Stick = Vector2.zero;
                if (DepositIntent && DepositStation != null)
                {
                    Dwelling = true; Status = "depositing";
                    var dwell = 4f;
                    try { dwell = DepositStation.depositSeconds + 3f; } catch { }
                    DwellUntil = now + dwell;
                }
                else { Active = false; Arrived = true; Status = "arrived"; BotInput.WalkOverride = false; }
                return;
            }
            if (now > DeadlineAt) { Cancel("timeout"); return; }
            if (dist < LastDist - 0.05f) { LastDist = dist; LastProgressAt = now; }
            else if (!Flipped && now - LastProgressAt > 2.0f) { Flipped = true; StickSign = -StickSign; LastProgressAt = now; }
            var dir = new Vector3(dx, 0f, dz);
            if (dir.sqrMagnitude > 0.0001f)
            {
                dir.Normalize();
                try { walker.Face(dir, 0f); } catch { }
            }
            BotInput.Stick = new Vector2(0f, StickSign);
        }
    }

    // Suction level driver: the truck's suction level only changes through
    // MoonInput.SuctionStep pulses (its setters are private). Pulse +1, read
    // back, learn the maximum (levels wrap to 0 past the top).
    public static class BotSuction
    {
        public static bool Active;
        public static bool Done;
        public static string Status = "idle";
        public static int Peak;
        public static int Goal = -1; // -1 = full
        private static int LastLevel = -1;
        private static int NoChange;
        private static float NextAt;

        // Write <SuctionLevel>k__BackingField (offset 0x15C in this build)
        // directly on the native object and verify via the public getter.
        public static unsafe bool SetLevelDirect(int level)
        {
            try
            {
                var veh = GameTools.Vehicle();
                if (veh == null) return false;
                var p = (byte*)veh.Pointer.ToPointer();
                *(int*)(p + 0x15C) = level;
                return veh.SuctionLevel == level;
            }
            catch { return false; }
        }

        // v0.6.12: call the game's own private set_SuctionLevel through
        // il2cpp_runtime_invoke, so SuctionChangedAt, the speed/width
        // shares and the HUD actually update. The raw backing-field write
        // (SetLevelDirect) only moved the getter - the HUD stayed NORMAL
        // all evening on 2026-10-26 while the plugin reported success.
        public static unsafe bool SetLevelInvoke(int level)
        {
            try
            {
                var veh = GameTools.Vehicle();
                if (veh == null) return false;
                var klass = IL2CPP.il2cpp_object_get_class(veh.Pointer);
                if (klass == IntPtr.Zero) return false;
                var method = IL2CPP.il2cpp_class_get_method_from_name(klass, "set_SuctionLevel", 1);
                if (method == IntPtr.Zero) return false;
                var arg = level;
                var args = stackalloc void*[1];
                args[0] = &arg;
                var exc = IntPtr.Zero;
                IL2CPP.il2cpp_runtime_invoke(method, veh.Pointer, args, ref exc);
                if (exc != IntPtr.Zero) return false;
                return veh.SuctionLevel == level;
            }
            catch { return false; }
        }

        public static void StartFull()
        {
            if (SetLevelInvoke(3)) { Active = false; Done = true; Status = "at max (setter)"; return; }
            if (SetLevelDirect(3)) { Active = false; Done = true; Status = "at max (direct)"; return; }
            Active = true; Done = false; Status = "seeking max";
            Goal = -1; Peak = -1; LastLevel = -1; NoChange = 0;
            NextAt = Time.realtimeSinceStartup + 0.2f;
        }

        public static void StartLevel(int level)
        {
            if (level < 0) level = 0;
            if (level > 3) level = 3;
            if (SetLevelInvoke(level)) { Active = false; Done = true; Status = "set to " + level + " (setter)"; return; }
            if (SetLevelDirect(level)) { Active = false; Done = true; Status = "set to " + level + " (direct)"; return; }
            Active = true; Done = false; Status = "seeking level " + level;
            Goal = level; Peak = -1; LastLevel = -1; NoChange = 0;
            NextAt = Time.realtimeSinceStartup + 0.2f;
        }

        public static void Tick()
        {
            if (!Active) return;
            var now = Time.realtimeSinceStartup;
            if (now < NextAt) return;
            NextAt = now + 0.4f;
            var veh = GameTools.Vehicle();
            if (veh == null) { Active = false; Status = "no vehicle"; return; }
            int level;
            try { level = veh.SuctionLevel; } catch { return; }
            if (LastLevel >= 0)
            {
                if (level == LastLevel) NoChange++; else NoChange = 0;
                if (level > Peak) Peak = level;
                if (Goal >= 0 && level == Goal) { Active = false; Done = true; Status = "at goal " + level; return; }
                if (Goal >= 0 && NoChange >= 3) { Active = false; Done = true; Status = "stuck at " + level; return; }
                if (Goal < 0)
                {
                    if (level < LastLevel) { Goal = Peak; Status = "wrapped; returning to max " + Peak; }
                    else if (NoChange >= 2) { Active = false; Done = true; Status = "at max " + level; return; }
                }
            }
            LastLevel = level;
            var step = Goal >= 0 ? Math.Sign(Goal - level) : 1;
            if (step == 0) step = 1;
            try { Plugin.Log.LogInfo("[BotSuction] step " + step + " at level " + level + " goal " + Goal); } catch { }
            BotInput.PulseSuctionStep(step);
        }
    }

    // Vehicle autopilot ("FSD for the sweeper truck"): steers the vehicle the
    // player is seated in toward a target through the same MoonInput throttle/
    // steer overrides a human would produce. Time-boxed, self-stopping.
    // v0.6.1: steering/distance computed in the surface tangent plane (the
    // world is a sphere - flat XZ steering degenerates at the poles, where
    // both stations live), stuck detection with reverse recovery, cleaning
    // latch re-checked every second, forward obstacle cast for slow/stop.
    public static class BotAuto
    {
        public static bool Active;
        public static bool Arrived;
        public static string Status = "idle";
        public static Vector3 Target;
        public static float DeadlineAt;
        public static bool CleaningWanted;
        public static bool CruiseWanted;
        public static bool CruiseEngaged;
        private static float CruisePulseAt;
        public static int Recoveries;
        public static float ObstacleM = -1f;
        public static string ObstacleName = "";
        private static float LastDist = float.MaxValue;
        private static float LastProgressAt;
        private static float NextCleanCheck;
        private static float NextSenseAt;
        private static float RecoveryUntil;
        private static Vector3 LastTickPos;
        private static float LastTickAt;
        private static bool HaveTickPos;
        private static float RecoveryDist;
        private static float ObstacleBlockSince;
        private static float ObstacleBlockDist;
        private static float PrevSteer;
        private static float StartedAt;
        private static float NextLogAt;
        private static Vector3 LastLogPos;
        private static bool HaveLogPos;
        private static float RecoverySteer;
        // v0.6.13: pure-pursuit + anti-orbit state (steering-control memo).
        private static Vector3 ApproachDir;
        private static bool HaveBasis;
        private static Vector3 BasisUp;
        private static Vector3 BasisEast;
        private static Vector3 BasisNorth;
        private static float LastHdg;
        private static float WindAccum;
        private static float WindWindowAt;
        private static float WindStartDist;
        public const float SteerSign = 1f; // flip if live test shows inverted steering

        public static void Start(Vector3 target, float timeoutSeconds, bool cleaning, bool cruise = false)
        {
            Active = true; Arrived = false; Status = "driving";
            Target = target;
            DeadlineAt = Time.realtimeSinceStartup + Math.Max(15f, timeoutSeconds);
            CleaningWanted = cleaning;
            CruiseWanted = cruise; CruiseEngaged = false; CruisePulseAt = 0f;
            Recoveries = 0;
            PrevSteer = 0f;
            ObstacleM = -1f; ObstacleName = "";
            LastDist = float.MaxValue; LastProgressAt = Time.realtimeSinceStartup;
            ObstacleBlockSince = 0f; ObstacleBlockDist = float.MaxValue;
            StartedAt = Time.realtimeSinceStartup;
            NextCleanCheck = 0f; NextSenseAt = 0f; RecoveryUntil = 0f; NextLogAt = 0f; HaveLogPos = false; HaveTickPos = false; RecoveryDist = float.MaxValue;
            HaveBasis = false; WindAccum = 0f; WindWindowAt = 0f; WindStartDist = float.MaxValue; ApproachDir = Vector3.zero;
        }

        public static void Cancel(string why)
        {
            Active = false; Status = why;
            BotInput.StopVehicle();
        }

        // Project v onto the surface tangent plane at pos (world centre = origin).
        private static Vector3 Tangent(Vector3 pos, Vector3 v)
        {
            var up = pos;
            if (up.sqrMagnitude < 1f) return v;
            up.Normalize();
            return v - up * Vector3.Dot(v, up);
        }

        public static void Tick()
        {
            if (!Active) return;
            var veh = GameTools.Vehicle();
            if (veh == null) { Cancel("no_vehicle"); return; }
            var now = Time.realtimeSinceStartup;
            if (now > DeadlineAt) { Cancel("timeout"); return; }
            var pos = veh.transform.position;

            // Cleaning latch: re-check every second until it reads on.
            if (CleaningWanted && now >= NextCleanCheck)
            {
                NextCleanCheck = now + 1f;
                try { if (!veh.Cleaning) veh.ToggleCleaning(); } catch { }
            }

            // v0.6.13: geometry first, in the tangent plane at the truck.
            // Angle + distance drive arrival, RECOVER and the steering law.
            var toT3 = Target - pos;
            var dist = toT3.magnitude;
            var fwdT2 = Tangent(pos, veh.transform.forward);
            var toTT = Tangent(pos, toT3);
            var haveGeom = fwdT2.sqrMagnitude > 0.001f && toTT.sqrMagnitude > 0.001f;
            var ang = 0f;
            if (haveGeom)
            {
                fwdT2.Normalize(); toTT.Normalize();
                var upv2 = pos.normalized;
                var cross2 = Vector3.Cross(fwdT2, toTT);
                ang = (float)Math.Atan2(Vector3.Dot(cross2, upv2), Vector3.Dot(fwdT2, toTT));
                if (dist > 25f) ApproachDir = toTT; // remembered for the pass test
            }
            // Arrival: inside the radius, OR past the waypoint's plane
            // (a turning-radius truck may never enter a small circle).
            if (dist <= 6f || (dist < 12f && ApproachDir.sqrMagnitude > 0.5f && Vector3.Dot(toT3, ApproachDir) < 0f))
            {
                Active = false; Arrived = true; Status = dist <= 6f ? "arrived" : "passed";
                BotInput.StopVehicle();
                return;
            }

            // Recovery manoeuvre in progress: reverse out with counter-lock.
            // v0.6.13: steer tracks -sign(ang) and the manoeuvre exits early
            // once the nose is back within 45 degrees of the target.
            if (now < RecoveryUntil)
            {
                if (haveGeom && Math.Abs(ang) < 0.785f) { RecoveryUntil = 0f; LastProgressAt = now; }
                else
                {
                    if (haveGeom) RecoverySteer = (ang >= 0f ? -1f : 1f) * SteerSign;
                    BotInput.SteerVal = RecoverySteer;
                    BotInput.ThrottleVal = -0.55f;
                    BotInput.VehicleUntilUtc = DateTime.UtcNow.AddSeconds(0.5);
                    BotInput.VehicleOverride = true;
                    return;
                }
            }
            var spdTick = 0f;
            if (HaveTickPos)
            {
                var dtTick = now - LastTickAt;
                if (dtTick > 0.01f) spdTick = Vector3.Distance(pos, LastTickPos) / dtTick;
            }
            LastTickPos = pos; LastTickAt = now; HaveTickPos = true;
            if (dist < LastDist - 0.15f)
            {
                LastDist = dist; LastProgressAt = now;
                if (Recoveries > 0 && dist < RecoveryDist - 5f) Recoveries = 0; // real progress clears the count
            }
            // v0.6.13: progress is DISTANCE CLOSURE ONLY. The old speed clause
            // (spdTick > 1) counted a circling truck as "making progress" and
            // made the orbit immortal — the stuck check could never fire.
            // v0.6.13 anti-orbit watchdog: accumulate heading rotation in the
            // leg's tangent basis. >270 degrees of winding with <15% distance
            // closure is an orbit, not driving (a slow creep orbit takes
            // ~20 s/rev, so the window is 30 s, not 8). Also enter RECOVER
            // directly when the target sits behind-close.
            if (haveGeom)
            {
                if (!HaveBasis)
                {
                    BasisUp = pos.normalized;
                    var e = Tangent(pos, Vector3.right);
                    if (e.sqrMagnitude < 0.01f) e = Tangent(pos, Vector3.forward);
                    BasisEast = e.normalized;
                    BasisNorth = Vector3.Cross(BasisUp, BasisEast);
                    LastHdg = (float)Math.Atan2(Vector3.Dot(fwdT2, BasisNorth), Vector3.Dot(fwdT2, BasisEast));
                    WindAccum = 0f; WindWindowAt = now; WindStartDist = dist;
                    HaveBasis = true;
                }
                else
                {
                    var hdg = (float)Math.Atan2(Vector3.Dot(fwdT2, BasisNorth), Vector3.Dot(fwdT2, BasisEast));
                    var dH = hdg - LastHdg;
                    while (dH > 3.14159f) dH -= 6.28318f;
                    while (dH < -3.14159f) dH += 6.28318f;
                    LastHdg = hdg;
                    WindAccum += Math.Abs(dH);
                    if (dist < WindStartDist * 0.7f) { WindAccum = 0f; WindWindowAt = now; WindStartDist = dist; } // healthy closure resets
                }
                var orbit = WindAccum > 4.712f && now - WindWindowAt >= 10f && dist > WindStartDist * 0.85f;
                var behindClose = Math.Abs(ang) > 2.0f && dist < 45f;
                if (orbit || behindClose)
                {
                    if (Recoveries >= 3) { Cancel("orbit"); return; }
                    Recoveries++;
                    Status = "recovering";
                    RecoverySteer = (ang >= 0f ? -1f : 1f) * SteerSign;
                    RecoveryUntil = now + 3.5f;
                    RecoveryDist = dist;
                    LastProgressAt = now; LastDist = dist;
                    WindAccum = 0f; WindWindowAt = now; WindStartDist = dist;
                    return;
                }
                if (now - WindWindowAt > 30f) { WindAccum = 0f; WindWindowAt = now; WindStartDist = dist; }
            }
            if (!(ObstacleM > 0f && ObstacleM < 6f)) { ObstacleBlockSince = 0f; ObstacleBlockDist = float.MaxValue; } // moving at speed is never "stuck" - a fleeing target is not a wall
            if (ObstacleM > 0f && ObstacleM < 6f)
            {
                LastProgressAt = now; // creeping around an obstacle is not "stuck"
                // ...but wedged is not creeping: if the gap to the target
                // has not closed by at least 1 m in 20 s with an obstacle
                // close ahead, abort as blocked instead of grinding on it
                // forever (the 2026-10-26 Collector wedge, dist frozen
                // at 10.67 m for minutes).
                if (dist < ObstacleBlockDist - 1f) { ObstacleBlockDist = dist; ObstacleBlockSince = now; }
                else if (ObstacleBlockSince <= 0f) { ObstacleBlockSince = now; ObstacleBlockDist = dist; }
                else if (now - ObstacleBlockSince > 20f) { Cancel("blocked"); return; }
            }
            else if (now - StartedAt > 7f && now - LastProgressAt > 6f)
            {
                if (Recoveries >= 3) { Cancel("stuck"); return; }
                Recoveries++;
                Status = "recovering";
                RecoverySteer = BotInput.SteerVal >= 0f ? -0.9f : 0.9f;
                RecoveryUntil = now + 2.2f;
                RecoveryDist = dist;
                LastProgressAt = now;
                LastDist = dist;
                return;
            }
            if (Status == "recovering") Status = "driving";

            // Cruise: pulse the binding once, then let the truck hold speed.
            if (CruiseWanted && !CruiseEngaged)
            {
                bool on = false;
                try { on = veh.CruiseOn; } catch { }
                if (on) { CruiseEngaged = true; Status = "driving (cruise)"; }
                else if (CruisePulseAt == 0f || now - CruisePulseAt > 3f)
                {
                    CruisePulseAt = now;
                    BotInput.PulseCruise();
                }
            }

            // Obstacle sense (2 Hz): sphere-cast ahead; slow, then stop+hold.
            if (now >= NextSenseAt)
            {
                NextSenseAt = now + 0.5f;
                ObstacleM = -1f; ObstacleName = "";
                try
                {
                    var up = pos.normalized;
                    var fwdT = Tangent(pos, veh.transform.forward);
                    if (fwdT.sqrMagnitude > 0.01f)
                    {
                        fwdT.Normalize();
                        var origin = pos + up * 1.6f + fwdT * 2.0f;
                        if (Physics.SphereCast(origin, 2.1f, fwdT, out var hit, 16f))
                        {
                            var n = hit.collider != null ? hit.collider.gameObject.name : "?";
                            // Ignore hits on our own vehicle, and ignore TERRAIN:
                            // the sphere-cast grazes the ground constantly on a
                            // sphere world and braking for it was the 4 km/h crawl.
                            if ((n == null || !n.StartsWith("Terrain")) && (hit.collider == null || !hit.collider.transform.IsChildOf(veh.transform)))
                            {
                                ObstacleM = hit.distance;
                                ObstacleName = n ?? "";
                            }
                        }
                    }
                }
                catch { ObstacleM = -1f; }
            }

            if (!haveGeom) return;
            // v0.6.13: pure-pursuit steering + curvature/arrival speed
            // profile (steering-control memo, 2026-10-27). The old law —
            // saturating gain 1.1 with throttle = 0.30 + 0.70*cos(ang) — has
            // a stable limit cycle at ang ~90 deg: the orbit. Pure pursuit's
            // gain softens as range closes and can never demand an arc
            // tighter than the truck can drive; speed comes from the
            // demanded curvature and the arrival ramp, not from cos(ang).
            var vNow = spdTick;
            var look = Math.Max(6f, Math.Min(20f, 1.2f * vNow));
            var kappa = 2f * (float)Math.Sin(ang) / Math.Max(dist, look);
            var kappaMax = 0.143f; // R_min ~7 m — measure live, then retune
            if (kappa > kappaMax) kappa = kappaMax; else if (kappa < -kappaMax) kappa = -kappaMax;
            var steerTarget = Math.Abs(ang) < 0.02f ? 0f : kappa / kappaMax * SteerSign;
            var maxSteerStep = 2.2f * Math.Max(0.001f, Time.deltaTime);
            var steer = Math.Max(PrevSteer - maxSteerStep, Math.Min(PrevSteer + maxSteerStep, steerTarget));
            PrevSteer = steer;
            var vCurve = (float)Math.Sqrt(3.5f / Math.Max(Math.Abs(kappa), 0.01f));
            var vArrive = Math.Max(0f, Math.Min(11f, dist / 3.0f));
            var vDes = Math.Min(11f, Math.Min(vCurve, vArrive));
            if (dist > 8f) vDes = Math.Max(vDes, 1.2f); // crawl floor until the arrival radius
            var throttle = Math.Max(-1f, Math.Min(1f, 0.30f * (vDes - vNow)));
            if (CruiseEngaged) throttle = 0f; // cruise holds the speed; we just steer
            if (ObstacleM > 0f)
            {
                if (ObstacleM < 4f) throttle = 0f;
                else if (ObstacleM < 8f) throttle *= 0.4f;
            }
            if (now >= NextLogAt)
            {
                NextLogAt = now + 1f;
                var spd = HaveLogPos ? Vector3.Distance(pos, LastLogPos) : 0f;
                LastLogPos = pos; HaveLogPos = true;
                try { Plugin.Log.LogInfo("[BotAuto] thr=" + throttle.ToString("F2") + " drive_in=" + veh.DriveInput.ToString("F2") + " spd=" + spd.ToString("F1") + "m/s dist=" + dist.ToString("F0") + " status=" + Status + " rec=" + Recoveries + " obs=" + (ObstacleM >= 0f ? ObstacleM.ToString("F1") + " " + ObstacleName : "-") + " cruise_on=" + veh.CruiseOn + " ang=" + (ang * 57.29578f).ToString("F0") + " vdes=" + vDes.ToString("F1") + " wind=" + (WindAccum * 57.29578f).ToString("F0")); } catch { }
            }
            BotInput.SteerVal = steer;
            BotInput.ThrottleVal = throttle;
            BotInput.VehicleUntilUtc = DateTime.UtcNow.AddSeconds(0.5);
            BotInput.VehicleOverride = true;
        }
    }

    // Runs on the Unity main thread; drains tool calls queued by TCP threads.
    // Postfix target: runs on the Unity main thread inside the game's own
    // Update; drains tool calls queued by the TCP threads.
    public static class MainThreadPump
    {
        public static readonly ConcurrentQueue<PendingCall> Queue = new ConcurrentQueue<PendingCall>();

        public static void Drain()
        {
            try { BotDrive.Tick(); } catch { }
            try { BotAuto.Tick(); } catch { }
            try { BotSuction.Tick(); } catch { }
            try { BotInput.RefreshActionPointers(); } catch { }
            var budget = 8;
            while (budget-- > 0 && Queue.TryDequeue(out var call))
            {
                try { call.Result = GameTools.Execute(call.Tool, call.Args); }
                catch (Exception e) { call.Error = e.GetType().Name + ": " + e.Message; }
                call.Done.Set();
            }
        }
    }

    public class PendingCall
    {
        public string Tool;
        public JsonElement Args;
        public string Result;
        public string Error;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
    }

    internal static class GameTools
    {
        private static T Find<T>() where T : UnityEngine.Object
        {
            // Object.FindObjectOfType(Type) fails "Method unstripping failed"
            // on this Unity 6000.6 build; the plural overload works.
            var found = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<T>(), true);
            if (found != null)
                foreach (var o in found)
                {
                    var c = o != null ? o.TryCast<T>() : null;
                    if (c != null) return c;
                }
            return null;
        }
        private static MoonWorld World() => Find<MoonWorld>();
        private static MoonWalker Walker() => Find<MoonWalker>();
        internal static MoonWalker WalkerPub() => Walker();

        internal static MoonPrototypeVehicle Vehicle()
        {
            var eq = Equipment();
            try { var v = eq != null ? eq.Vehicle : null; if (v != null) return v; } catch { }
            return Find<MoonPrototypeVehicle>();
        }

        internal static MoonPrototypeEquipment Equipment()
        {
            var walker = Walker();
            var all = FindAll<MoonPrototypeEquipment>();
            if (all.Count == 0) return null;
            if (walker == null) return all[0];
            var wp = walker.transform.position;
            MoonPrototypeEquipment best = null; var bestD = float.MaxValue;
            foreach (var e in all)
            {
                try
                {
                    var d = (e.transform.position - wp).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = e; }
                }
                catch { if (best == null) best = e; }
            }
            return best ?? all[0];
        }

        internal static MoonToolbelt Belt()
        {
            var walker = Walker();
            var all = FindAll<MoonToolbelt>();
            if (all.Count == 0) return null;
            if (walker == null) return all[0];
            var wp = walker.transform.position;
            MoonToolbelt best = null; var bestD = float.MaxValue;
            foreach (var b in all)
            {
                try
                {
                    var d = (b.transform.position - wp).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = b; }
                }
                catch { if (best == null) best = b; }
            }
            return best ?? all[0];
        }

        internal static MoonDustInventory InventoryPub() => Inventory();

        private static MoonDustInventory Inventory()
        {
            var eq = Equipment();
            try { var inv = eq != null ? eq.Inventory : null; if (inv != null) return inv; } catch { }
            var walker = Walker();
            var all = FindAll<MoonDustInventory>();
            if (all.Count == 0) return null;
            if (walker == null) return all[0];
            var wp = walker.transform.position;
            MoonDustInventory best = null; var bestD = float.MaxValue;
            foreach (var i2 in all)
            {
                try
                {
                    var d = (i2.transform.position - wp).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = i2; }
                }
                catch { if (best == null) best = i2; }
            }
            return best ?? all[0];
        }
        private static MoonBrush Brush()
        {
            var walker = Walker();
            var all = FindAll<MoonBrush>();
            if (all.Count == 0) return null;
            if (walker == null) return all[0];
            var wp = walker.transform.position;
            MoonBrush best = null; var bestD = float.MaxValue;
            foreach (var b in all)
            {
                try
                {
                    var bp = b.transform.position;
                    var d = (bp - wp).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = b; }
                }
                catch { if (best == null) best = b; }
            }
            return best ?? all[0];
        }

        public static string Execute(string tool, JsonElement args)
        {
            switch (tool)
            {
                case "state": return State();
                case "host_info": return HostInfo();
                case "teleport": return Teleport(args);
                case "face": return Face(args);
                case "sweep": return Sweep(args);
                case "equip": return Equip(args);
                case "deposit": return Deposit();
                case "screenshot": return Screenshot(args);
                case "radar": return Radar(args);
                case "guide": return GuideText;
                case "tools": return ToolsList();
                case "crew": return Crew();
                case "save_game": return SaveGame();
                case "return_to_depot": return ReturnToDepot();
                case "buy": return Buy(args);
                case "walk_to": return WalkTo(args);
                case "walk_status": return WalkStatus();
                case "walk_stop": BotDrive.Cancel("stopped"); return "{\"walk_stopped\":true}";
                case "drive": return Drive(args);
                case "drive_stop": BotAuto.Cancel("stopped"); BotInput.StopVehicle(); return "{\"drive_stopped\":true}";
                case "drive_to": return DriveTo(args);
                case "drive_status": return DriveStatus();
                case "sense": return Sense(args);
                case "vehicle": return VehicleTool(args);
                case "join_lobby": return JoinLobby(args);
                case "leave_lobby": MoonSteamLobby.Leave(); return "{\"left_lobby\":true}";
                default: throw new ArgumentException("unknown tool: " + tool);
            }
        }

        private static string State()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            Field(sb, "in_lobby", MoonSteamLobby.InLobby.ToString().ToLowerInvariant(), true);
            Field(sb, "is_host", MoonSteamLobby.IsHost.ToString().ToLowerInvariant(), true);
            try
            {
                var walker = Walker();
                if (walker != null)
                {
                    var p = walker.transform.position;
                    sb.Append(",\"player_pos\":[").Append(F(p.x)).Append(',').Append(F(p.y)).Append(',').Append(F(p.z)).Append(']');
                    sb.Append(",\"player_present\":true");
                }
                else sb.Append(",\"player_present\":false");
            }
            catch (Exception e) { Field(sb, "player_error", Q(e.Message), false); }
            try
            {
                var brush = Brush();
                if (brush?.EquippedDefinition != null)
                    Field(sb, "tool", Q(brush.EquippedDefinition.displayName), false);
                Field(sb, "brush_count", FindAll<MoonBrush>().Count.ToString(), true);
                var wk = Walker();
                if (brush != null && wk != null)
                {
                    var bp = brush.transform.position; var wp2 = wk.transform.position;
                    var dd = Math.Sqrt((bp.x - wp2.x) * (bp.x - wp2.x) + (bp.z - wp2.z) * (bp.z - wp2.z));
                    Field(sb, "brush_dist_m", F((float)dd), true);
                }
            }
            catch (Exception e) { Field(sb, "tool_error", Q(e.Message), false); }
            try
            {
                var inv = Inventory();
                if (inv != null)
                {
                    try { Field(sb, "cargo_kg", F((float)inv.Kilograms), true); } catch { }
                    try { Field(sb, "cargo_capacity_kg", F((float)inv.CapacityKg), true); } catch { }
                    try { Field(sb, "cargo_pending_deposit_kg", F((float)inv.PendingDepositKg), true); } catch { }
                    try { Field(sb, "cargo_full", inv.IsFull.ToString().ToLowerInvariant(), true); } catch { }
                    try { Field(sb, "container", Q(inv.ContainerName ?? ""), false); } catch { }
                    try { var ht = inv.HeldTool; if (ht != null) Field(sb, "held_tool", Q(ht.displayName ?? ""), false); } catch { }
                }
            }
            catch (Exception e) { Field(sb, "cargo_error", Q(e.Message), false); }
            try
            {
                var veh = Vehicle();
                if (veh != null)
                {
                    var vp = veh.transform.position;
                    sb.Append(",\"vehicle\":{\"present\":true,\"pos\":[").Append(F(vp.x)).Append(',').Append(F(vp.y)).Append(',').Append(F(vp.z)).Append(']');
                    try { sb.Append(",\"drive_input\":").Append(F(veh.DriveInput)); } catch { }
                    try { sb.Append(",\"cruise_on\":").Append(veh.CruiseOn.ToString().ToLowerInvariant()); } catch { }
                    try { sb.Append(",\"cleaning\":").Append(veh.Cleaning.ToString().ToLowerInvariant()); } catch { }
                    try { sb.Append(",\"suction_level\":").Append(veh.SuctionLevel); } catch { }
                    try { sb.Append(",\"suction\":").Append(veh.Suction); } catch { }
                    try { sb.Append(",\"dust_kg\":").Append(F((float)veh.DustKilograms)); } catch { }
                    try { sb.Append(",\"dust_capacity_kg\":").Append(F((float)veh.DustCapacityKg)); } catch { }
                    try { sb.Append(",\"dust_full\":").Append(veh.DustFull.ToString().ToLowerInvariant()); } catch { }
                    sb.Append('}');
                }
            }
            catch { }
            sb.Append(",\"walk\":{\"active\":").Append(BotDrive.Active.ToString().ToLowerInvariant())
              .Append(",\"status\":").Append(Q(BotDrive.Status)).Append('}');
            try
            {
                var w = World();
                if (w != null)
                {
                    try { Field(sb, "wallet_dollars", Q(PrettyMoney(w.WalletDollars("local").ToString())), false); } catch { }
                    Field(sb, "deposited_kg", F((float)w.DepositedDustKilograms), true);
                    Field(sb, "last_collected_kg", F((float)w.LastCollectedKilograms), true);
                }
            }
            catch (Exception e) { Field(sb, "world_error", Q(e.Message), false); }
            sb.Append('}');
            return sb.ToString();
        }

        private static string HostInfo()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            Field(sb, "in_lobby", MoonSteamLobby.InLobby.ToString().ToLowerInvariant(), true);
            Field(sb, "is_host", MoonSteamLobby.IsHost.ToString().ToLowerInvariant(), true);
            Field(sb, "lobby_id", MoonSteamLobby.LobbyId.ToString(), true);
            Field(sb, "host_id", MoonSteamLobby.HostId.ToString(), true);
            Field(sb, "crew_name", Q(MoonSteamLobby.CrewName ?? ""), false);
            Field(sb, "all_ready", MoonSteamLobby.AllReady.ToString().ToLowerInvariant(), true);
            sb.Append('}');
            return sb.ToString();
        }

        private static string Teleport(JsonElement args)
        {
            var walker = Walker() ?? throw new InvalidOperationException("no player walker in scene yet");
            BotDrive.Cancel("teleported");
            BotAuto.Cancel("teleported");
            var cur = walker.transform.position;
            var x = GetF(args, "x", cur.x);
            var z = GetF(args, "z", cur.z);
            var y = GetF(args, "y", cur.y + 1.0f);
            walker.Teleport(new Vector3(x, y, z), true);
            var p = walker.transform.position;
            return "{\"teleported\":[" + F(p.x) + "," + F(p.y) + "," + F(p.z) + "]}";
        }

        private static string Face(JsonElement args)
        {
            var walker = Walker() ?? throw new InvalidOperationException("no player walker in scene yet");
            var deg = GetF(args, "yaw_degrees", 0f);
            var rad = deg * Mathf.PI / 180f;
            walker.Face(new Vector3(Mathf.Sin(rad), 0, Mathf.Cos(rad)), 0f);
            return "{\"faced_degrees\":" + F(deg) + "}";
        }

        private static string Sweep(JsonElement args)
        {
            var walker = Walker() ?? throw new InvalidOperationException("no player walker in scene yet");
            var brush = Brush() ?? throw new InvalidOperationException("no brush in scene yet");
            var seconds = GetF(args, "seconds", 0.5f);
            var origin = walker.transform.position + new Vector3(0, 1.4f, 0);
            var fwd = walker.transform.forward;
            var dir = new Vector3(fwd.x, -0.85f, fwd.z);
            dir.Normalize();
            var swept = brush.Sweep(new Ray(origin, dir), seconds, false);
            return "{\"sweep_return\":" + swept + "}";
        }

        private static string Equip(JsonElement args)
        {
            var namePart = args.TryGetProperty("tool", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(namePart)) throw new ArgumentException("equip needs {tool: \"<part of display name>\"}");
            // Human path (v0.6): the toolbelt holds the owned tools in order and
            // MoonPrototypeEquipment.EquipTool(index) performs the real switch
            // (brush + dust inventory + HUD). MoonBrush.Equip alone only changed
            // a definition and left the hand tool stale.
            var belt = Belt();
            var eq = Equipment();
            if (belt != null && eq != null && belt.tools != null)
            {
                var names = new StringBuilder();
                for (var i = 0; i < belt.tools.Length; i++)
                {
                    var d = belt.tools[i];
                    if (d == null) continue;
                    names.Append(i).Append(':').Append(d.displayName).Append("; ");
                    if (d.displayName != null && d.displayName.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        eq.EquipTool(i);
                        var active = -1; try { active = eq.ActiveTool; } catch { }
                        return "{\"equipped\":" + Q(d.displayName) + ",\"index\":" + i + ",\"active_tool\":" + active + ",\"via\":\"equipment\"}";
                    }
                }
                throw new InvalidOperationException("no belt tool matching '" + namePart + "'. belt: " + names);
            }
            var brush = Brush() ?? throw new InvalidOperationException("no brush in scene yet");
            var found = Resources.FindObjectsOfTypeAll(Il2CppType.Of<MoonToolDefinition>());
            var defs = new System.Collections.Generic.List<MoonToolDefinition>();
            if (found != null) foreach (var o in found) { var d = o != null ? o.TryCast<MoonToolDefinition>() : null; if (d != null) defs.Add(d); }
            MoonToolDefinition match = null;
            var allNames = new StringBuilder();
            foreach (var d in defs)
            {
                allNames.Append(d.displayName).Append("; ");
                if (match == null && d.displayName != null &&
                    d.displayName.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0)
                    match = d;
            }
            if (match == null) throw new InvalidOperationException("no tool matching '" + namePart + "'. available: " + allNames);
            brush.Equip(match, true);
            return "{\"equipped\":" + Q(match.displayName) + ",\"via\":\"brush-legacy\"}";
        }

        private static string ToolsList()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            string equipped = null;
            try { equipped = Brush()?.EquippedDefinition?.displayName; } catch { }
            var belt = Belt();
            var eq = Equipment();
            if (belt != null && belt.tools != null)
            {
                sb.Append("\"belt\":[");
                var firstB = true;
                for (var i = 0; i < belt.tools.Length; i++)
                {
                    var d = belt.tools[i];
                    if (d == null) continue;
                    if (!firstB) sb.Append(',');
                    firstB = false;
                    sb.Append("{\"index\":").Append(i).Append(",\"name\":").Append(Q(d.displayName ?? d.name ?? "?")).Append('}');
                }
                sb.Append(']');
                try { Field(sb, "active_tool_index", eq != null ? eq.ActiveTool.ToString() : "-1", true); } catch { }
            }
            Field(sb, "equipped", Q(equipped ?? ""), false);
            var found = Resources.FindObjectsOfTypeAll(Il2CppType.Of<MoonToolDefinition>());
            sb.Append(",\"tools\":[");
            var first = true;
            if (found != null) foreach (var o in found)
            {
                var d = o != null ? o.TryCast<MoonToolDefinition>() : null;
                if (d == null) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Q(d.displayName ?? d.name ?? "?"));
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string Crew()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            Field(sb, "in_lobby", MoonSteamLobby.InLobby.ToString().ToLowerInvariant(), true);
            Field(sb, "local_index", MoonSteamLobby.LocalMemberIndex.ToString(), true);
            sb.Append(",\"members\":[");
            var first = true;
            try
            {
                var members = MoonSteamLobby.Members;
                var list = members != null
                    ? (members as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)?.TryCast<Il2CppSystem.Collections.Generic.List<MoonSteamLobby.Member>>()
                    : null;
                if (list != null)
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        var m = list[i];
                        if (m == null) continue;
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append("{\"name\":").Append(Q(m.name ?? "?"))
                          .Append(",\"id\":").Append(m.id.ToString())
                          .Append(",\"ready\":").Append(m.ready.ToString().ToLowerInvariant())
                          .Append(",\"host\":").Append(m.host.ToString().ToLowerInvariant())
                          .Append(",\"local\":").Append(m.local.ToString().ToLowerInvariant())
                          .Append("}");
                    }
                }
                else sb.Append("],\"crew_error\":\"members list cast failed\"");
            }
            catch (Exception e)
            {
                sb.Append("],\"crew_error\":").Append(Q(e.GetType().Name + ": " + e.Message));
                sb.Append(",\"members_note\":\"see crew_error\"");
                // members array already closed by the error branch
                sb.Append('}');
                return sb.ToString();
            }
            if (!sb.ToString().Contains("crew_error")) sb.Append("]}");
            else sb.Append('}');
            return sb.ToString();
        }

        private static string SaveGame()
        {
            var w = World() ?? throw new InvalidOperationException("no MoonWorld in scene yet");
            w.RequestSave();
            return "{\"save_requested\":true}";
        }

        private static string ReturnToDepot()
        {
            var walker = Walker() ?? throw new InvalidOperationException("no player walker in scene yet");
            walker.ReturnToDepot();
            return "{\"returning_to_depot\":true}";
        }

        private static string Buy(JsonElement args)
        {
            var w = World() ?? throw new InvalidOperationException("no MoonWorld in scene yet");
            var kindStr = args.TryGetProperty("kind", out var k) ? k.GetString() : "Tool";
            var id = args.TryGetProperty("id", out var i2) ? i2.GetString() : null;
            var level = args.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("buy needs {kind, id, level}");
            var kind = kindStr switch
            {
                "Tool" => MoonWorld.PurchaseKind.Tool,
                "Container" => MoonWorld.PurchaseKind.Container,
                "TreeNode" => MoonWorld.PurchaseKind.TreeNode,
                "VehicleUpgrade" => MoonWorld.PurchaseKind.VehicleUpgrade,
                _ => throw new ArgumentException("unknown kind: " + kindStr + " (Tool|Container|TreeNode|VehicleUpgrade)"),
            };
            var ok = w.CrewPurchase("local", kind, id, level);
            return "{\"purchased\":" + ok.ToString().ToLowerInvariant() + ",\"kind\":" + Q(kindStr) + ",\"id\":" + Q(id) + ",\"level\":" + level + "}";
        }

        private static string Deposit()
        {
            var walker = Walker() ?? throw new InvalidOperationException("no player walker in scene yet");
            var collector = Find<MoonCollector>() ?? throw new InvalidOperationException("no MoonCollector in scene");
            MoonDustStation station = null;
            try { station = collector.receiver; } catch { }
            var target = station != null ? station.transform.position : collector.transform.position;
            double cargo = -1;
            try { var inv = Inventory(); if (inv != null) cargo = inv.Kilograms; } catch { }
            BotDrive.Start(new Vector3(target.x, walker.transform.position.y, target.z), 120f, true, station, cargo);
            return "{\"deposit\":\"walking_to_collector\",\"cargo_before_kg\":" + cargo.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + ",\"note\":\"poll walk_status until status=deposited, then verify cargo_kg in state\"}";
        }

        private static string WalkTo(JsonElement args)
        {
            var walker = Walker() ?? throw new InvalidOperationException("no player walker in scene yet");
            var cur = walker.transform.position;
            var x = GetF(args, "x", cur.x);
            var z = GetF(args, "z", cur.z);
            var timeout = GetF(args, "timeout_s", 90f);
            BotDrive.Start(new Vector3(x, cur.y, z), timeout, false, null, -1);
            var dx = x - cur.x; var dz = z - cur.z;
            var dist = Math.Sqrt(dx * dx + dz * dz);
            return "{\"walking\":true,\"target\":[" + F(x) + "," + F(z) + "],\"dist_m\":" + F((float)dist) + "}";
        }

        private static string WalkStatus()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            Field(sb, "active", BotDrive.Active.ToString().ToLowerInvariant(), true);
            Field(sb, "status", Q(BotDrive.Status), false);
            Field(sb, "arrived", BotDrive.Arrived.ToString().ToLowerInvariant(), true);
            var walker = Walker();
            if (walker != null && BotDrive.Active)
            {
                var p = walker.transform.position;
                var dx = BotDrive.Target.x - p.x; var dz = BotDrive.Target.z - p.z;
                Field(sb, "dist_m", F((float)Math.Sqrt(dx * dx + dz * dz)), true);
            }
            if (BotDrive.LastReport != null) sb.Append(",\"deposit_report\":").Append(BotDrive.LastReport);
            sb.Append('}');
            return sb.ToString();
        }

        private static string Drive(JsonElement args)
        {
            var throttle = GetF(args, "throttle", 0f);
            var steer = GetF(args, "steer", 0f);
            var seconds = GetF(args, "seconds", 3f);
            if (seconds < 0.2f) seconds = 0.2f;
            if (seconds > 30f) seconds = 30f;
            BotInput.ThrottleVal = Math.Max(-1f, Math.Min(1f, throttle));
            BotInput.SteerVal = Math.Max(-1f, Math.Min(1f, steer));
            BotInput.VehicleUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
            BotInput.VehicleOverride = true;
            string veh = "none";
            try { if (Vehicle() != null) veh = "present"; } catch { }
            // DriveInput/CleaningEnabled are read-only state computed by the
            // vehicle from MoonInput - the getter patches above are the input.
            return "{\"driving\":true,\"throttle\":" + F(BotInput.ThrottleVal) + ",\"steer\":" + F(BotInput.SteerVal)
                + ",\"seconds\":" + F(seconds) + ",\"vehicle\":\"" + veh + "\"}";
        }

        private static string DriveTo(JsonElement args)
        {
            var veh = Vehicle() ?? throw new InvalidOperationException("no vehicle found (are you seated in one?)");
            var pos = veh.transform.position;
            var x = GetF(args, "x", pos.x);
            var z = GetF(args, "z", pos.z);
            var timeout = GetF(args, "timeout_s", 120f);
            var cleaning = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("cleaning", out var c) && c.ValueKind == JsonValueKind.True;
            var target3 = new Vector3(x, pos.y, z);
            try
            {
                // If the request points at a station/collector, aim at its real
                // 3D position (XZ alone is degenerate at this world's poles).
                foreach (var col in FindAll<MoonCollector>())
                {
                    var cp = col.transform.position;
                    var ddx = cp.x - x; var ddz = cp.z - z;
                    if (ddx * ddx + ddz * ddz < 900f) { target3 = cp; break; }
                }
            }
            catch { }
            var cruise = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("cruise", out var cr) && cr.ValueKind == JsonValueKind.True;
            var suctionFull = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("suction_full", out var sf) && sf.ValueKind == JsonValueKind.True;
            if (suctionFull) BotSuction.StartFull();
            BotAuto.Start(target3, timeout, cleaning, cruise);
            var dx = x - pos.x; var dz = z - pos.z;
            return "{\"driving_to\":true,\"target\":[" + F(x) + "," + F(z) + "],\"dist_m\":" + F((float)Math.Sqrt(dx * dx + dz * dz)) + ",\"cleaning\":" + cleaning.ToString().ToLowerInvariant() + "}";
        }

        private static string DriveStatus()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            Field(sb, "active", BotAuto.Active.ToString().ToLowerInvariant(), true);
            Field(sb, "status", Q(BotAuto.Status), false);
            Field(sb, "arrived", BotAuto.Arrived.ToString().ToLowerInvariant(), true);
            Field(sb, "recoveries", BotAuto.Recoveries.ToString(), true);
            if (BotAuto.ObstacleM > 0f)
            {
                Field(sb, "obstacle_m", F(BotAuto.ObstacleM), true);
                Field(sb, "obstacle", Q(BotAuto.ObstacleName), false);
            }
            var veh = Vehicle();
            if (veh != null && BotAuto.Active)
            {
                var p = veh.transform.position;
                var dx = BotAuto.Target.x - p.x; var dz = BotAuto.Target.z - p.z;
                Field(sb, "dist_m", F((float)Math.Sqrt(dx * dx + dz * dz)), true);
                try { Field(sb, "cleaning", veh.Cleaning.ToString().ToLowerInvariant(), true); } catch { }
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static string VehicleTool(JsonElement args)
        {
            var veh = Vehicle() ?? throw new InvalidOperationException("no vehicle in scene");
            var action = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("action", out var act) ? act.GetString() : "status";
            switch (action)
            {
                case "enter":
                {
                    var ok = veh.Enter();
                    return "{\"entered\":" + ok.ToString().ToLowerInvariant() + "}";
                }
                case "cruise":
                {
                    BotInput.PulseCruise();
                    return "{\"cruise_pulse_sent\":true,\"cruise_on\":" + veh.CruiseOn.ToString().ToLowerInvariant() + "}";
                }
                case "boost":
                {
                    BotInput.PulseBoost();
                    return "{\"boost_pulse_sent\":true}";
                }
                case "suction_full":
                {
                    BotSuction.StartFull();
                    return "{\"suction\":\"seeking max\",\"level_now\":" + veh.SuctionLevel + "}";
                }
                case "suction_level":
                {
                    var lvl = (int)GetF(args, "level", 1f);
                    BotSuction.StartLevel(lvl);
                    return "{\"suction\":\"seeking level " + lvl + "\",\"level_now\":" + veh.SuctionLevel + "}";
                }
                case "suction_step":
                {
                    var steps = (int)GetF(args, "steps", 1f);
                    BotInput.PulseSuctionStep(steps);
                    return "{\"suction_step_sent\":" + steps + ",\"level_now\":" + veh.SuctionLevel + "}";
                }
                case "board":
                {
                    BotInput.PulseInteract();
                    return "{\"interact_pulsed\":true,\"note\":\"stand at the truck door, facing it; if boarding works the player enters within a second\"}";
                }
                case "lights":
                {
                    veh.ToggleLights();
                    return "{\"lights_toggled\":true}";
                }
                default:
                {
                    var sb = new StringBuilder();
                    sb.Append('{');
                    Field(sb, "suction", veh.Suction.ToString(), true);
                    Field(sb, "suction_level", veh.SuctionLevel.ToString(), true);
                    Field(sb, "suction_adjustable", veh.SuctionAdjustable.ToString().ToLowerInvariant(), true);
                    Field(sb, "suction_goal", Q(BotSuction.Status), false);
                    Field(sb, "cruise_fitted", veh.CruiseFitted.ToString().ToLowerInvariant(), true);
                    Field(sb, "cruise_on", veh.CruiseOn.ToString().ToLowerInvariant(), true);
                    Field(sb, "cruise_speed", F(veh.CruiseSpeed), true);
                    Field(sb, "cleaning", veh.Cleaning.ToString().ToLowerInvariant(), true);
                    Field(sb, "drive_input", F(veh.DriveInput), true);
                    sb.Append('}');
                    return sb.ToString();
                }
            }
        }

        private static string Sense(JsonElement args)
        {
            var walker = Walker() ?? throw new InvalidOperationException("no player walker in scene yet");
            var me = walker.transform.position;
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"pos\":[").Append(F(me.x)).Append(',').Append(F(me.y)).Append(',').Append(F(me.z)).Append(']');
            // Forward obstacle cast from the walker/vehicle viewpoint.
            try
            {
                var veh = Vehicle();
                var origin = veh != null ? veh.transform.position : me;
                var fwd = veh != null ? veh.transform.forward : walker.transform.forward;
                var up = origin.sqrMagnitude > 1f ? origin.normalized : Vector3.up;
                var o = origin + up * 1.6f + fwd * 1.5f;
                if (Physics.SphereCast(o, 1.6f, fwd, out var hit, 30f))
                {
                    sb.Append(",\"ahead\":{\"dist_m\":").Append(F(hit.distance))
                      .Append(",\"object\":").Append(Q(hit.collider != null ? hit.collider.gameObject.name : "?"))
                      .Append(",\"normal_y\":").Append(F(hit.normal.y)).Append('}');
                }
                else sb.Append(",\"ahead\":null");
            }
            catch (Exception e) { sb.Append(",\"sense_error\":").Append(Q(e.Message)); }
            sb.Append(",\"autopilot\":{\"active\":").Append(BotAuto.Active.ToString().ToLowerInvariant())
              .Append(",\"status\":").Append(Q(BotAuto.Status))
              .Append(",\"recoveries\":").Append(BotAuto.Recoveries).Append('}');
            sb.Append(",\"walk\":{\"active\":").Append(BotDrive.Active.ToString().ToLowerInvariant())
              .Append(",\"status\":").Append(Q(BotDrive.Status)).Append('}');
            sb.Append('}');
            return sb.ToString();
        }

        private static string JoinLobby(JsonElement args)
        {
            string raw = null;
            if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("lobby_id", out var v))
                raw = v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText();
            if (string.IsNullOrEmpty(raw)) throw new ArgumentException("join_lobby needs {lobby_id: \"<steam lobby id>\"}");
            var id = ulong.Parse(raw.Trim().Trim('"'));
            MoonSteamLobby.Join(id);
            return "{\"join_requested\":\"" + id + "\"}";
        }

        private static string PrettyMoney(string raw)
        {
            return decimal.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var dv)
                ? dv.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : raw;
        }

        private static List<T> FindAll<T>() where T : UnityEngine.Object
        {
            var list = new List<T>();
            var found = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<T>(), true);
            if (found != null)
                foreach (var o in found)
                {
                    var c = o != null ? o.TryCast<T>() : null;
                    if (c != null) list.Add(c);
                }
            return list;
        }

        private static string Screenshot(JsonElement args)
        {
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            if (tex == null) throw new InvalidOperationException("screenshot capture returned null");
            Texture2D outTex = tex;
            RenderTexture rt = null;
            try
            {
                var scale = GetF(args, "scale", 0.5f);
                if (scale > 0.01f && scale < 0.99f)
                {
                    var w = Math.Max(64, (int)(tex.width * scale));
                    var h = Math.Max(64, (int)(tex.height * scale));
                    rt = RenderTexture.GetTemporary(w, h, 0);
                    Graphics.Blit(tex, rt);
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    outTex = new Texture2D(w, h, TextureFormat.RGB24, false);
                    outTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    outTex.Apply();
                    RenderTexture.active = prev;
                }
                var png = ImageConversion.EncodeToPNG(outTex);
                if (png == null) throw new InvalidOperationException("PNG encode failed");
                var bytes = new byte[png.Length];
                for (var i = 0; i < png.Length; i++) bytes[i] = png[i];
                return "IMAGE:" + Convert.ToBase64String(bytes);
            }
            finally
            {
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (outTex != tex && outTex != null) UnityEngine.Object.Destroy(outTex);
                UnityEngine.Object.Destroy(tex);
            }
        }

        private static string Radar(JsonElement args)
        {
            var walker = Walker() ?? throw new InvalidOperationException("no player walker in scene yet");
            var me = walker.transform.position;
            var fwd = walker.transform.forward;
            var radius = GetF(args, "radius", 80f);
            var sb = new StringBuilder();
            sb.Append("{\"origin\":[").Append(F(me.x)).Append(',').Append(F(me.y)).Append(',').Append(F(me.z)).Append("],\"radius\":").Append(F(radius)).Append(",\"contacts\":[");
            var first = true;
            Scan<MoonWalker>(sb, ref first, "crew", me, fwd, radius, walker);
            Scan<MoonRobotVacuum>(sb, ref first, "robot", me, fwd, radius, null);
            Scan<MoonCollector>(sb, ref first, "collector", me, fwd, radius, null);
            Scan<MoonRobotDock>(sb, ref first, "robot_dock", me, fwd, radius, null);
            Scan<MoonPrototypeVehicle>(sb, ref first, "vehicle", me, fwd, radius, null);
            Scan<MoonDustStation>(sb, ref first, "station", me, fwd, radius, null);
            ScanRemotePlayers(sb, ref first, me, fwd, radius);
            ScanRemoteVehicles(sb, ref first, me, fwd, radius);

            sb.Append("]}");
            return sb.ToString();
        }

        private static void Scan<T>(StringBuilder sb, ref bool first, string kind, Vector3 me, Vector3 fwd, float radius, UnityEngine.Object skip) where T : UnityEngine.Object
        {
            foreach (var o in FindAll<T>())
            {
                var comp = o.TryCast<Component>();
                if (comp == null) continue;
                if (skip != null && comp.gameObject == ((Component)skip).gameObject) continue;
                var p = comp.transform.position;
                var dx = p.x - me.x; var dz = p.z - me.z;
                var dist = Math.Sqrt(dx * dx + dz * dz);
                if (dist > radius) continue;
                var bearing = Math.Atan2(fwd.x * dz - fwd.z * dx, fwd.x * dx + fwd.z * dz) * 180.0 / Math.PI;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"kind\":\"").Append(kind).Append("\",\"name\":").Append(Q(comp.gameObject.name))
                  .Append(",\"dist\":").Append(F((float)dist))
                  .Append(",\"bearing_deg\":").Append(F((float)bearing))
                  .Append(",\"pos\":[").Append(F(p.x)).Append(',').Append(F(p.y)).Append(',').Append(F(p.z)).Append("]}");
            }
        }

        private static void ScanRemotePlayers(StringBuilder sb, ref bool first, Vector3 me, Vector3 fwd, float radius)
        {
            foreach (var p in FindAll<MoonRemotePlayer>())
            {
                try
                {
                    var comp = p.TryCast<Component>();
                    if (comp == null) continue;
                    var pos = comp.transform.position;
                    var dx = pos.x - me.x; var dz = pos.z - me.z;
                    var dist = Math.Sqrt(dx * dx + dz * dz);
                    if (dist > radius) continue;
                    var bearing = Math.Atan2(fwd.x * dz - fwd.z * dx, fwd.x * dx + fwd.z * dz) * 180.0 / Math.PI;
                    if (!first) sb.Append(',');
                    first = false;
                    string name = "?";
                    try { name = p.DisplayName ?? comp.gameObject.name; } catch { name = comp.gameObject.name; }
                    sb.Append("{\"kind\":\"crew\",\"remote\":true,\"name\":").Append(Q(name))
                      .Append(",\"steam_id\":").Append(Q(p.SteamId.ToString()))
                      .Append(",\"dist\":").Append(F((float)dist))
                      .Append(",\"bearing_deg\":").Append(F((float)bearing))
                      .Append(",\"pos\":[").Append(F(pos.x)).Append(',').Append(F(pos.y)).Append(',').Append(F(pos.z)).Append("]}");
                }
                catch { }
            }
        }

        private static void ScanRemoteVehicles(StringBuilder sb, ref bool first, Vector3 me, Vector3 fwd, float radius)
        {
            foreach (var v in FindAll<MoonRemoteVehicle>())
            {
                try
                {
                    var comp = v.TryCast<Component>();
                    if (comp == null) continue;
                    var pos = comp.transform.position;
                    var dx = pos.x - me.x; var dz = pos.z - me.z;
                    var dist = Math.Sqrt(dx * dx + dz * dz);
                    if (dist > radius) continue;
                    var bearing = Math.Atan2(fwd.x * dz - fwd.z * dx, fwd.x * dx + fwd.z * dz) * 180.0 / Math.PI;
                    if (!first) sb.Append(',');
                    first = false;
                    string owner = "";
                    string crewName = "";
                    try { owner = v.OwnerLabel ?? ""; } catch { }
                    try { if (v.Crewmate != null) crewName = v.Crewmate.DisplayName ?? ""; } catch { }
                    var name = !string.IsNullOrEmpty(crewName) ? crewName : (!string.IsNullOrEmpty(owner) ? owner : comp.gameObject.name);
                    sb.Append("{\"kind\":\"remote_vehicle\",\"remote\":true,\"name\":").Append(Q(name))
                      .Append(",\"owner\":").Append(Q(owner))
                      .Append(",\"dist\":").Append(F((float)dist))
                      .Append(",\"bearing_deg\":").Append(F((float)bearing))
                      .Append(",\"pos\":[").Append(F(pos.x)).Append(',').Append(F(pos.y)).Append(',').Append(F(pos.z)).Append("]}");
                }
                catch { }
            }
        }

        public const string GuideText = @"MOON SWEEPERS — AGENT PLAY GUIDE
You are a crew member in Moon Sweepers, a co-op moon-cleaning game, playing through this MCP mod.
THE LOOP: The moon is covered in dust (96% coverage). Sweep dust with your brush — swept dust becomes cargo weight you carry. Carry it back to the Collector and deposit: dust sells at $1.00 per kg into the team wallet. Fully cleaning a 25 m surface section pays a $10 bonus. Earn, then spend the wallet on depot structures and better tools from the build catalogue (193 buildable definitions).
TOOLS: You start with a Toothbrush. Wider tools (Hand Brush, Nailbrush, Household Broom, Wide Broom, Extra Wide Broom) sweep more per stroke. Equip by name with the equip tool. The Building Tool is for construction.
AUTOMATION: A Robot Vacuum (Mk I: 15 kg bin, 1.35 m/s, 1.3 m cleaning width) cleans on its own and unloads at its dock (dock holds 300 kg, auto-sells at 50 kg+).
MOVEMENT: Run speed 7 m/s, moon gravity 1.62. World is a 2000 m radius disc; spawn is a crater. Use state for your position, radar to find crew/robots/the Collector, screenshot to SEE through the game camera before deciding.
HOW TO PLAY VIA MCP: 1) state — where am I, what do I hold, wallet/deposited. 2) radar — what is near me and which bearing. 3) screenshot — look before you act. 4) face + sweep in short bursts (the brush strokes on a 0.12 s cadence); re-check state as cargo builds. 5) deposit — travels you to the Collector; confirm via state (deposited_kg up, wallet up). 6) Repeat; suggest purchases to the host when the wallet grows.
MULTIPLAYER: The host's game holds the Steam lobby; each bot instance joins as crew. This MCP drives the local player of the game it is plugged into. Be a good crew mate: don't spend the team wallet without the host's say-so.
WALKING (v0.6): NEVER teleport unless the host explicitly orders it. Move with walk_to {x, z} — the character walks with the game's own physics. Poll walk_status until status is arrived/timeout. walk_stop halts. Scrub where you stand with face+sweep; when a patch dries up (sweep returns taper to 0), walk a few metres to fresh dust.
DEPOSIT (v0.6): deposit walks you to the Collector station and dwells while the game's own intake runs; poll walk_status for status=deposited and its deposit_report (cargo before/after), then confirm cargo_kg dropped in state. TravelTo-based deposit is gone — it returned true without moving.
DRIVING (v0.6): when seated in a vehicle, drive {throttle: -1..1, steer: -1..1, seconds} (time-boxed, max 30 s per call; drive_stop cuts it). Add cleaning:true to run the sweeper. state shows the vehicle block (position, drive_input, cruise, cleaning) while one is near/occupied.
JOINING (v0.6): join_lobby {lobby_id} joins a friend's Steam lobby by id; leave_lobby leaves. state/host_info confirm in_lobby and is_host after joining.
FOLLOWING PEOPLE (v0.6.5): radar now reports remote players as kind=crew with remote:true, DisplayName, steam_id and live position (MoonRemotePlayer), and their vehicles as kind=remote_vehicle (MoonRemoteVehicle). To follow a person, poll radar, pick the crew contact by name/steam_id, and re-issue drive_to at their current position whenever they move. WALKING (v0.6.3): the character reads a Unity InputAction, not the MoonInput stick - the mod overrides the walker's own move action reads while walk_to is active; vehicle {action: board} pulses the walker's interact action (experimental boarding: stand at the truck, face it). VEHICLE CONTROLS (v0.6.2): vehicle {action: enter} puts the player in the truck; {action: cruise} pulses cruise control on; {action: suction_full} drives the suction level to its maximum (suction levels only change via input steps; the bot pulses and reads back); drive_to accepts cruise:true and suction_full:true to combine everything for a run. SELF-DRIVING (v0.6): drive_to {x, z, cleaning:true} is the FSD mode for the sweeper truck. v0.6.1 adds surface-tangent steering (the world is a sphere; flat steering breaks at the poles), stuck detection with reverse recovery, obstacle sensing (use the sense tool to see what the bot sees; radar now also reports vehicles, stations and dropped canisters). - it steers and throttles itself to the target with the game's own physics, running the sweeper when cleaning is set. Poll drive_status; drive_stop cuts it instantly. For hand-driving, drive {throttle, steer, seconds} gives time-boxed manual input.";

        private static float GetF(JsonElement args, string key, float dflt)
            => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
                ? (float)v.GetDouble() : dflt;

        private static string F(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        private static string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        private static void Field(StringBuilder sb, string name, string value, bool raw)
        {
            if (sb.Length > 1) sb.Append(',');
            sb.Append('"').Append(name).Append("\":").Append(raw ? value : value);
        }
    }

    internal class McpServer
    {
        public void Run()
        {
            TcpListener listener = null;
            while (listener == null)
            {
                try { listener = new TcpListener(IPAddress.Loopback, Plugin.Port); listener.Start(); }
                catch { Thread.Sleep(2000); }
            }
            Plugin.Log.LogInfo("MCP listening on 127.0.0.1:" + Plugin.Port);
            while (true)
            {
                var client = listener.AcceptTcpClient();
                var t = new Thread(() => HandleClient(client)) { IsBackground = true };
                t.Start();
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Trim().Length == 0) continue;
                    var reply = HandleLine(line);
                    if (reply != null) writer.WriteLine(reply);
                }
            }
            catch { /* client went away */ }
            finally { try { client.Close(); } catch { } }
        }

        private string HandleLine(string line)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch { return Error(null, -32700, "parse error"); }
            var root = doc.RootElement;
            var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
            var hasId = root.TryGetProperty("id", out var idEl);
            var id = hasId ? idEl.GetRawText() : null;
            if (method == null) return Error(id, -32600, "no method");
            if (method.StartsWith("notifications/")) return null;
            switch (method)
            {
                case "initialize":
                {
                    var init = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"moonsweepers-game-mcp\",\"version\":\"0.6.13\"},\"instructions\":" + JsonSerializer.Serialize(GameTools.GuideText) + "}}";
                    try { using var _doc = JsonDocument.Parse(init); return init; }
                    catch { return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"moonsweepers-game-mcp\",\"version\":\"0.6.13\"}}}"; }
                }
                case "ping":
                    return Ok(id, "{}");
                case "tools/list":
                    return Ok(id, "{\"tools\":" + ToolsJson + "}");
                case "tools/call":
                {
                    var p = root.GetProperty("params");
                    var name = p.GetProperty("name").GetString();
                    var args = p.TryGetProperty("arguments", out var a) ? a : default;
                    var call = new PendingCall { Tool = name, Args = args };
                    MainThreadPump.Queue.Enqueue(call);
                    if (!call.Done.Wait(TimeSpan.FromSeconds(20)))
                        return Ok(id, Content("error: timed out waiting for the game main thread (is a session loaded?)", true));
                    if (call.Error != null) return Ok(id, Content("error: " + call.Error, true));
                    var res = call.Result ?? "{}";
                    if (res.StartsWith("IMAGE:")) return Ok(id, "{\"content\":[{\"type\":\"image\",\"data\":\"" + res.Substring(6) + "\",\"mimeType\":\"image/png\"}],\"isError\":false}");
                    return Ok(id, Content(res, false));
                }
                default:
                    return Error(id, -32601, "method not found: " + method);
            }
        }

        private static string Ok(string id, string resultJson)
            => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + resultJson + "}";
        private static string Error(string id, int code, string msg)
            => "{\"jsonrpc\":\"2.0\",\"id\":" + (id ?? "null") + ",\"error\":{\"code\":" + code + ",\"message\":" + JsonSerializer.Serialize(msg) + "}}";
        private static string Content(string text, bool isError)
            => "{\"content\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text) + "}],\"isError\":" + (isError ? "true" : "false") + "}";

        private const string ToolsJson = @"[{""name"":""state"",""description"":""Live game state: lobby/host flags, player position, held tool, cargo kg/capacity/pending-deposit from the dust inventory, team wallet, deposited kg, vehicle block when in/near one, walk status."",""inputSchema"":{""type"":""object"",""properties"":{}}},{""name"":""host_info"",""description"":""Steam lobby info: in_lobby, is_host, lobby_id, host_id, crew_name, all_ready."",""inputSchema"":{""type"":""object"",""properties"":{}}},{""name"":""teleport"",""description"":""Teleport the local player. HOST-ORDER ONLY - the house rule is walk, never teleport. Args: x, z (y optional)."",""inputSchema"":{""type"":""object"",""properties"":{""x"":{""type"":""number""},""z"":{""type"":""number""},""y"":{""type"":""number""}}}},{""name"":""face"",""description"":""Turn the local player to a compass yaw in degrees (0 = +Z/north, 90 = +X/east)."",""inputSchema"":{""type"":""object"",""properties"":{""yaw_degrees"":{""type"":""number""}}}},{""name"":""sweep"",""description"":""Run the equipped brush for the given seconds along a downward ray in front of the player. Returns dust-cell edit count; a patch dries up as it is cleaned."",""inputSchema"":{""type"":""object"",""properties"":{""seconds"":{""type"":""number""}}}},{""name"":""equip"",""description"":""Equip a tool by part of its display name via the toolbelt + MoonPrototypeEquipment.EquipTool (the real in-hand switch)."",""inputSchema"":{""type"":""object"",""properties"":{""tool"":{""type"":""string""}}}},{""name"":""deposit"",""description"":""Walk to the Collector station and deposit: starts a walk drive + dwell at the intake. Poll walk_status for status=deposited and the cargo before/after report, then verify cargo_kg in state."",""inputSchema"":{""type"":""object"",""properties"":{}}},{""name"":""tools"",""description"":""Toolbelt order with indices + active index, every tool definition by name, and the currently equipped one."",""inputSchema"":{""type"":""object"",""properties"":{}}},{""name"":""screenshot"",""description"":""See through the game camera: returns the current frame as an inline PNG image. Optional scale arg (default 0.5)."",""inputSchema"":{""type"":""object"",""properties"":{""scale"":{""type"":""number""}}}},{""name"":""radar"",""description"":""Radar: contacts within radius (default 80 m) - crew, robots, collectors, robot docks, vehicles, stations - each with distance and relative bearing."",""inputSchema"":{""type"":""object"",""properties"":{""radius"":{""type"":""number""}}}},{""name"":""guide"",""description"":""The agent play guide for Moon Sweepers."",""inputSchema"":{""type"":""object"",""properties"":{}}},{""name"":""crew"",""description"":""Lobby crew list: names, Steam ids, ready/host/local flags, local member index."",""inputSchema"":{""type"":""object"",""properties"":{}}},{""name"":""save_game"",""description"":""Ask the game to save (MoonWorld.RequestSave)."",""inputSchema"":{""type"":""object"",""properties"":{}}},{""name"":""return_to_depot"",""description"":""Send the local player back to the depot (MoonWalker.ReturnToDepot)."",""inputSchema"":{""type"":""object"",""properties"":{}}},{""name"":""buy"",""description"":""Buy through the game's own purchase path (MoonWorld.CrewPurchase). Get the host's OK first. Args: kind Tool|Container|TreeNode|VehicleUpgrade, id, level."",""inputSchema"":{""type"":""object"",""properties"":{""kind"":{""type"":""string""},""id"":{""type"":""string""},""level"":{""type"":""number""}}}},{""name"":""walk_to"",""description"":""Walk to a world position (x, z) with the game's own locomotion - no teleporting. Returns immediately; poll walk_status."",""inputSchema"":{""type"":""object"",""properties"":{""x"":{""type"":""number""},""z"":{""type"":""number""},""timeout_s"":{""type"":""number""}}}},{""name"":""walk_status"",""description"":""Walk/deposit drive status: active, status (walking/arrived/timeout/depositing/deposited), distance remaining, last deposit report."",""inputSchema"":{""type"":""object"",""properties"":{}}},{""name"":""walk_stop"",""description"":""Stop the current walk/deposit drive."",""inputSchema"":{""type"":""object"",""properties"":{}}},{""name"":""drive"",""description"":""Drive the vehicle you are seated in: throttle -1..1, steer -1..1, seconds (time-boxed, max 30). Optional cleaning:true runs the sweeper."",""inputSchema"":{""type"":""object"",""properties"":{""throttle"":{""type"":""number""},""steer"":{""type"":""number""},""seconds"":{""type"":""number""},""cleaning"":{""type"":""boolean""}}}},{""name"":""drive_stop"",""description"":""Cut vehicle throttle/steer immediately."",""inputSchema"":{""type"":""object"",""properties"":{}}},{""name"":""join_lobby"",""description"":""Join a friend's Steam lobby by id (string to preserve precision)."",""inputSchema"":{""type"":""object"",""properties"":{""lobby_id"":{""type"":""string""}}}},{""name"":""leave_lobby"",""description"":""Leave the current Steam lobby."",""inputSchema"":{""type"":""object"",""properties"":{}}},{""name"":""drive_to"",""description"":""SELF-DRIVING: drive the vehicle you are seated in to a world position (x, z) - autonomous steering + throttle with the game's own physics. cleaning:true runs the sweeper on the way. Poll drive_status."",""inputSchema"":{""type"":""object"",""properties"":{""x"":{""type"":""number""},""z"":{""type"":""number""},""timeout_s"":{""type"":""number""},""cleaning"":{""type"":""boolean""},""cruise"":{""type"":""boolean""},""suction_full"":{""type"":""boolean""}}}},{""name"":""drive_status"",""description"":""Autopilot status: active, status (driving/arrived/timeout), distance remaining, cleaning on/off."",""inputSchema"":{""type"":""object"",""properties"":{}}},{""name"":""sense"",""description"":""Perception snapshot: forward obstacle cast (distance, object, slope), autopilot and walk status. The bot's situational awareness."",""inputSchema"":{""type"":""object"",""properties"":{}}},{""name"":""vehicle"",""description"":""Vehicle controls: action status (default), enter (put the player in the truck), cruise (pulse cruise control on), boost (pulse the boost - use it when closing a big gap), suction_full (drive suction level to max), suction_level {level:0-3} (step suction to a set level), suction_step {steps} (raw signed suction step), lights."",""inputSchema"":{""type"":""object"",""properties"":{""action"":{""type"":""string""}}}}]";
    }
}
