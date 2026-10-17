using System;
using System.Collections.Generic;
using System.Reflection;
using CupheadOnline.Net;
using HarmonyLib;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Cuphead routes (almost) all sound through static AudioManager methods keyed by strings, which raise static
    /// events that every AudioManagerComponent listens to (observed via REA + decompilation). The host records those
    /// calls; the client replays the same call so its own AudioManagerComponents (loaded with the same scene) play it.
    /// PauseManager.Pause/Unpause are replicated the same way (they toggle AudioListener.pause).
    /// </summary>
    public static class AudioSync
    {
        private static readonly List<MethodInfo> methods = new List<MethodInfo>();
        private static readonly Dictionary<MethodBase, int> indexOf = new Dictionary<MethodBase, int>();

        [ThreadStatic] private static int depth;
        /// <summary>Client: true while replaying a host call (lets the call through the block).</summary>
        [ThreadStatic] public static bool Replaying;

        private struct Queued { public ushort Serial; public byte[] Payload; }
        private static readonly List<Queued> waiting = new List<Queued>();

        private static readonly string[] ReplicatedNames =
        {
            "Play", "PlayLoop", "Stop", "Pause", "Unpaused", "Pan", "Attenuation", "ChangeSFXPitch",
            "FadeSFXVolume", "FadeSFXVolumeLinear", "StopAll", "PauseAllSFX", "UnpauseAllSFX",
            "PlayBGM", "StopBGM", "PlaylistBGM", "PlayBGMPlaylistManually", "StopBGMPlaylistManually",
            "StartBGMAlternate", "FadeBGMVolume", "ChangeBGMPitch", "WarbleBGMPitch",
            "SnapshotTransition", "HandleSnapshot", "SnapshotReset",
        };

        private static bool Supported(Type t)
        {
            return t == typeof(string) || t == typeof(float) || t == typeof(int) || t == typeof(bool)
                || t == typeof(string[]) || t == typeof(float[]);
        }

        public static void Install(Harmony harmony)
        {
            List<MethodInfo> found = new List<MethodInfo>();
            foreach (MethodInfo m in typeof(AudioManager).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (Array.IndexOf(ReplicatedNames, m.Name) < 0) continue;
                bool ok = true;
                foreach (ParameterInfo p in m.GetParameters()) if (!Supported(p.ParameterType)) { ok = false; break; }
                if (ok) found.Add(m);
            }
            found.Add(AccessTools.Method(typeof(PauseManager), "Pause"));
            found.Add(AccessTools.Method(typeof(PauseManager), "Unpause"));
            // Deterministic order on both machines (same mod + same game build).
            found.Sort((a, b) => string.CompareOrdinal(Signature(a), Signature(b)));
            HarmonyMethod prefix = new HarmonyMethod(typeof(AudioSync), "Prefix");
            HarmonyMethod finalizer = new HarmonyMethod(typeof(AudioSync), "Finalizer");
            foreach (MethodInfo m in found)
            {
                if (m == null) continue;
                indexOf[m] = methods.Count;
                methods.Add(m);
                harmony.Patch(m, prefix: prefix, finalizer: finalizer);
            }
            Plugin.Log.LogInfo("Audio replication hooks: " + methods.Count);
        }

        private static string Signature(MethodInfo m)
        {
            string s = m.DeclaringType.Name + "." + m.Name + "(";
            foreach (ParameterInfo p in m.GetParameters()) s += p.ParameterType.Name + ",";
            return s + ")";
        }

        private static bool Prefix(MethodBase __originalMethod, object[] __args, out bool __state)
        {
            __state = false;
            if (ClientMirror.Mirroring && !Replaying)
            {
                return false; // the local (frozen) game must not make sounds of its own
            }
            __state = true;
            depth++;
            if (depth == 1 && Session.IsHost)
            {
                int index;
                if (indexOf.TryGetValue(__originalMethod, out index)) Record(index, __originalMethod, __args);
            }
            return true;
        }

        // A finalizer runs even when the original throws, so depth can never get stuck above zero.
        private static void Finalizer(bool __state)
        {
            if (__state && depth > 0) depth--;
        }

        private static readonly NetWriter batch = new NetWriter(512);
        private static int batchCount;
        private static ushort batchSerial;
        private static readonly NetWriter scratch = new NetWriter(256);

        private static void Record(int index, MethodBase method, object[] args)
        {
            NetWriter w = scratch;
            w.Reset();
            w.U8((byte)index);
            ParameterInfo[] ps = method.GetParameters();
            for (int i = 0; i < ps.Length; i++)
            {
                Type t = ps[i].ParameterType;
                object a = args[i];
                if (t == typeof(string)) w.Str((string)a);
                else if (t == typeof(float)) w.F32((float)a);
                else if (t == typeof(int)) w.I32((int)a);
                else if (t == typeof(bool)) w.Bool((bool)a);
                else if (t == typeof(string[]))
                {
                    string[] arr = (string[])a ?? new string[0];
                    w.VarU((uint)arr.Length);
                    foreach (string s in arr) w.Str(s);
                }
                else if (t == typeof(float[]))
                {
                    float[] arr = (float[])a ?? new float[0];
                    w.VarU((uint)arr.Length);
                    foreach (float f in arr) w.F32(f);
                }
            }
            if (w.Length > 900) return;
            ushort serial = HostSync.CurrentSerial;
            if (batchCount > 0 && (serial != batchSerial || batch.Length + w.Length > 900)) FlushBatch(Session.Connection);
            if (batchCount == 0)
            {
                batch.Reset();
                batchSerial = serial;
            }
            batch.Bytes(w.Buffer, 0, w.Length);
            batchCount++;
        }

        /// <summary>Host: all audio calls of a frame go out as one reliable message (serial, count, calls...).</summary>
        public static void FlushBatch(Connection connection)
        {
            if (batchCount == 0 || connection == null) { batchCount = 0; return; }
            NetWriter msg = new NetWriter(batch.Length + 8);
            msg.U16(batchSerial);
            msg.VarU((uint)batchCount);
            msg.Bytes(batch.Buffer, 0, batch.Length);
            batchCount = 0;
            connection.SendReliable(MsgType.Audio, msg);
        }

        // ---------------------------------------------------------------- client

        public static void OnAudioMessage(NetReader r, int length)
        {
            ushort serial = r.U16();
            byte[] payload = new byte[r.Remaining];
            for (int i = 0; i < payload.Length; i++) payload[i] = r.U8();
            if (serial == ClientMirror.LoadedSerial && !ClientMirror.Loading) Replay(payload);
            else if ((short)(serial - ClientMirror.LoadedSerial) > 0 || ClientMirror.Loading) waiting.Add(new Queued { Serial = serial, Payload = payload });
            // older scene: drop
        }

        /// <summary>Client finished loading the scene with this serial: play what the host did meanwhile.</summary>
        public static void FlushWaiting(ushort loadedSerial)
        {
            for (int i = 0; i < waiting.Count; i++)
            {
                if (waiting[i].Serial == loadedSerial) Replay(waiting[i].Payload);
            }
            waiting.RemoveAll(q => (short)(q.Serial - loadedSerial) <= 0);
        }

        public static void ClearWaiting() { waiting.Clear(); }

        private static void Replay(byte[] payload)
        {
            NetReader r = new NetReader(payload, 0, payload.Length);
            int count = (int)r.VarU();
            for (int n = 0; n < count && r.Remaining > 0; n++)
            {
                if (!ReplayOne(r)) return;
            }
        }

        private static bool ReplayOne(NetReader r)
        {
            int index = r.U8();
            if (index >= methods.Count) return false;
            MethodInfo m = methods[index];
            ParameterInfo[] ps = m.GetParameters();
            object[] args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                Type t = ps[i].ParameterType;
                if (t == typeof(string)) args[i] = r.Str();
                else if (t == typeof(float)) args[i] = r.F32();
                else if (t == typeof(int)) args[i] = r.I32();
                else if (t == typeof(bool)) args[i] = r.Bool();
                else if (t == typeof(string[]))
                {
                    string[] arr = new string[r.VarU()];
                    for (int k = 0; k < arr.Length; k++) arr[k] = r.Str();
                    args[i] = arr;
                }
                else if (t == typeof(float[]))
                {
                    float[] arr = new float[r.VarU()];
                    for (int k = 0; k < arr.Length; k++) arr[k] = r.F32();
                    args[i] = arr;
                }
            }
            Replaying = true;
            try { m.Invoke(null, args); }
            catch (Exception ex) { Plugin.Log.LogDebug("Audio replay " + m.Name + " failed: " + (ex.InnerException ?? ex).Message); }
            finally { Replaying = false; }
            return true;
        }
    }
}
