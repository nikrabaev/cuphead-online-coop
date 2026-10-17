using System;
using System.Collections.Generic;
using System.Reflection;
using CupheadOnline.Net;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Client side of the mirror. The client loads the host's scene through Cuphead's own SceneLoader (which also
    /// loads the sprite atlas / music asset bundles), freezes every gameplay script in it, and draws what the host
    /// sends: SpriteRenderers, SpriteMasks, cameras, UGUI graphics (with mask clipping) and runtime material
    /// parameters. Its own controller input goes to the host as player 2.
    /// </summary>
    public static class ClientMirror
    {
        public static bool Mirroring { get; private set; }
        public static ushort LoadedSerial { get; private set; }
        public static bool Loading { get; private set; }
        public static string LoadedScene { get; private set; }
        /// <summary>Lets our own SceneLoader calls through the block on locally-initiated scene loads.</summary>
        public static bool AllowSceneLoad;

        private static string targetScene;
        private static ushort targetSerial;
        private static int targetLevel, targetMode;
        private static bool loadPending;

        private static GameObject root;
        private static Transform spriteRoot;
        private static Transform cameraRoot;
        private static GameObject uiRoot;

        public static readonly ClientAssetTable<Sprite> Sprites = new ClientAssetTable<Sprite>(AssetKeys.Sprite, AtlasSprite) { Usable = s => s.texture != null };

        private static readonly Dictionary<string, float> atlasRequests = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> atlasNextCheck = new Dictionary<string, float>();
        private static readonly Dictionary<string, UnityEngine.U2D.SpriteAtlas> atlases = new Dictionary<string, UnityEngine.U2D.SpriteAtlas>();

        /// <summary>
        /// The host's sprite lives in a late-bound SpriteAtlas the frozen client never asked for (Cuphead loads atlases
        /// from asset bundles on demand). Ask the game's own loader for it, then take the sprite from the atlas.
        /// </summary>
        private static Sprite AtlasSprite(string key)
        {
            int hash = key.IndexOf('#');
            if (hash < 0) return null;
            string tag = key.Substring(hash + 1);
            string name = key.Substring(0, key.IndexOf('|'));
            UnityEngine.U2D.SpriteAtlas atlas;
            if (!atlases.TryGetValue(tag, out atlas) || atlas == null)
            {
                float next;
                if (atlasNextCheck.TryGetValue(tag, out next) && Time.unscaledTime < next) return null;
                atlasNextCheck[tag] = Time.unscaledTime + 0.3f;
                atlas = null;
                try { atlas = AssetLoader<UnityEngine.U2D.SpriteAtlas>.GetCachedAsset(tag); }
                catch (Exception) { } // not cached yet
                if (atlas == null)
                {
                    float last;
                    if (!atlasRequests.TryGetValue(tag, out last) || Time.unscaledTime - last > 5f)
                    {
                        atlasRequests[tag] = Time.unscaledTime;
                        try
                        {
                            AssetLoader<UnityEngine.U2D.SpriteAtlas>.LoadAsset(tag, AssetLoaderOption.None());
                            Plugin.Log.LogInfo("Requested sprite atlas " + tag);
                        }
                        catch (Exception ex) { Plugin.Log.LogWarning("Atlas " + tag + " load failed: " + ex.Message); }
                    }
                    return null;
                }
                atlases[tag] = atlas;
            }
            return atlas.GetSprite(name);
        }
        public static readonly ClientAssetTable<Material> Materials = new ClientAssetTable<Material>(AssetKeys.Material, FallbackMaterial);
        public static readonly ClientAssetTable<Font> Fonts = new ClientAssetTable<Font>(f => AssetKeys.Font(f));
        public static readonly ClientAssetTable<TMPro.TMP_FontAsset> TmpFonts = new ClientAssetTable<TMPro.TMP_FontAsset>(f => AssetKeys.Font(f));

        private sealed class MirrorSprite
        {
            public GameObject Go;
            public SpriteRenderer Sr;   // KindSprite
            public SpriteMask Mask;     // KindMask
            public byte BuiltKind = 255;
            public SpriteState St;
            public uint LastFrame;
            public uint DespawnFrame;
            public bool Shown;
        }

        private sealed class MirrorGraphic
        {
            public GameObject Go;
            public RectTransform Rt;
            public Graphic G;
            public UiState St;
            public byte BuiltKind = 255;
            public uint LastFrame;
            public uint DespawnFrame;
            public bool Shown;
            public bool ClipApplied;
        }

        private sealed class MirrorCamera
        {
            public Camera Cam;
            public bool CupheadLetterbox;
            public uint LastFrame;
        }

        private static readonly Dictionary<ushort, MirrorSprite> sprites = new Dictionary<ushort, MirrorSprite>();
        private static readonly Dictionary<ushort, MirrorGraphic> graphics = new Dictionary<ushort, MirrorGraphic>();
        private static readonly Dictionary<byte, MirrorCamera> cameras = new Dictionary<byte, MirrorCamera>();
        private static readonly Dictionary<short, Canvas> canvases = new Dictionary<short, Canvas>();
        private static readonly HashSet<short> dirtyCanvases = new HashSet<short>();
        private static int spriteVersionSeen = -1, uiVersionSeen = -1;

        public static void Install()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        // ------------------------------------------------------------------ lifecycle

        public static void OnConnected()
        {
            Mirroring = true;
            Application.runInBackground = true;
            RemoteInput.BlockLocalGame = true;
            LoadedSerial = 0;
            targetSerial = 0;
            Sprites.ClearAll();
            Materials.ClearAll();
            Fonts.ClearAll();
            TmpFonts.ClearAll();
            MaterialSync.ClearClient();
            AudioSync.ClearWaiting();
            EnsureRoot();
        }

        public static void OnSessionEnded()
        {
            if (!Mirroring) return;
            Mirroring = false;
            RemoteInput.BlockLocalGame = false;
            ClearMirror();
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
            MaterialSync.ClearClient();
            AudioSync.ClearWaiting();
            loadPending = false;
            // The local scene is frozen: return to the title screen so the game is usable again.
            // SceneLoader ignores requests mid-load, so wait for any load in progress (see Update).
            returnToTitle = true;
            Update();
        }

        private static bool returnToTitle;

        /// <summary>Runs every frame, connected or not.</summary>
        public static void Update()
        {
            if (!returnToTitle || Mirroring || SceneLoader.CurrentlyLoading) return;
            returnToTitle = false;
            try
            {
                AudioManager.StopAll();
                PauseManager.Reset();
                AllowSceneLoad = true;
                SceneLoader.LoadScene(Scenes.scene_title, SceneLoader.Transition.Fade, SceneLoader.Transition.Fade, SceneLoader.Icon.None);
            }
            catch (Exception ex) { Plugin.Log.LogWarning("Return to title failed: " + ex.Message); }
            finally { AllowSceneLoad = false; }
        }

        private static void EnsureRoot()
        {
            if (root != null) return;
            root = new GameObject("CupheadOnline.Mirror");
            UnityEngine.Object.DontDestroyOnLoad(root);
            spriteRoot = new GameObject("Sprites").transform;
            spriteRoot.SetParent(root.transform, false);
            cameraRoot = new GameObject("Cameras").transform;
            cameraRoot.SetParent(root.transform, false);
            uiRoot = new GameObject("UI");
            uiRoot.transform.SetParent(root.transform, false);
        }

        private static void ClearMirror()
        {
            foreach (MirrorSprite s in sprites.Values) if (s.Go != null) UnityEngine.Object.Destroy(s.Go);
            foreach (MirrorGraphic g in graphics.Values) if (g.Go != null) UnityEngine.Object.Destroy(g.Go);
            foreach (MirrorCamera c in cameras.Values) if (c.Cam != null) UnityEngine.Object.Destroy(c.Cam.gameObject);
            foreach (Canvas c in canvases.Values) if (c != null) UnityEngine.Object.Destroy(c.gameObject);
            if (uiCamera != null) uiCamera.rect = new Rect(0f, 0f, 1f, 1f);
            sprites.Clear();
            graphics.Clear();
            cameras.Clear();
            canvases.Clear();
            dirtyCanvases.Clear();
        }

        // ------------------------------------------------------------------ per frame

        public static void Tick(Connection connection)
        {
            RemoteInput.SampleAndSend(connection);

            if (loadPending && !SceneLoader.CurrentlyLoading) StartLoad();

            if (Sprites.Version != spriteVersionSeen || Materials.Version != uiVersionSeen)
            {
                spriteVersionSeen = Sprites.Version;
                uiVersionSeen = Materials.Version;
                foreach (MirrorSprite s in sprites.Values)
                {
                    if (s.Go != null && s.St.Sprite != 0 && Sprites.NeedsRetry(CurrentSprite(s))) ApplySpriteAsset(s);
                }
                foreach (MirrorGraphic g in graphics.Values)
                {
                    Image img = g.G as Image;
                    if (img != null && img.sprite == null && g.St.Sprite != 0) img.sprite = Sprites.Get(g.St.Sprite);
                }
            }

            if (Time.unscaledTime - lastRetry > 0.25f)
            {
                lastRetry = Time.unscaledTime;
                foreach (MirrorSprite s in sprites.Values)
                {
                    if (s.Shown && s.Go != null && s.St.Sprite != 0 && Sprites.NeedsRetry(CurrentSprite(s))) ApplySpriteAsset(s);
                }
                foreach (MirrorGraphic g in graphics.Values)
                {
                    Image img = g.G as Image;
                    if (g.Shown && img != null && g.St.Sprite != 0 && Sprites.NeedsRetry(img.sprite)) img.sprite = Sprites.Get(g.St.Sprite);
                }
            }

            MaterialSync.Update();
            if (MaterialSync.Rebind.Count > 0) RebindMaterials();

            foreach (MirrorCamera c in cameras.Values)
            {
                if (c.Cam != null && c.Cam.enabled && c.CupheadLetterbox) c.Cam.rect = Letterbox.Viewport();
            }

            if (dirtyCanvases.Count > 0) SortCanvases();
            LayoutGraphics();
            if (Time.unscaledTime - lastDiag > 5f) Diagnostics();
        }

        private static float lastDiag;
        private static float lastRetry;

        /// <summary>Some materials switched to a clone carrying the host's runtime parameters; point their users at it.</summary>
        private static void RebindMaterials()
        {
            foreach (MirrorSprite s in sprites.Values)
            {
                if (s.Sr != null && s.St.Material != 0 && MaterialSync.Rebind.Contains(s.St.Material)) ApplyMaterial(s);
            }
            foreach (MirrorGraphic g in graphics.Values)
            {
                if (g.G != null && g.St.Material != 0 && MaterialSync.Rebind.Contains(g.St.Material)) ApplyGraphic(g, UiState.ColorBit, g.St.CanvasOrder, g.St.Depth);
            }
            MaterialSync.Rebind.Clear();
        }

        private static Sprite CurrentSprite(MirrorSprite s)
        {
            if (s.Sr != null) return s.Sr.sprite;
            if (s.Mask != null) return s.Mask.sprite;
            return null;
        }

        private static void Diagnostics()
        {
            lastDiag = Time.unscaledTime;
            if (DebugDump.Enabled)
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder("[mirror] entities:\n");
                foreach (KeyValuePair<ushort, MirrorSprite> kv in sprites)
                {
                    MirrorSprite s = kv.Value;
                    Sprite sp = CurrentSprite(s);
                    sb.AppendFormat("  #{0} shown={1} key={2} sprite={3} tex={4} pos=({5:0.0},{6:0.0},{7:0.0}) scale=({8:0.00},{9:0.00}) color={10} sort={11}/{12} layer={13} mat={14}\n",
                        kv.Key, s.Shown, Sprites.KeyOf(s.St.Sprite), sp != null ? sp.name : "null", sp != null && sp.texture != null ? sp.texture.name : "null",
                        s.St.X, s.St.Y, s.St.Z, s.St.SX, s.St.SY, s.St.Color, s.St.SortingLayer, s.St.SortingOrder, s.St.Layer, s.Sr != null && s.Sr.sharedMaterial != null ? s.Sr.sharedMaterial.name : "-");
                }
                foreach (KeyValuePair<byte, MirrorCamera> kv in cameras)
                {
                    Camera c = kv.Value.Cam;
                    if (c != null) sb.AppendFormat("  cam{0} en={1} pos={2} size={3} rect={4} mask={5:X} depth={6} clear={7}\n", kv.Key, c.enabled, c.transform.position, c.orthographicSize, c.rect, c.cullingMask, c.depth, c.clearFlags);
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            Plugin.Log.LogInfo("[mirror] " + Summary());
        }

        public static string Summary()
        {
            int shown = 0, missing = 0, masks = 0;
            string sample = null;
            foreach (MirrorSprite s in sprites.Values)
            {
                if (!s.Shown) continue;
                shown++;
                if (s.Mask != null) masks++;
                if (s.Go != null && s.St.Sprite != 0 && Sprites.NeedsRetry(CurrentSprite(s)))
                {
                    missing++;
                    if (sample == null) sample = Sprites.KeyOf(s.St.Sprite);
                }
            }
            int ui = 0, uiMissing = 0, clipped = 0;
            string uiSample = null;
            foreach (MirrorGraphic g in graphics.Values)
            {
                if (!g.Shown) continue;
                ui++;
                if (g.St.Clip) clipped++;
                Image img = g.G as Image;
                if (img != null && g.St.Sprite != 0 && Sprites.NeedsRetry(img.sprite))
                {
                    uiMissing++;
                    if (uiSample == null) uiSample = Sprites.KeyOf(g.St.Sprite);
                }
            }
            int cams = 0;
            foreach (MirrorCamera c in cameras.Values) if (c.Cam != null && c.Cam.enabled) cams++;
            return string.Format("scene={0} serial={1} loading={2} sprites={3} masks={8} unresolved={4}{5} ui={6} clipped={9} cameras={7} {10}",
                targetScene, LoadedSerial, Loading, shown, missing, sample != null ? " (e.g. " + sample + ")" : "", ui + (uiMissing > 0 ? " uiUnresolved=" + uiMissing + " (e.g. " + uiSample + ")" : ""), cams,
                masks, clipped, MaterialSync.ClientSummary());
        }

        public static void OnMessage(MsgType type, NetReader r)
        {
            switch (type)
            {
                case MsgType.Scene: OnScene(r); break;
                case MsgType.SpriteDef: Sprites.ReadDefs(r); break;
                case MsgType.MaterialDef: Materials.ReadDefs(r); break;
                case MsgType.FontDef:
                {
                    int start = r.Position;
                    int remaining = r.Remaining;
                    byte[] copy = new byte[remaining];
                    for (int i = 0; i < remaining; i++) copy[i] = r.U8();
                    Fonts.ReadDefs(new NetReader(copy, 0, copy.Length));
                    TmpFonts.ReadDefs(new NetReader(copy, 0, copy.Length));
                    break;
                }
                case MsgType.Snapshot: OnSnapshot(r); break;
                case MsgType.Despawn: OnDespawn(r); break;
                case MsgType.CameraDef: OnCameraDef(r); break;
                case MsgType.Camera: OnCamera(r); break;
                case MsgType.UiSnapshot: OnUiSnapshot(r); break;
                case MsgType.UiDespawn: OnUiDespawn(r); break;
                case MsgType.Audio: AudioSync.OnAudioMessage(r, r.Remaining); break;
                case MsgType.MaterialLayout: MaterialSync.OnLayout(r); break;
                case MsgType.MaterialProps: MaterialSync.OnProps(r); break;
            }
        }

        private static bool Live(ushort serial)
        {
            return !Loading && serial == LoadedSerial && LoadedSerial != 0;
        }

        // ------------------------------------------------------------------ scenes

        private static void OnScene(NetReader r)
        {
            targetSerial = r.U16();
            targetScene = r.Str();
            targetLevel = r.I32();
            targetMode = r.I32();
            r.Bool(); // host multiplayer flag (informational)
            ClearMirror();
            Loading = true;
            loadPending = true;
            if (!SceneLoader.CurrentlyLoading) StartLoad();
        }

        private static void StartLoad()
        {
            loadPending = false;
            Plugin.Log.LogInfo("Loading host scene " + targetScene);
            try
            {
                try { SceneLoader.SetCurrentLevel((Levels)targetLevel); } catch { }
                try { Level.SetCurrentMode((Level.Mode)targetMode); } catch { }
                PauseManager.Reset();
                AllowSceneLoad = true;
                SceneLoader.LoadScene(targetScene, SceneLoader.Transition.Fade, SceneLoader.Transition.Fade, SceneLoader.Icon.Hourglass);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Could not load " + targetScene + ": " + ex);
            }
            finally
            {
                AllowSceneLoad = false;
            }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!Mirroring || scene.name == HostSync.LoadHelperScene) return;
            // Every scene that loads while mirroring is frozen, even a stale one the host has already left.
            Freeze(scene);
            if (scene.name != targetScene) return;
            Sprites.Invalidate();
            atlasRequests.Clear();
            atlasNextCheck.Clear();
            atlases.Clear();
            Materials.Invalidate();
            MaterialSync.OnClientSceneChanged();
            Fonts.Invalidate();
            TmpFonts.Invalidate();
            LoadedSerial = targetSerial;
            LoadedScene = scene.name;
            Loading = false;
            AudioSync.FlushWaiting(LoadedSerial);
            if (Session.Connection != null)
            {
                NetWriter ready = new NetWriter(4);
                ready.U16(LoadedSerial);
                Session.Connection.SendReliable(MsgType.SceneReady, ready);
            }
            Plugin.Log.LogInfo("Mirroring host scene " + scene.name);
        }

        private static readonly HashSet<string> KeepEnabled = new HashSet<string>
        {
            "AudioManagerComponent", "LevelAudio", "DeferredAudioSource",
        };

        private static readonly Dictionary<Type, bool> imageEffectCache = new Dictionary<Type, bool>();

        private static bool IsImageEffect(Type t)
        {
            bool result;
            if (imageEffectCache.TryGetValue(t, out result)) return result;
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            result = t.GetMethod("OnRenderImage", all) != null;
            imageEffectCache[t] = result;
            return result;
        }

        /// <summary>
        /// Stop the local simulation: sceneLoaded runs after Awake/OnEnable but before Start, so disabling every
        /// gameplay MonoBehaviour here means Level.Start / Map.Start (players, HUD, bosses) never run locally.
        /// </summary>
        private static void Freeze(Scene scene)
        {
            int behaviours = 0;
            foreach (GameObject go in scene.GetRootGameObjects())
            {
                foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    Type t = mb.GetType();
                    if (KeepEnabled.Contains(t.Name)) continue;
                    if (IsImageEffect(t)) continue;
                    try { ((MonoBehaviour)mb).StopAllCoroutines(); } catch { }
                    mb.enabled = false;
                    behaviours++;
                }
                foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                foreach (Animator a in go.GetComponentsInChildren<Animator>(true)) a.enabled = false;
                foreach (Camera c in go.GetComponentsInChildren<Camera>(true)) c.enabled = false;
                foreach (Canvas c in go.GetComponentsInChildren<Canvas>(true)) c.enabled = false;
                foreach (Collider2D c in go.GetComponentsInChildren<Collider2D>(true)) c.enabled = false;
                foreach (Rigidbody2D rb in go.GetComponentsInChildren<Rigidbody2D>(true)) rb.simulated = false;
            }
            // Level.Start would have created the level's AudioManagerComponent prefab (all level + player SFX).
            try
            {
                if (Level.Current != null && Level.Current.gameObject.scene == scene) LevelAudio.Create();
            }
            catch (Exception ex) { Plugin.Log.LogWarning("LevelAudio.Create failed: " + ex.Message); }
            Plugin.Log.LogInfo("Froze " + behaviours + " behaviours in " + scene.name);
        }

        // ------------------------------------------------------------------ sprites

        private static readonly SpriteState scratch = new SpriteState();

        private static void OnSnapshot(NetReader r)
        {
            ushort serial = r.U16();
            uint frame = r.U32();
            bool live = Live(serial);
            while (r.Remaining > 0)
            {
                ushort id = (ushort)r.VarU();
                uint mask = r.VarU();
                MirrorSprite s = null;
                if (live && !sprites.TryGetValue(id, out s))
                {
                    s = new MirrorSprite();
                    sprites[id] = s;
                }
                if (s == null || frame < s.LastFrame || frame <= s.DespawnFrame)
                {
                    SpriteState junk = scratch;
                    junk.Read(r, mask);
                    continue;
                }
                s.St.Read(r, mask);
                s.LastFrame = frame;
                Apply(s, mask);
            }
        }

        private static void Apply(MirrorSprite s, uint mask)
        {
            if (s.Go == null || s.BuiltKind != s.St.Kind)
            {
                if (s.Go != null) UnityEngine.Object.Destroy(s.Go);
                s.Go = new GameObject(s.St.Kind == SpriteState.KindMask ? "mask" : "m");
                s.Go.transform.SetParent(spriteRoot, false);
                s.Sr = null;
                s.Mask = null;
                if (s.St.Kind == SpriteState.KindMask) s.Mask = s.Go.AddComponent<SpriteMask>();
                else s.Sr = s.Go.AddComponent<SpriteRenderer>();
                s.BuiltKind = s.St.Kind;
                s.Shown = true;
                mask = SpriteState.All;
            }
            if (!s.Shown) { s.Go.SetActive(true); s.Shown = true; }
            Transform t = s.Go.transform;
            SpriteState st = s.St;
            if ((mask & (SpriteState.PosXY | SpriteState.PosZ)) != 0) t.position = new Vector3(st.X, st.Y, st.Z);
            if ((mask & SpriteState.Rot) != 0) t.eulerAngles = st.Euler;
            if ((mask & SpriteState.Scale) != 0) t.localScale = new Vector3(st.SX, st.SY, 1f);
            if ((mask & SpriteState.SpriteId) != 0) ApplySpriteAsset(s);
            if (s.Mask != null)
            {
                ApplyMask(s, mask);
                return;
            }
            if ((mask & SpriteState.ColorBit) != 0) s.Sr.color = st.Color;
            if ((mask & SpriteState.Flags) != 0)
            {
                s.Sr.flipX = (st.Bits & 1) != 0;
                s.Sr.flipY = (st.Bits & 2) != 0;
                s.Sr.drawMode = (SpriteDrawMode)((st.Bits >> 2) & 3);
                s.Sr.maskInteraction = (SpriteMaskInteraction)((st.Bits >> 4) & 3);
            }
            if ((mask & SpriteState.Sort) != 0)
            {
                s.Sr.sortingLayerID = st.SortingLayer;
                s.Sr.sortingOrder = st.SortingOrder;
                s.Go.layer = st.Layer;
            }
            if ((mask & SpriteState.Mat) != 0) ApplyMaterial(s);
            if ((mask & SpriteState.Size) != 0 && s.Sr.drawMode != SpriteDrawMode.Simple) s.Sr.size = new Vector2(st.W, st.H);
        }

        private static void ApplyMaterial(MirrorSprite s)
        {
            if (s.St.Material == 0) return;
            Material m = MaterialSync.Resolve(s.St.Material);
            if (m != null) s.Sr.sharedMaterial = m;
        }

        private static void ApplyMask(MirrorSprite s, uint mask)
        {
            SpriteState st = s.St;
            SpriteMask m = s.Mask;
            if ((mask & SpriteState.Sort) != 0)
            {
                m.sortingLayerID = st.SortingLayer;
                m.sortingOrder = st.SortingOrder;
                s.Go.layer = st.Layer;
            }
            if ((mask & SpriteState.MaskBit) != 0)
            {
                m.alphaCutoff = st.Cutoff;
                m.isCustomRangeActive = st.CustomRange;
                if (st.CustomRange)
                {
                    m.frontSortingLayerID = st.FrontLayer;
                    m.frontSortingOrder = st.FrontOrder;
                    m.backSortingLayerID = st.BackLayer;
                    m.backSortingOrder = st.BackOrder;
                }
            }
        }

        private static void ApplySpriteAsset(MirrorSprite s)
        {
            Sprite sprite = Sprites.Get(s.St.Sprite);
            if (s.Sr != null) s.Sr.sprite = sprite;
            else if (s.Mask != null) s.Mask.sprite = sprite;
        }

        private static void OnDespawn(NetReader r)
        {
            ushort serial = r.U16();
            uint frame = r.U32();
            int n = (int)r.VarU();
            bool live = Live(serial);
            for (int i = 0; i < n; i++)
            {
                ushort id = (ushort)r.VarU();
                MirrorSprite s;
                if (!live || !sprites.TryGetValue(id, out s)) continue;
                if (frame < s.LastFrame) continue;
                s.DespawnFrame = frame;
                if (s.Go != null && s.Shown) { s.Go.SetActive(false); s.Shown = false; }
            }
        }

        private static Material FallbackMaterial(string key)
        {
            int bar = key.LastIndexOf('|');
            if (bar < 0) return null;
            Shader shader = Shader.Find(key.Substring(bar + 1));
            if (shader == null) return null;
            Material m = new Material(shader);
            m.name = key.Substring(0, bar);
            return m;
        }

        // ------------------------------------------------------------------ cameras

        private static void OnCameraDef(NetReader r)
        {
            ushort serial = r.U16();
            byte id = r.U8();
            string name = r.Str();
            bool ortho = r.Bool();
            int mask = r.I32();
            CameraClearFlags clear = (CameraClearFlags)r.U8();
            Color32 bg = new Color32(r.U8(), r.U8(), r.U8(), r.U8());
            float depth = r.F32();
            float near = r.F32();
            float far = r.F32();
            float fov = r.F32();
            bool cuphead = r.Bool();
            if (serial != targetSerial) return;
            EnsureRoot();
            MirrorCamera mc;
            if (!cameras.TryGetValue(id, out mc))
            {
                GameObject go = new GameObject("cam " + name);
                go.transform.SetParent(cameraRoot, false);
                mc = new MirrorCamera { Cam = go.AddComponent<Camera>() };
                cameras[id] = mc;
            }
            Camera c = mc.Cam;
            c.orthographic = ortho;
            c.cullingMask = mask;
            c.clearFlags = clear;
            c.backgroundColor = bg;
            c.depth = depth;
            c.nearClipPlane = near;
            c.farClipPlane = far;
            c.fieldOfView = fov;
            c.enabled = false; // until the first transform update
            mc.CupheadLetterbox = cuphead;
        }

        private static void OnCamera(NetReader r)
        {
            ushort serial = r.U16();
            uint frame = r.U32();
            int count = r.U8();
            bool live = Live(serial);
            updatedCameras.Clear();
            for (int i = 0; i < count; i++)
            {
                byte id = r.U8();
                Vector3 p = new Vector3(r.F32(), r.F32(), r.F32());
                Quaternion q = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
                float size = r.F32();
                Rect rect = new Rect(r.F32(), r.F32(), r.F32(), r.F32());
                MirrorCamera mc;
                if (!live || !cameras.TryGetValue(id, out mc) || mc.Cam == null || frame < mc.LastFrame) continue;
                Transform t = mc.Cam.transform;
                t.position = p;
                t.rotation = q;
                mc.Cam.orthographicSize = size;
                mc.Cam.rect = mc.CupheadLetterbox ? Letterbox.Viewport() : rect;
                mc.Cam.enabled = true;
                mc.LastFrame = frame;
                updatedCameras.Add(id);
            }
            if (!live) return;
            foreach (KeyValuePair<byte, MirrorCamera> kv in cameras)
            {
                if (!updatedCameras.Contains(kv.Key) && kv.Value.Cam != null && frame >= kv.Value.LastFrame) kv.Value.Cam.enabled = false;
            }
        }

        private static readonly HashSet<byte> updatedCameras = new HashSet<byte>();

        // ------------------------------------------------------------------ UGUI

        private static readonly UiState uiScratch = new UiState();

        /// <summary>Private layer for mirrored UI so no mirrored scene camera draws it twice.</summary>
        private const int UiLayer = 31;
        private static Camera uiCamera;

        /// <summary>
        /// Cuphead draws its HUD through ScreenSpace-Camera canvases (UICamera); some of its UI materials
        /// (e.g. mat_ui_overlay_100, Sprites/Color/Overlay) render white on an Overlay canvas, so do the same.
        /// </summary>
        private static Camera UiCamera()
        {
            if (uiCamera != null) return uiCamera;
            GameObject go = new GameObject("mirror ui camera");
            go.transform.SetParent(uiRoot.transform, false);
            go.transform.position = new Vector3(0f, 0f, -10000f);
            uiCamera = go.AddComponent<Camera>();
            uiCamera.orthographic = true;
            uiCamera.orthographicSize = 360f;
            uiCamera.clearFlags = CameraClearFlags.Depth;
            uiCamera.cullingMask = 1 << UiLayer;
            uiCamera.depth = 11f; // above the mirrored UICamera (10), below SceneLoader (90) and the film renderer (100)
            uiCamera.nearClipPlane = 0.1f;
            uiCamera.farClipPlane = 100f;
            return uiCamera;
        }

        private static Canvas CanvasFor(short order)
        {
            Canvas c;
            if (canvases.TryGetValue(order, out c) && c != null) return c;
            GameObject go = new GameObject("canvas " + order);
            go.layer = UiLayer;
            go.transform.SetParent(uiRoot.transform, false);
            c = go.AddComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceCamera;
            c.worldCamera = UiCamera();
            c.planeDistance = 10f;
            c.sortingOrder = order;
            canvases[order] = c;
            return c;
        }

        private static void OnUiSnapshot(NetReader r)
        {
            ushort serial = r.U16();
            uint frame = r.U32();
            bool live = Live(serial);
            while (r.Remaining > 0)
            {
                ushort id = (ushort)r.VarU();
                uint mask = r.VarU();
                MirrorGraphic g = null;
                if (live && !graphics.TryGetValue(id, out g))
                {
                    g = new MirrorGraphic();
                    graphics[id] = g;
                }
                if (g == null || frame < g.LastFrame || frame <= g.DespawnFrame)
                {
                    UiState junk = uiScratch;
                    junk.Read(r, mask);
                    continue;
                }
                short oldOrder = g.St.CanvasOrder;
                int oldDepth = g.St.Depth;
                g.St.Read(r, mask);
                g.LastFrame = frame;
                ApplyGraphic(g, mask, oldOrder, oldDepth);
            }
        }

        private static void ApplyGraphic(MirrorGraphic g, uint mask, short oldOrder, int oldDepth)
        {
            UiState st = g.St;
            if (g.Go == null || g.BuiltKind != st.Kind)
            {
                if (g.Go != null) UnityEngine.Object.Destroy(g.Go);
                g.Go = new GameObject("ui", typeof(RectTransform));
                g.Go.layer = UiLayer;
                g.Rt = (RectTransform)g.Go.transform;
                g.Rt.SetParent(CanvasFor(st.CanvasOrder).transform, false);
                g.Rt.anchorMin = Vector2.zero;
                g.Rt.anchorMax = Vector2.zero;
                g.Rt.pivot = new Vector2(0.5f, 0.5f);
                switch (st.Kind)
                {
                    case UiState.KindText:
                        Text text = g.Go.AddComponent<Text>();
                        text.horizontalOverflow = HorizontalWrapMode.Wrap;
                        text.verticalOverflow = VerticalWrapMode.Overflow;
                        text.raycastTarget = false;
                        g.G = text;
                        break;
                    case UiState.KindTmp:
                        TMPro.TextMeshProUGUI tmp = g.Go.AddComponent<TMPro.TextMeshProUGUI>();
                        tmp.raycastTarget = false;
                        tmp.enableWordWrapping = true;
                        g.G = tmp;
                        break;
                    default:
                        Image img = g.Go.AddComponent<Image>();
                        img.raycastTarget = false;
                        g.G = img;
                        break;
                }
                g.BuiltKind = st.Kind;
                g.Shown = true;
                mask = UiState.All;
                dirtyCanvases.Add(st.CanvasOrder);
            }
            if (!g.Shown) { g.Go.SetActive(true); g.Shown = true; }
            if ((mask & UiState.Order) != 0 && (st.CanvasOrder != oldOrder || st.Depth != oldDepth))
            {
                if (st.CanvasOrder != oldOrder) g.Rt.SetParent(CanvasFor(st.CanvasOrder).transform, false);
                dirtyCanvases.Add(st.CanvasOrder);
            }
            if ((mask & (UiState.ColorBit | UiState.Mat)) != 0)
            {
                Material m = st.Material != 0 ? MaterialSync.Resolve(st.Material) : null;
                Color color = st.Color;
                if (m != null && m.shader != null && m.shader.name.IndexOf("Overlay", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Cuphead's Sprites/Color/Overlay UI materials treat vertex colour 0.5 as "untinted" and do not render
                    // correctly outside their original canvas setup; approximate with the default UI material.
                    m = null;
                    color = new Color(Mathf.Clamp01(color.r * 2f), Mathf.Clamp01(color.g * 2f), Mathf.Clamp01(color.b * 2f), color.a);
                }
                g.G.material = m;
                g.G.color = color;
            }
            Image image = g.G as Image;
            if (image != null)
            {
                if ((mask & UiState.SpriteId) != 0) image.sprite = Sprites.Get(st.Sprite);
                if ((mask & UiState.Fill) != 0)
                {
                    image.type = (Image.Type)st.ImageType;
                    image.fillMethod = (Image.FillMethod)st.FillMethod;
                    image.fillOrigin = st.FillOrigin;
                    image.fillClockwise = st.FillClockwise;
                    image.preserveAspect = st.PreserveAspect;
                    image.fillAmount = st.FillAmount;
                }
            }
            if ((mask & UiState.TextBit) != 0) ApplyText(g);
            // Place it now: snapshots can arrive after this frame's LayoutGraphics, and a new graphic would otherwise
            // draw one frame at the canvas corner (unclipped).
            if ((mask & (UiState.RectBit | UiState.ClipBit)) != 0) LayoutGraphic(g, Letterbox.Pixels());
        }

        private static void ApplyText(MirrorGraphic g)
        {
            UiState st = g.St;
            Rect box = Letterbox.Pixels();
            Text text = g.G as Text;
            if (text != null)
            {
                Font f = Fonts.Get(st.Font);
                if (f != null) text.font = f;
                else if (text.font == null) text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                text.text = st.Text;
                text.fontSize = Mathf.Max(1, Mathf.RoundToInt(st.FontSize * box.height));
                text.alignment = (TextAnchor)st.Alignment;
                text.fontStyle = (FontStyle)st.FontStyle;
                text.lineSpacing = st.LineSpacing;
                text.supportRichText = st.RichText;
                return;
            }
            TMPro.TextMeshProUGUI tmp = g.G as TMPro.TextMeshProUGUI;
            if (tmp != null)
            {
                TMPro.TMP_FontAsset f = TmpFonts.Get(st.Font);
                if (f != null) tmp.font = f;
                tmp.text = st.Text;
                tmp.fontSize = st.FontSize * box.height;
                int[] aligns = HostSync.TmpAlignments;
                if (st.Alignment < aligns.Length) tmp.alignment = (TMPro.TextAlignmentOptions)aligns[st.Alignment];
                tmp.fontStyle = (TMPro.FontStyles)st.FontStyle;
                tmp.lineSpacing = st.LineSpacing;
                tmp.richText = st.RichText;
            }
        }

        private static Rect lastBox;

        private static void LayoutGraphics()
        {
            Rect box = Letterbox.Pixels();
            bool boxChanged = box != lastBox;
            lastBox = box;
            foreach (MirrorGraphic g in graphics.Values)
            {
                if (g.Go == null || !g.Shown) continue;
                LayoutGraphic(g, box);
                if (boxChanged && g.St.Kind != UiState.KindImage) ApplyText(g);
            }
        }

        private static void LayoutGraphic(MirrorGraphic g, Rect box)
        {
            UiState st = g.St;
            g.Rt.anchoredPosition = new Vector2(box.x + st.CX * box.width, box.y + st.CY * box.height);
            g.Rt.sizeDelta = new Vector2(st.W * box.width, st.H * box.height);
            g.Rt.localEulerAngles = new Vector3(0f, 0f, st.Angle);
            if (st.Clip)
            {
                // Canvas-local rect, as RectMask2D computes it: anchors sit at the canvas' bottom-left corner.
                Rect canvasRect = ((RectTransform)g.Rt.parent).rect;
                g.G.canvasRenderer.EnableRectClipping(Rect.MinMaxRect(
                    canvasRect.xMin + box.x + st.ClipX0 * box.width, canvasRect.yMin + box.y + st.ClipY0 * box.height,
                    canvasRect.xMin + box.x + st.ClipX1 * box.width, canvasRect.yMin + box.y + st.ClipY1 * box.height));
                g.ClipApplied = true;
            }
            else if (g.ClipApplied)
            {
                g.G.canvasRenderer.DisableRectClipping();
                g.ClipApplied = false;
            }
        }

        private static readonly List<MirrorGraphic> sortBuffer = new List<MirrorGraphic>();

        private static void SortCanvases()
        {
            foreach (short order in dirtyCanvases)
            {
                sortBuffer.Clear();
                foreach (MirrorGraphic g in graphics.Values)
                {
                    if (g.Go != null && g.St.CanvasOrder == order) sortBuffer.Add(g);
                }
                sortBuffer.Sort((a, b) => a.St.Depth.CompareTo(b.St.Depth));
                for (int i = 0; i < sortBuffer.Count; i++) sortBuffer[i].Rt.SetSiblingIndex(i);
            }
            dirtyCanvases.Clear();
        }

        private static void OnUiDespawn(NetReader r)
        {
            ushort serial = r.U16();
            uint frame = r.U32();
            int n = (int)r.VarU();
            bool live = Live(serial);
            for (int i = 0; i < n; i++)
            {
                ushort id = (ushort)r.VarU();
                MirrorGraphic g;
                if (!live || !graphics.TryGetValue(id, out g)) continue;
                if (frame < g.LastFrame) continue;
                g.DespawnFrame = frame;
                if (g.Go != null && g.Shown) { g.Go.SetActive(false); g.Shown = false; }
            }
        }
    }

    /// <summary>While mirroring, the client's game must not load scenes or write the save file on its own.</summary>
    [HarmonyPatch]
    internal static class ClientGuards
    {
        [HarmonyPatch(typeof(SceneLoader), "LoadScene", new[] { typeof(Scenes), typeof(SceneLoader.Transition), typeof(SceneLoader.Transition), typeof(SceneLoader.Icon), typeof(SceneLoader.Context) }), HarmonyPrefix]
        private static bool LoadScene()
        {
            return !ClientMirror.Mirroring || ClientMirror.AllowSceneLoad;
        }

        [HarmonyPatch(typeof(SceneLoader), "ReloadLevel"), HarmonyPrefix]
        private static bool ReloadLevel()
        {
            return !ClientMirror.Mirroring || ClientMirror.AllowSceneLoad;
        }

        [HarmonyPatch(typeof(PlayerData), "SaveCurrentFile"), HarmonyPrefix]
        private static bool SaveCurrentFile()
        {
            return !ClientMirror.Mirroring;
        }
    }
}
