using System;
using System.Collections;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace CupheadOnline.Net
{
    /// <summary>
    /// Once per launch, asks GitHub for this mod's latest release ([Updates] CheckForUpdates). One anonymous HTTPS
    /// GET to api.github.com; nothing about the player is sent. Uses UnityWebRequest because the game's old Mono
    /// runtime can't do modern TLS through System.Net.
    /// </summary>
    public static class UpdateCheck
    {
        private const string LatestReleaseApi = "https://api.github.com/repos/nikrabaev/cuphead-online-coop/releases/latest";

        /// <summary>Set when a newer release than this build exists, e.g. "0.1.3".</summary>
        public static string NewerVersion { get; private set; }
        public static string ReleaseUrl { get; private set; }

        public static IEnumerator Run()
        {
            if (!Plugin.CheckForUpdates.Value) yield break;
            yield return new WaitForSecondsRealtime(5f); // stay out of the way of startup loading
            UnityWebRequest request = UnityWebRequest.Get(LatestReleaseApi);
            request.timeout = 15;
            request.SetRequestHeader("Accept", "application/vnd.github+json");
            try { request.SetRequestHeader("User-Agent", "CupheadOnline/" + Plugin.Version); }
            catch (Exception) { } // some platforms refuse to override the user agent
            yield return request.SendWebRequest();
            try
            {
                if (request.isNetworkError || request.isHttpError)
                {
                    Plugin.Log.LogInfo("Update check skipped: " + (request.isHttpError ? "HTTP " + request.responseCode : request.error));
                    yield break;
                }
                string json = request.downloadHandler.text;
                Match tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9][0-9.]*)\"");
                Match url = Regex.Match(json, "\"html_url\"\\s*:\\s*\"(https://github\\.com/[^\"]+/releases/[^\"]+)\"");
                if (!tag.Success) yield break;
                if (new Version(tag.Groups[1].Value) > new Version(Plugin.Version))
                {
                    NewerVersion = tag.Groups[1].Value;
                    ReleaseUrl = url.Success ? url.Groups[1].Value : Plugin.RepoUrl + "/releases/latest";
                    Plugin.Log.LogInfo("A newer version of the mod is out: v" + NewerVersion + " (" + ReleaseUrl + ")");
                }
                else
                {
                    Plugin.Log.LogInfo("Mod is up to date (latest release v" + tag.Groups[1].Value + ")");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogInfo("Update check failed: " + ex.Message);
            }
            finally
            {
                request.Dispose();
            }
        }
    }
}
