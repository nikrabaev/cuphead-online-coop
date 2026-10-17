using System;
using System.Collections;
using System.IO;
using CupheadOnline.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Scripted end-to-end testing without touching the real keyboard. Set CUPHEAD_ONLINE_SCRIPT to a file of
    /// commands (one per line, '#' comments). On the host the script plays player 1 (Rewired player 0); on the
    /// client it replaces local sampling, so its presses travel the real network path and become player 2.
    ///
    ///   wait SEC                     sleep (realtime)
    ///   waitscene NAME [TIMEOUT]     host: active scene loaded; client: mirror of that scene loaded
    ///   waitp2 [TIMEOUT]             host only: player 2 has joined
    ///   anykey [TIMEOUT]             title screen "press any button" (P1 keyboard), retried until it loads
    ///   tap BUTTON [COUNT]           press and release a CupheadButton (Jump, Shoot, Accept, MenuDown, ...)
    ///   tapuntil BUTTON SCENES [T]   keep tapping until a matching scene is loaded
    ///   hold BUTTON SEC              hold a button
    ///   move X Y SEC                 hold the movement stick
    ///   level NAME [MODE]            host: load a boss level directly (Levels enum, e.g. Veggies; mode Easy/Normal/Hard)
    ///   hudweapon NAME               host: show the HUD weapon-switch icon (slides inside a UGUI Mask) for Weapon NAME
    ///   secondary NAME               host: give both players Weapon NAME as secondary weapon (in memory, not saved)
    ///   invincible                   host: both players stop taking damage (the game's debug toggle; reset per level)
    ///   shot NAME                    save a screenshot to CUPHEAD_ONLINE_SHOTS/ROLE_NAME.png
    ///   state                        log scene / player / mirror state
    ///   dump                         log every camera, renderer, canvas and UI graphic
    ///   fxdump                       log particle systems, sprite/UI masks and per-renderer material state
    ///   quit                         exit the game
    /// </summary>
    public static class TestDriver
    {
        public static VirtualPad HostPad;
        public static int KeyboardPulse;

        private static PadState pad;
        private static string role = "offline";

        public static void Start(MonoBehaviour runner)
        {
            string path = Environment.GetEnvironmentVariable("CUPHEAD_ONLINE_SCRIPT");
            if (string.IsNullOrEmpty(path)) return;
            if (!File.Exists(path))
            {
                Plugin.Log.LogError("[test] script not found: " + path);
                return;
            }
            runner.StartCoroutine(Run(File.ReadAllLines(path)));
        }

        public static void Update()
        {
            if (KeyboardPulse > 0) KeyboardPulse--;
        }

        private static void Log(string text)
        {
            Plugin.Log.LogInfo("[test:" + role + "] " + text);
        }

        private static IEnumerator Run(string[] lines)
        {
            float deadline = Time.realtimeSinceStartup + 60f;
            while (Session.Role == Role.None && Time.realtimeSinceStartup < deadline) yield return null;
            if (Session.Role == Role.Host)
            {
                role = "host";
                HostPad = new VirtualPad();
                pad = HostPad.Latest;
            }
            else
            {
                role = Session.Role == Role.Client ? "client" : "offline";
                RemoteInput.Scripted = new PadState();
                pad = RemoteInput.Scripted;
            }
            Log("script started (" + lines.Length + " lines)");
            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                string[] a = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                Log("> " + line);
                switch (a[0])
                {
                    case "wait":
                        yield return new WaitForSecondsRealtime(F(a, 1, 1f));
                        break;
                    case "waitscene":
                    {
                        float until = Time.realtimeSinceStartup + F(a, 2, 60f);
                        while (!InScene(a[1]) && Time.realtimeSinceStartup < until) yield return null;
                        Log(InScene(a[1]) ? "PASS scene " + a[1] : "FAIL timed out waiting for scene " + a[1] + " (now " + SceneManager.GetActiveScene().name + ")");
                        break;
                    }
                    case "waitp2":
                    {
                        float until = Time.realtimeSinceStartup + F(a, 1, 20f);
                        while (!P2Joined() && Time.realtimeSinceStartup < until) yield return null;
                        Log(P2Joined() ? "PASS player 2 joined" : "FAIL player 2 did not join");
                        break;
                    }
                    case "anykey":
                    {
                        // The title only accepts P1 after its intro animation, so keep pulsing until a load starts.
                        string from = SceneManager.GetActiveScene().name;
                        float until = Time.realtimeSinceStartup + F(a, 1, 30f);
                        while (!SceneLoader.CurrentlyLoading && SceneManager.GetActiveScene().name == from && Time.realtimeSinceStartup < until)
                        {
                            KeyboardPulse = 2;
                            yield return new WaitForSecondsRealtime(0.5f);
                        }
                        Log(SceneLoader.CurrentlyLoading || SceneManager.GetActiveScene().name != from ? "PASS anykey accepted" : "FAIL anykey ignored");
                        break;
                    }
                    case "tap":
                    {
                        int id = Button(a[1]);
                        int count = (int)F(a, 2, 1f);
                        for (int i = 0; i < count && id >= 0; i++)
                        {
                            pad.Presses[id]++;
                            yield return new WaitForSecondsRealtime(0.25f);
                        }
                        break;
                    }
                    case "tapuntil":
                    {
                        // tapuntil BUTTON SCENEPATTERN [TIMEOUT]: e.g. page through a cutscene until the level loads
                        int id = Button(a[1]);
                        float until = Time.realtimeSinceStartup + F(a, 3, 90f);
                        while (id >= 0 && !InScene(a[2]) && Time.realtimeSinceStartup < until)
                        {
                            pad.Presses[id]++;
                            yield return new WaitForSecondsRealtime(1.2f);
                        }
                        Log(InScene(a[2]) ? "PASS reached " + a[2] : "FAIL never reached " + a[2]);
                        break;
                    }
                    case "hold":
                    {
                        int id = Button(a[1]);
                        if (id < 0) break;
                        pad.Held |= 1u << id;
                        pad.Presses[id]++;
                        yield return new WaitForSecondsRealtime(F(a, 2, 0.5f));
                        pad.Held &= ~(1u << id);
                        break;
                    }
                    case "move":
                        pad.Axes[0] = F(a, 1, 0f);
                        pad.Axes[1] = F(a, 2, 0f);
                        yield return new WaitForSecondsRealtime(F(a, 3, 0.5f));
                        pad.Axes[0] = 0f;
                        pad.Axes[1] = 0f;
                        break;
                    case "level":
                    {
                        try
                        {
                            if (a.Length > 2) Level.SetCurrentMode((Level.Mode)Enum.Parse(typeof(Level.Mode), a[2], true));
                            SceneLoader.LoadLevel((Levels)Enum.Parse(typeof(Levels), a[1], true), SceneLoader.Transition.Fade);
                        }
                        catch (Exception ex) { Log("FAIL level " + a[1] + ": " + ex.Message); }
                        break;
                    }
                    case "hudweapon":
                        try
                        {
                            Weapon weapon = (Weapon)Enum.Parse(typeof(Weapon), a[1], true);
                            foreach (LevelHUDPlayer hud in UnityEngine.Object.FindObjectsOfType<LevelHUDPlayer>())
                                HarmonyLib.AccessTools.Method(typeof(LevelHUDPlayer), "OnWeaponChanged").Invoke(hud, new object[] { weapon });
                        }
                        catch (Exception ex) { Log("FAIL hudweapon " + a[1] + ": " + ex.Message); }
                        break;
                    case "secondary":
                        try
                        {
                            Weapon weapon = (Weapon)Enum.Parse(typeof(Weapon), a[1], true);
                            PlayerData.Data.Loadouts.GetPlayerLoadout(PlayerId.PlayerOne).secondaryWeapon = weapon;
                            PlayerData.Data.Loadouts.GetPlayerLoadout(PlayerId.PlayerTwo).secondaryWeapon = weapon;
                        }
                        catch (Exception ex) { Log("FAIL secondary " + a[1] + ": " + ex.Message); }
                        break;
                    case "invincible":
                        if (!PlayerStatsManager.DebugInvincible) PlayerStatsManager.DebugToggleInvincible();
                        break;
                    case "shot":
                        Shot(a.Length > 1 ? a[1] : "shot");
                        yield return null;
                        break;
                    case "state":
                        Log(State());
                        break;
                    case "dump":
                        DebugDump.Dump("script");
                        break;
                    case "fxdump":
                        DebugDump.DumpEffects("script");
                        break;
                    case "quit":
                        Log("quit");
                        Application.Quit();
                        yield break;
                    default:
                        Log("unknown command " + a[0]);
                        break;
                }
            }
            Log("script finished");
        }

        private static float F(string[] a, int i, float fallback)
        {
            float v;
            return a.Length > i && float.TryParse(a[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private static int Button(string name)
        {
            try { return (int)(CupheadButton)Enum.Parse(typeof(CupheadButton), name, true); }
            catch
            {
                Log("unknown button " + name);
                return -1;
            }
        }

        /// <summary>Pattern: names separated by '|', each may end with '*' for a prefix match.</summary>
        private static bool InScene(string pattern)
        {
            string current;
            bool loading;
            if (Session.Role == Role.Client) { current = ClientMirror.LoadedScene; loading = ClientMirror.Loading; }
            else { current = SceneManager.GetActiveScene().name; loading = SceneLoader.CurrentlyLoading; }
            if (loading || current == null) return false;
            foreach (string option in pattern.Split('|'))
            {
                if (option.EndsWith("*") ? current.StartsWith(option.Substring(0, option.Length - 1), StringComparison.Ordinal) : current == option) return true;
            }
            return false;
        }

        private static bool P2Joined()
        {
            try { return PlayerManager.Multiplayer && PlayerManager.DoesPlayerExist(PlayerId.PlayerTwo); }
            catch { return false; }
        }

        private static void Shot(string name)
        {
            string dir = Environment.GetEnvironmentVariable("CUPHEAD_ONLINE_SHOTS");
            if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
            string file = Path.Combine(dir, role + "_" + name + ".png");
            ScreenCapture.CaptureScreenshot(file);
            Log("screenshot " + file);
        }

        private static string State()
        {
            string s = "scene=" + SceneManager.GetActiveScene().name + " loading=" + SceneLoader.CurrentlyLoading + " paused=" + PauseManager.state;
            if (Session.Role == Role.Client) return s + " " + ClientMirror.Summary();
            s += " multiplayer=" + PlayerManager.Multiplayer;
            for (int i = 0; i < 2; i++)
            {
                PlayerId id = (PlayerId)i;
                try
                {
                    if (!PlayerManager.DoesPlayerExist(id)) { s += " p" + (i + 1) + "=none"; continue; }
                    AbstractPlayerController p = PlayerManager.GetPlayer(id);
                    Vector3 pos = p.transform.position;
                    s += string.Format(" p{0}=({1:0},{2:0}) hp={3}", i + 1, pos.x, pos.y, p.stats != null ? p.stats.Health : -1);
                    LevelPlayerWeaponManager weapons = p.GetComponent<LevelPlayerWeaponManager>();
                    if (weapons != null) s += " weapon=" + HarmonyLib.Traverse.Create(weapons).Field("currentWeapon").GetValue();
                }
                catch (Exception ex) { s += " p" + (i + 1) + "=? (" + ex.GetType().Name + ")"; }
            }
            return s;
        }
    }
}
