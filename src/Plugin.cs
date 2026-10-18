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
        public const string Version = "0.1.2";
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
        public static ConfigEntry<string> LastPartnerId;
        public static ConfigEntry<string> LastPartnerName;
        public static ConfigEntry<bool> BackupSaves;
        public static ConfigEntry<int> BackupsToKeep;
        public static ConfigEntry<bool> CheckForUpdates;

        public const string RepoUrl = "https://github.com/nikrabaev/cuphead-online-coop";

        /// <summary>Patch classes that failed to apply (the game changed under the mod); shown in the menu.</summary>
        public static readonly System.Collections.Generic.List<string> HookFailures = new System.Collections.Generic.List<string>();

        /// <summary>Scripted or autostarted runs (two instances on one machine) must keep running while unfocused.</summary>
        public static bool AlwaysRunInBackground { get; private set; }

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
            LastPartnerId = Config.Bind("Network", "LastPartnerSteamId", "", "SteamID64 of the last person you played with over Steam (for 'Play with ...').");
            LastPartnerName = Config.Bind("Network", "LastPartnerName", "", "Their Steam name when you last played.");
            BackupSaves = Config.Bind("Saves", "BackupSaves", true, "Copy your Cuphead saves to BepInEx/CupheadOnline/SaveBackups when an online session starts.");
            BackupsToKeep = Config.Bind("Saves", "BackupsToKeep", 10, "How many save backups to keep (oldest are deleted).");
            CheckForUpdates = Config.Bind("Updates", "CheckForUpdates", true, "Once per launch, ask GitHub whether a newer release of the mod is out.");

            if (DebugDump.Enabled)
            {
                // Two instances on one machine share LogOutput.log; give each its own file too.
                BepInEx.Logging.Logger.Listeners.Add(new DiskLogListener("CupheadOnline-" + System.Diagnostics.Process.GetCurrentProcess().Id + ".log", LogLevel.All));
            }
            harmony = new Harmony(Guid);
            PatchAll();
            AudioSync.Install(harmony);
            HostSync.Install();
            ClientMirror.Install();
            SteamSupport.Install();
            AlwaysRunInBackground = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CUPHEAD_ONLINE")) ||
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CUPHEAD_ONLINE_SCRIPT"));
            if (AlwaysRunInBackground) Application.runInBackground = true;
            StartCoroutine(EndOfFrame());
            StartCoroutine(AutoStart());
            StartCoroutine(UpdateCheck.Run());
            TestDriver.Start(this);
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) => { if (scene.name != HostSync.LoadHelperScene) dumpAt = Time.realtimeSinceStartup + 4f; };
            Log.LogInfo(DisplayName + " " + Version + " loaded. Press " + MenuKey.Value + " for the online menu." +
                (HookFailures.Count > 0 ? " " + HookFailures.Count + " game hook(s) failed: " + string.Join(", ", HookFailures.ToArray()) : ""));
        }

        /// <summary>
        /// Harmony's PatchAll stops at the first patch class whose target is missing, leaving every later one
        /// unapplied. Patch class by class instead, so one hook broken by a game update disables only its feature,
        /// and the menu can say so. (scripts/check-hooks.sh catches the same problem at build time.)
        /// </summary>
        private void PatchAll()
        {
            foreach (Type type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception ex)
                {
                    HookFailures.Add(type.Name);
                    Log.LogError("Could not hook the game (" + type.FullName + "): " + (ex.InnerException ?? ex).Message);
                }
            }
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
            string[] args = Environment.GetCommandLineArgs();
            foreach (string arg in args)
            {
                if (arg.StartsWith("--online=", StringComparison.Ordinal)) spec = arg.Substring(9);
            }
            // Steam starts the game with "+connect_lobby ID" when an invite (or a friend's "Join Game") is accepted
            // while Cuphead is closed.
            ulong launchLobby = string.IsNullOrEmpty(spec) ? SteamSupport.ParseConnectLobby(args) : 0;
            if (launchLobby != 0) spec = "steamlobby:" + launchLobby;
            if (string.IsNullOrEmpty(spec)) yield break;
            float deadline = Time.realtimeSinceStartup + 30f;
            while (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "scene_title" && Time.realtimeSinceStartup < deadline) yield return null;
            yield return new WaitForSeconds(2f);
            while (launchLobby != 0 && !SteamSupport.Available && Time.realtimeSinceStartup < deadline + 15f) yield return null;
            Log.LogInfo("Autostart: " + spec);
            try
            {
                string[] parts = spec.Split(':');
                switch (parts[0])
                {
                    case "host":
                        int hostPort = parts.Length > 1 ? int.Parse(parts[1]) : Port.Value;
                        Session.StartHost(() => UdpTransport.Host(hostPort), PlayerName.Value);
                        break;
                    case "steamhost":
                        SteamSupport.HostViaSteam();
                        break;
                    case "join":
                        string joinHost = parts[1];
                        int joinPort = parts.Length > 2 ? int.Parse(parts[2]) : Port.Value;
                        Session.StartClient(() => UdpTransport.Connect(joinHost, joinPort), PlayerName.Value);
                        break;
                    case "steamjoin":
                        SteamSupport.JoinViaSteam(ulong.Parse(parts[1]));
                        break;
                    case "steamlobby":
                        SteamSupport.JoinLobby(ulong.Parse(parts[1]));
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

        private void LateUpdate()
        {
            Overlay.UpdateCursor();
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
