using System;
using CupheadOnline.Net;
using HarmonyLib;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Wire format of one controller state. Action ids are Cuphead's Rewired action ids (CupheadButton values).
    /// Each button carries a press counter so taps shorter than a network frame are never lost.
    /// </summary>
    public sealed class PadState
    {
        public const int ActionCount = 27;            // CupheadButton.Swap == 26 is the highest real action id
        public static readonly int[] AxisActions = { 0, 1, 22, 23 }; // MoveHorizontal, MoveVertical, MenuHorizontal, MenuVertical

        public uint Held;
        public readonly byte[] Presses = new byte[ActionCount];
        public readonly float[] Axes = new float[AxisActions.Length];

        public static int AxisSlot(int actionId)
        {
            for (int i = 0; i < AxisActions.Length; i++) if (AxisActions[i] == actionId) return i;
            return -1;
        }

        public void Write(NetWriter w)
        {
            w.U32(Held);
            w.Bytes(Presses, 0, ActionCount);
            for (int i = 0; i < Axes.Length; i++)
            {
                w.I8((sbyte)Mathf.RoundToInt(Mathf.Clamp(Axes[i], -1f, 1f) * 127f));
            }
        }

        public void Read(NetReader r)
        {
            Held = r.U32();
            for (int i = 0; i < ActionCount; i++) Presses[i] = r.U8();
            for (int i = 0; i < Axes.Length; i++) Axes[i] = r.I8() / 127f;
        }
    }

    /// <summary>
    /// Turns a stream of PadStates (latest wins) into per-frame held/down/up state, the way Rewired reports it.
    /// Press counters guarantee a tap shorter than one frame/packet still produces exactly one down edge.
    /// Like Rewired, the Update and FixedUpdate loops keep separate edge state: Cuphead polls SwitchWeapon, Jump,
    /// Dash, Super, parry and Accept-to-interact with GetButtonDown inside FixedUpdate, and a press consumed by
    /// the rendered frame must still read as down in the first fixed step after it.
    /// </summary>
    public sealed class VirtualPad
    {
        public readonly PadState Latest = new PadState();
        private readonly Edges frameLoop = new Edges();
        private readonly Edges fixedLoop = new Edges();

        private sealed class Edges
        {
            /// <summary>Presses that waited this long (realtime) for a fixed step are dropped, e.g. menu taps while paused.</summary>
            private const float StaleAfter = 0.25f;

            public readonly byte[] SeenPresses = new byte[PadState.ActionCount];
            public readonly bool[] Held = new bool[PadState.ActionCount];
            public readonly bool[] Down = new bool[PadState.ActionCount];
            public readonly bool[] Up = new bool[PadState.ActionCount];
            public readonly float[] PressStart = new float[PadState.ActionCount];
            private double computedStep = -1;
            private float lastAdvance = -1f;

            public void Reset(PadState latest)
            {
                computedStep = -1;
                Array.Clear(Held, 0, Held.Length);
                Array.Clear(Down, 0, Down.Length);
                Array.Clear(Up, 0, Up.Length);
                Buffer.BlockCopy(latest.Presses, 0, SeenPresses, 0, SeenPresses.Length);
            }

            public void Advance(PadState latest, double step, bool dropStale)
            {
                if (step == computedStep) return;
                computedStep = step;
                float now = Time.unscaledTime;
                if (dropStale && now - lastAdvance > StaleAfter) Buffer.BlockCopy(latest.Presses, 0, SeenPresses, 0, SeenPresses.Length);
                lastAdvance = now;
                for (int i = 0; i < PadState.ActionCount; i++)
                {
                    bool newPress = latest.Presses[i] != SeenPresses[i];
                    SeenPresses[i] = latest.Presses[i];
                    bool want = (latest.Held & (1u << i)) != 0 || newPress;
                    bool was = Held[i];
                    Down[i] = (want && !was) || (newPress && was);
                    Up[i] = !want && was;
                    Held[i] = want;
                    if (Down[i]) PressStart[i] = now;
                }
            }
        }

        public void Reset()
        {
            Latest.Held = 0;
            Array.Clear(Latest.Axes, 0, Latest.Axes.Length);
            frameLoop.Reset(Latest);
            fixedLoop.Reset(Latest);
        }

        /// <summary>Accept the current counters as already seen (don't replay old presses).</summary>
        public void SyncPresses()
        {
            Buffer.BlockCopy(Latest.Presses, 0, frameLoop.SeenPresses, 0, PadState.ActionCount);
            Buffer.BlockCopy(Latest.Presses, 0, fixedLoop.SeenPresses, 0, PadState.ActionCount);
        }

        /// <summary>The edge state for the loop that is asking: one step per rendered frame or per fixed step.</summary>
        private Edges Advance()
        {
            if (Time.inFixedTimeStep)
            {
                fixedLoop.Advance(Latest, Time.fixedTime, true);
                return fixedLoop;
            }
            frameLoop.Advance(Latest, Time.frameCount, false);
            return frameLoop;
        }

        private static bool InRange(int id) { return id >= 0 && id < PadState.ActionCount; }

        public bool Button(int id) { Edges e = Advance(); return InRange(id) && e.Held[id]; }
        public bool ButtonDown(int id) { Edges e = Advance(); return InRange(id) && e.Down[id]; }
        public bool ButtonUp(int id) { Edges e = Advance(); return InRange(id) && e.Up[id]; }

        public float Axis(int id)
        {
            int slot = PadState.AxisSlot(id);
            return slot < 0 ? 0f : Latest.Axes[slot];
        }

        public float TimePressed(int id)
        {
            Edges e = Advance();
            return InRange(id) && e.Held[id] ? Time.unscaledTime - e.PressStart[id] : 0f;
        }

        public bool AnyButton()
        {
            Edges e = Advance();
            for (int i = 2; i < PadState.ActionCount; i++) if (e.Held[i]) return true;
            return false;
        }

        public bool AnyButtonDown()
        {
            Edges e = Advance();
            for (int i = 2; i < PadState.ActionCount; i++) if (e.Down[i]) return true;
            return false;
        }

        public bool AnyButtonUp()
        {
            Edges e = Advance();
            for (int i = 2; i < PadState.ActionCount; i++) if (e.Up[i]) return true;
            return false;
        }
    }

    /// <summary>
    /// Client side: samples the local player's controller (Rewired player 0) every frame and sends it.
    /// Host side: the received PadStates drive Rewired player 1 (Mugman) through a VirtualPad.
    /// </summary>
    public static class RemoteInput
    {
        /// <summary>When true, Rewired calls pass through untouched (used while we sample real input ourselves).</summary>
        [ThreadStatic] public static bool Sampling;

        /// <summary>True while the host should feed remote input into Rewired player 1.</summary>
        public static bool DriveHostPlayerTwo;

        /// <summary>True while a mirroring client must not let the local game see any input.</summary>
        public static bool BlockLocalGame;

        public static readonly VirtualPad Remote = new VirtualPad();

        /// <summary>Optional scripted pad (test driver): replaces local sampling on the client, drives P1 on the host.</summary>
        public static PadState Scripted;

        // ---- client sampling ----
        private static readonly PadState local = new PadState();
        private static uint localSeq;

        public static void SampleAndSend(Connection connection)
        {
            PadState state = Scripted ?? SampleLocal();
            if (state == null) return;
            NetWriter w = new NetWriter(48);
            w.U32(++localSeq);
            state.Write(w);
            connection.SendUnreliable(MsgType.Input, w);
        }

        private static PadState SampleLocal()
        {
            Rewired.Player player;
            try { player = Rewired.ReInput.players.GetPlayer(0); }
            catch { return null; }
            if (player == null) return null;
            Sampling = true;
            try
            {
                uint held = 0;
                for (int i = 0; i < PadState.ActionCount; i++)
                {
                    if (i == 9 || i == 10 || i == 17 || i == 24 || i == 25) continue; // unused ids
                    if (player.GetButton(i)) held |= 1u << i;
                    if (player.GetButtonDown(i)) local.Presses[i]++;
                }
                local.Held = held;
                for (int i = 0; i < PadState.AxisActions.Length; i++)
                {
                    local.Axes[i] = player.GetAxis(PadState.AxisActions[i]);
                }
            }
            finally
            {
                Sampling = false;
            }
            return local;
        }

        // ---- host application ----
        private static uint latestSeq;
        private static bool hasRemote;

        public static void ResetHost()
        {
            latestSeq = 0;
            hasRemote = false;
            Remote.Reset();
        }

        private static double lastInputTime;
        private static bool released;

        /// <summary>Link hiccup: don't leave Mugman running/shooting on the last packet for the whole timeout.</summary>
        public static void ReleaseIfStale(double now)
        {
            if (!hasRemote || released || now - lastInputTime < 0.25) return;
            released = true;
            Remote.Latest.Held = 0;
            Array.Clear(Remote.Latest.Axes, 0, Remote.Latest.Axes.Length);
        }

        public static void OnInputMessage(NetReader r, double now)
        {
            lastInputTime = now;
            released = false;
            uint seq = r.U32();
            if (hasRemote && (int)(seq - latestSeq) <= 0) return; // stale / reordered
            Remote.Latest.Read(r);
            if (!hasRemote)
            {
                Remote.SyncPresses(); // don't replay presses from before we connected
                hasRemote = true;
            }
            latestSeq = seq;
        }

        /// <summary>Which virtual pad (if any) answers for this Rewired player right now.</summary>
        internal static VirtualPad PadFor(Rewired.Player p)
        {
            if (p == null) return null;
            if (DriveHostPlayerTwo && p.id == 1) return Remote;
            if (TestDriver.HostPad != null && p.id == 0) return TestDriver.HostPad;
            return null;
        }
    }

    /// <summary>
    /// Rewired.Player is the single input choke point (observed via REA: Rewired_Core.dll Rewired.Player
    /// GetButton/GetButtonDown/GetButtonUp/GetAxis(int) and GetAnyButton*(); Cuphead's PlayerInput.actions
    /// and PlayerManager's join logic call these on ReInput.players.GetPlayer(0/1)).
    /// </summary>
    [HarmonyPatch]
    internal static class RewiredPatches
    {
        private static bool Block(Rewired.Player p)
        {
            return RemoteInput.BlockLocalGame && !RemoteInput.Sampling;
        }

        [HarmonyPatch(typeof(Rewired.Player), "GetButton", new[] { typeof(int) }), HarmonyPrefix]
        private static bool GetButton(Rewired.Player __instance, int actionId, ref bool __result)
        {
            if (RemoteInput.Sampling) return true;
            VirtualPad pad = RemoteInput.PadFor(__instance); if (pad != null) { __result = pad.Button(actionId); return false; }
            if (Block(__instance)) { __result = false; return false; }
            return true;
        }

        [HarmonyPatch(typeof(Rewired.Player), "GetButtonDown", new[] { typeof(int) }), HarmonyPrefix]
        private static bool GetButtonDown(Rewired.Player __instance, int actionId, ref bool __result)
        {
            if (RemoteInput.Sampling) return true;
            VirtualPad pad = RemoteInput.PadFor(__instance); if (pad != null) { __result = pad.ButtonDown(actionId); return false; }
            if (Block(__instance)) { __result = false; return false; }
            return true;
        }

        [HarmonyPatch(typeof(Rewired.Player), "GetButtonUp", new[] { typeof(int) }), HarmonyPrefix]
        private static bool GetButtonUp(Rewired.Player __instance, int actionId, ref bool __result)
        {
            if (RemoteInput.Sampling) return true;
            VirtualPad pad = RemoteInput.PadFor(__instance); if (pad != null) { __result = pad.ButtonUp(actionId); return false; }
            if (Block(__instance)) { __result = false; return false; }
            return true;
        }

        [HarmonyPatch(typeof(Rewired.Player), "GetAxis", new[] { typeof(int) }), HarmonyPrefix]
        private static bool GetAxis(Rewired.Player __instance, int actionId, ref float __result)
        {
            if (RemoteInput.Sampling) return true;
            VirtualPad pad = RemoteInput.PadFor(__instance); if (pad != null) { __result = pad.Axis(actionId); return false; }
            if (Block(__instance)) { __result = 0f; return false; }
            return true;
        }

        [HarmonyPatch(typeof(Rewired.Player), "GetAxisRaw", new[] { typeof(int) }), HarmonyPrefix]
        private static bool GetAxisRaw(Rewired.Player __instance, int actionId, ref float __result)
        {
            if (RemoteInput.Sampling) return true;
            VirtualPad pad = RemoteInput.PadFor(__instance); if (pad != null) { __result = pad.Axis(actionId); return false; }
            if (Block(__instance)) { __result = 0f; return false; }
            return true;
        }

        [HarmonyPatch(typeof(Rewired.Player), "GetButtonTimePressed", new[] { typeof(int) }), HarmonyPrefix]
        private static bool GetButtonTimePressed(Rewired.Player __instance, int actionId, ref float __result)
        {
            if (RemoteInput.Sampling) return true;
            VirtualPad pad = RemoteInput.PadFor(__instance); if (pad != null) { __result = pad.TimePressed(actionId); return false; }
            if (Block(__instance)) { __result = 0f; return false; }
            return true;
        }

        [HarmonyPatch(typeof(Rewired.Player), "GetAnyButton"), HarmonyPrefix]
        private static bool GetAnyButton(Rewired.Player __instance, ref bool __result)
        {
            if (RemoteInput.Sampling) return true;
            VirtualPad pad = RemoteInput.PadFor(__instance); if (pad != null) { __result = pad.AnyButton(); return false; }
            if (Block(__instance)) { __result = false; return false; }
            return true;
        }

        [HarmonyPatch(typeof(Rewired.Player), "GetAnyButtonDown"), HarmonyPrefix]
        private static bool GetAnyButtonDown(Rewired.Player __instance, ref bool __result)
        {
            if (RemoteInput.Sampling) return true;
            VirtualPad pad = RemoteInput.PadFor(__instance); if (pad != null) { __result = pad.AnyButtonDown(); return false; }
            if (Block(__instance)) { __result = false; return false; }
            return true;
        }

        [HarmonyPatch(typeof(Rewired.Player), "GetAnyButtonUp"), HarmonyPrefix]
        private static bool GetAnyButtonUp(Rewired.Player __instance, ref bool __result)
        {
            if (RemoteInput.Sampling) return true;
            VirtualPad pad = RemoteInput.PadFor(__instance); if (pad != null) { __result = pad.AnyButtonUp(); return false; }
            if (Block(__instance)) { __result = false; return false; }
            return true;
        }

        /// <summary>The title screen only accepts P1's keyboard (PlayerManager.Update: controllers.Keyboard.GetAnyButtonDown on scene_title).</summary>
        [HarmonyPatch(typeof(Rewired.Controller), "GetAnyButtonDown"), HarmonyPostfix]
        private static void ControllerAnyButtonDown(Rewired.Controller __instance, ref bool __result)
        {
            if (!__result && TestDriver.KeyboardPulse > 0 && __instance is Rewired.Keyboard) __result = true;
        }

        /// <summary>A network player has no physical controller to lose; never show the reconnect prompt for P2.</summary>
        [HarmonyPatch(typeof(PlayerManager), "IsControllerDisconnected"), HarmonyPrefix]
        private static bool IsControllerDisconnected(PlayerId playerId, ref bool __result)
        {
            if (RemoteInput.DriveHostPlayerTwo && playerId == PlayerId.PlayerTwo) { __result = false; return false; }
            return true;
        }
    }
}
