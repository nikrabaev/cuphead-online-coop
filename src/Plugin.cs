using System;
using System.Collections;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using CupheadOnline.Net;
using CupheadOnline.Sync;
using CupheadOnline.UI;
using HarmonyLib;
using UnityEngine;

namespace CupheadOnline
{
    [BepInPlugin(Guid, DisplayName, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "dev.cupheadonline.mod";
        public const string Version = "0.1.0";
        /// <summary>Unofficial mod: the name must not suggest it is made or endorsed by Studio MDHR.</summary>
        public const string DisplayName = "Online Co-op for Cuphead (unofficial)";

        public static ManualLogSource Log;
        public static Plugin Instance;

        public static ConfigEntry<string> PlayerName;
        public static ConfigEntry<int> Port;
        public static ConfigEntry<string> LastAddress;
        public static ConfigEntry<string> LastSteamId;
        public static ConfigEntry<KeyCode> MenuKey;
        public static ConfigEntry<int> SendInterval;

        private Harmony harmony;
        private float dumpAt;
        private float nextShot;
        private int shotIndex;
        private readonly string shotDir = Environment.GetEnvironmentVariable("CUPHEAD_ONLINE_SHOTS");

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            PlayerName = Config.Bind("General", "PlayerName", Environment.UserName, "Name shown to the other player.");
            Port = Config.Bind("Network", "Port", Protocol.DefaultPort, "UDP port used when hosting by IP.");
            LastAddress = Config.Bind("Network", "LastAddress", "127.0.0.1:" + Protocol.DefaultPort, "Last host address joined by IP.");
            LastSteamId = Config.Bind("Network", "LastSteamId", "", "Last host SteamID64 joined via Steam.");
            MenuKey = Config.Bind("General", "MenuKey", KeyCode.F8, "Key that opens the online menu.");
            SendInterval = Config.Bind("Network", "SendInterval", 1, "Host: send world state every N frames (1 = 60 Hz, 2 = 30 Hz for slow connections).");

            if (DebugDump.Enabled)
            {
                // Two instances on one machine share LogOutput.log; give each its own file too.
                BepInEx.Logging.Logger.Listeners.Add(new DiskLogListener("CupheadOnline-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".log", LogLevel.All));
            }
            harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            AudioSync.Install(harmony);
            HostSync.Install();
            ClientMirror.Install();
            SteamSupport.Install();
            Application.runInBackground = true;
            StartCoroutine(EndOfFrame());
            StartCoroutine(AutoStart());
            TestDriver.Start(this);
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) => { if (scene.name != HostSync.LoadHelperScene) dumpAt = Time.realtimeSinceStartup + 4f; };
            Log.LogInfo(DisplayName + " " + Version + " loaded. Press " + MenuKey.Value + " for the online menu.");
        }

        private void Update()
        {
            HostSync.SendInterval = Mathf.Max(1, SendInterval.Value);
            TestDriver.Update();
            ClientMirror.Update();
            if (Input.GetKeyDown(MenuKey.Value)) Overlay.Visible = !Overlay.Visible;
            if (DebugDump.Enabled && Input.GetKeyDown(KeyCode.F9)) DebugDump.Dump("F9");
            if (shotDir != null && Time.realtimeSinceStartup > nextShot)
            {
                nextShot = Time.realtimeSinceStartup + 5f;
                string role = Session.Role == Role.None ? "offline" : Session.Role.ToString().ToLowerInvariant();
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(shotDir, role + "_" + (shotIndex++ % 6) + ".png"));
            }
            if (DebugDump.Enabled && dumpAt > 0 && Time.realtimeSinceStartup > dumpAt) { dumpAt = 0; DebugDump.Dump("auto"); }
            try
            {
                Session.PreUpdate();
            }
            catch (Exception ex)
            {
                Log.LogError("Network receive failed: " + ex);
            }
        }

        /// <summary>Capture after the frame is rendered, so transforms (camera LateUpdates) and isVisible are final.</summary>
        private IEnumerator EndOfFrame()
        {
            WaitForEndOfFrame wait = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait;
                try
                {
                    Session.PostUpdate();
                }
                catch (Exception ex)
                {
                    Log.LogError("Network send failed: " + ex);
                }
            }
        }

        /// <summary>
        /// Optional autostart, from the CUPHEAD_ONLINE environment variable or a --online=... argument:
        ///   host | host:PORT | steamhost | join:HOST:PORT | steamjoin:STEAMID64
        /// </summary>
        private IEnumerator AutoStart()
        {
            string spec = Environment.GetEnvironmentVariable("CUPHEAD_ONLINE");
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (arg.StartsWith("--online=", StringComparison.Ordinal)) spec = arg.Substring(9);
            }
            if (string.IsNullOrEmpty(spec)) yield break;
            float deadline = Time.realtimeSinceStartup + 30f;
            while (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "scene_title" && Time.realtimeSinceStartup < deadline) yield return null;
            yield return new WaitForSeconds(2f);
            Log.LogInfo("Autostart: " + spec);
            try
            {
                string[] parts = spec.Split(':');
                switch (parts[0])
                {
                    case "host":
                        Session.StartHost(UdpTransport.Host(parts.Length > 1 ? int.Parse(parts[1]) : Port.Value), PlayerName.Value);
                        break;
                    case "steamhost":
                        SteamSupport.HostViaSteam();
                        break;
                    case "join":
                        Session.StartClient(UdpTransport.Connect(parts[1], parts.Length > 2 ? int.Parse(parts[2]) : Port.Value), PlayerName.Value);
                        break;
                    case "steamjoin":
                        SteamSupport.JoinViaSteam(ulong.Parse(parts[1]));
                        break;
                    default:
                        Log.LogWarning("Unknown autostart spec " + spec);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.LogError("Autostart failed: " + ex.Message);
            }
        }

        private void OnGUI()
        {
            Overlay.Draw();
        }

        private void OnApplicationQuit()
        {
            Session.Stop("quit");
        }
    }
}
