using System;
using System.Collections.Generic;
using CupheadOnline.Net;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Host side of the mirror: every frame captures the active scene's SpriteRenderers, SpriteMasks, cameras, UGUI
    /// graphics (with their Mask/RectMask2D clipping) and runtime material parameters, and sends what changed.
    /// The host is authoritative; the client never simulates gameplay.
    /// </summary>
    public static class HostSync
    {
        public const string LoadHelperScene = "scene_load_helper";

        private sealed class Tracked<TState>
        {
            public ushort Id;
            public TState Last;
            public bool Visible;
            public int FullSends;   // > 0: send all fields
            public int SeenFrame;
        }

        public static readonly HostAssetTable<Sprite> Sprites = new HostAssetTable<Sprite>(AssetKeys.SpriteWithAtlas, MsgType.SpriteDef);
        public static readonly HostAssetTable<Material> Materials = new HostAssetTable<Material>(AssetKeys.Material, MsgType.MaterialDef);
        public static readonly HostAssetTable<UnityEngine.Object> Fonts = new HostAssetTable<UnityEngine.Object>(AssetKeys.Font, MsgType.FontDef);

        private static readonly Dictionary<int, Tracked<SpriteState>> renderers = new Dictionary<int, Tracked<SpriteState>>();
        private static readonly Dictionary<int, Tracked<UiState>> graphics = new Dictionary<int, Tracked<UiState>>();
        private static readonly Dictionary<int, int> cameraIds = new Dictionary<int, int>();
        private static readonly IdPool rendererIds = new IdPool();
        private static readonly IdPool graphicIds = new IdPool();

        private static uint frame;
        private static ushort sceneSerial;
        public static ushort CurrentSerial { get { return sceneSerial; } }
        private static string sceneName;
        private static bool sceneDirty;
        private static bool active;
        private static int refreshCursor;
        private static float lastDiag;
        private static int uiRefreshCursor;

        /// <summary>Send at most every N frames (1 = every frame). Configurable for slow links.</summary>
        public static int SendInterval = 1;

        private static readonly NetWriter chunk = new NetWriter(1400);
        private static readonly List<ushort> despawns = new List<ushort>();
        private static readonly List<int> scratchKeys = new List<int>();

        public static void Install()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == LoadHelperScene) return;
            // Bump now (not at end of frame) so audio from the new scene's Awake/Start carries the new serial.
            sceneSerial++;
            sceneDirty = true;
        }

        /// <summary>The client finished loading: everything it saw while loading was dropped, so resend in full.</summary>
        public static void OnClientSceneReady(ushort serial)
        {
            if (serial != sceneSerial) return;
            foreach (Tracked<SpriteState> t in renderers.Values) t.Visible = false;
            foreach (Tracked<UiState> t in graphics.Values) t.Visible = false;
            cameraIds.Clear();
            MaterialSync.ResendValues();
        }

        public static void OnClientJoined(Connection connection)
        {
            active = true;
            Application.runInBackground = true;
            RemoteInput.ResetHost();
            RemoteInput.DriveHostPlayerTwo = true;
            Sprites.ResetSent();
            Materials.ResetSent();
            Fonts.ResetSent();
            MaterialSync.ResetHost();
            cameraIds.Clear();
            foreach (Tracked<SpriteState> t in renderers.Values) { t.Visible = false; }
            foreach (Tracked<UiState> t in graphics.Values) { t.Visible = false; }
            sceneDirty = true;
        }

        public static void OnSessionEnded()
        {
            if (!active) return;
            active = false;
            RemoteInput.DriveHostPlayerTwo = false;
            RemoteInput.ResetHost();
            try
            {
                if (PlayerManager.Multiplayer) PlayerManager.PlayerLeave(PlayerId.PlayerTwo);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not remove player 2: " + ex.Message);
            }
        }

        public static void Tick(Connection connection)
        {
            if (!active) return;
            Scene activeScene = SceneManager.GetActiveScene();
            if (sceneDirty && activeScene.name != LoadHelperScene && activeScene.isLoaded)
            {
                sceneDirty = false;
                SendScene(connection, activeScene.name);
            }
            AudioSync.FlushBatch(connection);
            frame++;
            if (SendInterval > 1 && frame % SendInterval != 0) return;
            if (activeScene.name != sceneName) return; // mid-load

            frameCameras = Camera.allCameras;
            MaterialSync.BeginHostFrame(frame);
            CaptureRenderers(connection, activeScene);
            if (Time.unscaledTime - lastDiag > 5f)
            {
                lastDiag = Time.unscaledTime;
                int visible = 0;
                foreach (Tracked<SpriteState> t in renderers.Values) if (t.Visible) visible++;
                int ui = 0;
                foreach (Tracked<UiState> t in graphics.Values) if (t.Visible) ui++;
                Plugin.Log.LogInfo("[host] scene=" + sceneName + " sprites=" + visible + " (masks " + lastMaskCount + ") ui=" + ui + " (clipped " + lastClippedCount + ") " + MaterialSync.HostSummary() + " cameras=" + cameraIds.Count + " p2=" + PlayerManager.Multiplayer + " rtt=" + Mathf.RoundToInt((float)connection.RttSeconds * 1000) + "ms");
            }
            CaptureCameras(connection, activeScene);
            CaptureGraphics(connection, activeScene);
            MaterialSync.Flush(connection, sceneSerial, frame);
        }

        private static void SendScene(Connection connection, string name)
        {
            if (sceneSerial == 0) sceneSerial = 1;
            sceneName = name;
            // Everything currently tracked belongs to the old scene; the client wipes its mirror on Scene.
            foreach (Tracked<SpriteState> t in renderers.Values) t.Visible = false;
            foreach (Tracked<UiState> t in graphics.Values) t.Visible = false;
            cameraIds.Clear();
            NetWriter w = new NetWriter(96);
            w.U16(sceneSerial);
            w.Str(name);
            int level = 0, mode = 0;
            try { level = (int)SceneLoader.CurrentLevel; } catch { }
            try { mode = (int)Level.CurrentMode; } catch { }
            w.I32(level);
            w.I32(mode);
            w.Bool(PlayerManager.Multiplayer);
            connection.SendReliable(MsgType.Scene, w);
            Plugin.Log.LogInfo("Mirroring scene " + name + " (#" + sceneSerial + ")");
        }

        // ------------------------------------------------------------------ sprites

        private static int rendererChunkCount;
        private static int lastMaskCount, lastClippedCount;

        private static void CaptureRenderers(Connection connection, Scene scene)
        {
            SpriteRenderer[] all = UnityEngine.Object.FindObjectsOfType<SpriteRenderer>();
            int seen = (int)frame;
            BeginChunk(MsgType.Snapshot);
            rendererChunkCount = 0;
            int refreshBudget = Math.Max(4, renderers.Count / 30);

            for (int i = 0; i < all.Length; i++)
            {
                SpriteRenderer sr = all[i];
                if (!sr.enabled || sr.sprite == null || sr.gameObject.scene != scene || !sr.isVisible) continue;
                SpriteState st = SpriteState.Capture(sr, Sprites, MaterialSync.HostId);
                EmitRenderer(connection, sr.GetInstanceID(), ref st, seen, ref refreshBudget);
            }
            // SpriteMasks only write the stencil, so they are sent regardless of isVisible (few, and cheap).
            SpriteMask[] masks = UnityEngine.Object.FindObjectsOfType<SpriteMask>();
            lastMaskCount = 0;
            for (int i = 0; i < masks.Length; i++)
            {
                SpriteMask sm = masks[i];
                if (!sm.enabled || sm.sprite == null || sm.gameObject.scene != scene) continue;
                SpriteState st = SpriteState.Capture(sm, Sprites);
                EmitRenderer(connection, sm.GetInstanceID(), ref st, seen, ref refreshBudget);
                lastMaskCount++;
            }
            refreshCursor++;
            // Defs go out (reliable) in the same flush as the snapshot that needs them.
            Sprites.FlushDefs(connection);
            Materials.FlushDefs(connection);
            EndChunk(connection, rendererChunkCount);

            // Hidden or destroyed renderers.
            despawns.Clear();
            scratchKeys.Clear();
            foreach (KeyValuePair<int, Tracked<SpriteState>> kv in renderers)
            {
                Tracked<SpriteState> t = kv.Value;
                if (t.SeenFrame == seen) continue;
                if (t.Visible) { t.Visible = false; despawns.Add(t.Id); }
                if (seen - t.SeenFrame > 600) scratchKeys.Add(kv.Key); // long gone: recycle
            }
            for (int i = 0; i < scratchKeys.Count; i++)
            {
                rendererIds.Release(renderers[scratchKeys[i]].Id);
                renderers.Remove(scratchKeys[i]);
            }
            SendDespawns(connection, MsgType.Despawn);
        }

        private static void EmitRenderer(Connection connection, int key, ref SpriteState st, int seen, ref int refreshBudget)
        {
            Tracked<SpriteState> t;
            if (!renderers.TryGetValue(key, out t))
            {
                t = new Tracked<SpriteState> { Id = rendererIds.Take() };
                renderers[key] = t;
            }
            t.SeenFrame = seen;
            uint mask;
            if (!t.Visible) { t.Visible = true; t.FullSends = 3; }
            if (t.FullSends > 0) { t.FullSends--; mask = SpriteState.All; }
            else
            {
                mask = st.Diff(ref t.Last);
                if (mask == 0 && refreshBudget > 0 && (t.Id % 30) == (refreshCursor % 30)) { mask = SpriteState.All; refreshBudget--; }
            }
            // Compare future frames against what the client actually has, so slow drifts still get sent.
            t.Last.Merge(ref st, mask);
            if (mask == 0) return;
            int before = chunk.Length;
            chunk.VarU(t.Id);
            chunk.VarU(mask);
            st.Write(chunk, mask);
            if (chunk.Length > connection.MaxUnreliablePayload)
            {
                // Roll the entity into a fresh chunk.
                int len = chunk.Length - before;
                byte[] tail = new byte[len];
                Buffer.BlockCopy(chunk.Buffer, before, tail, 0, len);
                chunk.Length = before;
                EndChunk(connection, rendererChunkCount);
                BeginChunk(MsgType.Snapshot);
                chunk.Bytes(tail, 0, len);
                rendererChunkCount = 0;
            }
            rendererChunkCount++;
        }

        private static MsgType chunkType = MsgType.Snapshot;

        private static void BeginChunk(MsgType type)
        {
            chunkType = type;
            chunk.Reset();
            chunk.U16(sceneSerial);
            chunk.U32(frame);
        }

        private static void EndChunk(Connection connection, int count)
        {
            if (count == 0) return;
            connection.SendUnreliable(chunkType, chunk);
        }

        private static void SendDespawns(Connection connection, MsgType type)
        {
            int i = 0;
            while (i < despawns.Count)
            {
                NetWriter w = new NetWriter(512);
                w.U16(sceneSerial);
                w.U32(frame);
                int n = Math.Min(300, despawns.Count - i);
                w.VarU((uint)n);
                for (int k = 0; k < n; k++) w.VarU(despawns[i + k]);
                connection.SendReliable(type, w);
                i += n;
            }
        }

        // ------------------------------------------------------------------ cameras

        private static Camera[] frameCameras = new Camera[0];

        private static void CaptureCameras(Connection connection, Scene scene)
        {
            Camera[] cams = frameCameras;
            NetWriter w = new NetWriter(256);
            w.U16(sceneSerial);
            w.U32(frame);
            int countPos = w.Length;
            w.U8(0);
            int count = 0;
            for (int i = 0; i < cams.Length && count < 16; i++)
            {
                Camera c = cams[i];
                if (c.gameObject.scene != scene || c.targetTexture != null) continue;
                int key = c.GetInstanceID();
                int id;
                if (!cameraIds.TryGetValue(key, out id))
                {
                    id = cameraIds.Count + 1;
                    cameraIds[key] = id;
                    NetWriter def = new NetWriter(96);
                    def.U16(sceneSerial);
                    def.U8((byte)id);
                    def.Str(c.name);
                    def.Bool(c.orthographic);
                    def.I32(c.cullingMask);
                    def.U8((byte)c.clearFlags);
                    Color32 bg = c.backgroundColor;
                    def.U8(bg.r); def.U8(bg.g); def.U8(bg.b); def.U8(bg.a);
                    def.F32(c.depth);
                    def.F32(c.nearClipPlane);
                    def.F32(c.farClipPlane);
                    def.F32(c.fieldOfView);
                    def.Bool(c.GetComponent<AbstractCupheadCamera>() != null);
                    connection.SendReliable(MsgType.CameraDef, def);
                }
                Transform t = c.transform;
                w.U8((byte)id);
                Vector3 p = t.position;
                Quaternion q = t.rotation;
                w.F32(p.x); w.F32(p.y); w.F32(p.z);
                w.F32(q.x); w.F32(q.y); w.F32(q.z); w.F32(q.w);
                w.F32(c.orthographicSize);
                Rect r = c.rect;
                w.F32(r.x); w.F32(r.y); w.F32(r.width); w.F32(r.height);
                count++;
            }
            w.Buffer[countPos] = (byte)count;
            connection.SendUnreliable(MsgType.Camera, w);
        }

        // ------------------------------------------------------------------ UGUI

        private static readonly Vector3[] corners = new Vector3[4];
        private static readonly List<Graphic> graphicBuffer = new List<Graphic>();

        private static void CaptureGraphics(Connection connection, Scene scene)
        {
            Canvas[] canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            int seen = (int)frame;
            int max = connection.MaxUnreliablePayload;
            BeginChunk(MsgType.UiSnapshot);
            int inChunk = 0;
            Rect box = Letterbox.Pixels();
            int refreshBudget = Math.Max(2, graphics.Count / 30);
            clipCache.Clear();
            lastClippedCount = 0;

            for (int ci = 0; ci < canvases.Length; ci++)
            {
                Canvas canvas = canvases[ci];
                if (!canvas.isRootCanvas || !canvas.enabled || canvas.gameObject.scene != scene) continue;
                Camera cam = null;
                if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                {
                    // ScreenSpaceCamera: worldCamera renders it. WorldSpace: worldCamera is only the UI event camera,
                    // the canvas is drawn by whichever camera renders its layer (e.g. UICamera for DialogueCanvas).
                    cam = canvas.renderMode == RenderMode.ScreenSpaceCamera && canvas.worldCamera != null
                        ? canvas.worldCamera
                        : SceneCamera(scene, canvas.gameObject.layer, canvas.transform.position);
                    if (cam == null) continue;
                }
                graphicBuffer.Clear();
                canvas.GetComponentsInChildren(false, graphicBuffer);
                for (int gi = 0; gi < graphicBuffer.Count; gi++)
                {
                    Graphic g = graphicBuffer[gi];
                    if (!g.enabled) continue;
                    UiState st;
                    if (!CaptureGraphic(g, canvas, cam, box, out st)) continue;
                    int key = g.GetInstanceID();
                    Tracked<UiState> t;
                    if (!graphics.TryGetValue(key, out t))
                    {
                        t = new Tracked<UiState> { Id = graphicIds.Take() };
                        graphics[key] = t;
                    }
                    t.SeenFrame = seen;
                    uint mask;
                    if (!t.Visible) { t.Visible = true; t.FullSends = 3; }
                    if (t.FullSends > 0) { t.FullSends--; mask = UiState.All; }
                    else
                    {
                        mask = st.Diff(ref t.Last);
                        if (mask == 0 && refreshBudget > 0 && (t.Id % 30) == (uiRefreshCursor % 30)) { mask = UiState.All; refreshBudget--; }
                    }
                    t.Last.Merge(ref st, mask);
                    if (mask == 0) continue;
                    int before = chunk.Length;
                    chunk.VarU(t.Id);
                    chunk.VarU(mask);
                    st.Write(chunk, mask);
                    if (chunk.Length > max)
                    {
                        int len = chunk.Length - before;
                        if (len + 6 > max) { chunk.Length = before; continue; } // pathological text, skip
                        byte[] tail = new byte[len];
                        Buffer.BlockCopy(chunk.Buffer, before, tail, 0, len);
                        chunk.Length = before;
                        EndChunk(connection, inChunk);
                        BeginChunk(MsgType.UiSnapshot);
                        chunk.Bytes(tail, 0, len);
                        inChunk = 0;
                    }
                    inChunk++;
                }
            }
            uiRefreshCursor++;
            Sprites.FlushDefs(connection);
            Materials.FlushDefs(connection);
            Fonts.FlushDefs(connection);
            EndChunk(connection, inChunk);

            despawns.Clear();
            scratchKeys.Clear();
            foreach (KeyValuePair<int, Tracked<UiState>> kv in graphics)
            {
                Tracked<UiState> t = kv.Value;
                if (t.SeenFrame == seen) continue;
                if (t.Visible) { t.Visible = false; despawns.Add(t.Id); }
                if (seen - t.SeenFrame > 600) scratchKeys.Add(kv.Key);
            }
            for (int i = 0; i < scratchKeys.Count; i++)
            {
                graphicIds.Release(graphics[scratchKeys[i]].Id);
                graphics.Remove(scratchKeys[i]);
            }
            SendDespawns(connection, MsgType.UiDespawn);
        }

        /// <summary>
        /// The scene camera that actually draws a world-space point on the given layer: layer in its culling mask and
        /// the point inside its frustum (UICamera also has the UI layer, but its clip planes exclude level-space
        /// canvases such as Dialoguer's speech bubbles). Highest depth wins.
        /// </summary>
        private static Camera SceneCamera(Scene scene, int layer, Vector3 worldPos)
        {
            Camera best = null;
            Camera fallback = null;
            Camera[] cams = frameCameras;
            for (int i = 0; i < cams.Length; i++)
            {
                Camera c = cams[i];
                if (c.gameObject.scene != scene || c.targetTexture != null) continue;
                if ((c.cullingMask & (1 << layer)) == 0) continue;
                if (fallback == null || c.GetComponent<AbstractCupheadCamera>() != null) fallback = c;
                Vector3 v = c.WorldToViewportPoint(worldPos);
                bool inside = v.z >= c.nearClipPlane && v.z <= c.farClipPlane && v.x > -1f && v.x < 2f && v.y > -1f && v.y < 2f;
                if (inside && (best == null || c.depth > best.depth)) best = c;
            }
            return best ?? fallback;
        }

        private static readonly List<CanvasGroup> groupBuffer = new List<CanvasGroup>();

        private static float GroupAlpha(Transform t)
        {
            float a = 1f;
            t.GetComponentsInParent(false, groupBuffer);
            List<CanvasGroup> groups = groupBuffer;
            for (int i = 0; i < groups.Count; i++)
            {
                a *= groups[i].alpha;
                if (groups[i].ignoreParentGroups) break;
            }
            return a;
        }

        private static bool CaptureGraphic(Graphic g, Canvas canvas, Camera cam, Rect box, out UiState st)
        {
            st = new UiState();
            // A Mask with showMaskGraphic off only writes the stencil (e.g. the HUD weapon slot); it is never visible.
            Mask mask = g.GetComponent<Mask>();
            if (mask != null && mask.enabled && !mask.showMaskGraphic) return false;
            Image image = g as Image;
            Text text = g as Text;
            TMPro.TextMeshProUGUI tmp = g as TMPro.TextMeshProUGUI;
            RawImage raw = g as RawImage;
            if (image != null)
            {
                Sprite active = image.overrideSprite; // the sprite actually drawn (Animators drive it)
                if (active == null && image.color.a <= 0f) return false;
                st.Kind = UiState.KindImage;
                st.Sprite = Sprites.IdOf(active);
                st.ImageType = (byte)image.type;
                st.FillMethod = (byte)image.fillMethod;
                st.FillOrigin = (byte)image.fillOrigin;
                st.FillClockwise = image.fillClockwise;
                st.PreserveAspect = image.preserveAspect;
                st.FillAmount = image.fillAmount;
            }
            else if (text != null)
            {
                if (string.IsNullOrEmpty(text.text)) return false;
                st.Kind = UiState.KindText;
                st.Text = text.text;
                st.Font = Fonts.IdOf(text.font);
                int size = text.resizeTextForBestFit ? text.cachedTextGenerator.fontSizeUsedForBestFit : text.fontSize;
                if (size <= 0) size = text.fontSize;
                st.FontSize = size; // converted to screen space below
                st.Alignment = (byte)text.alignment;
                st.FontStyle = (byte)text.fontStyle;
                st.LineSpacing = text.lineSpacing;
                st.RichText = text.supportRichText;
            }
            else if (tmp != null)
            {
                if (string.IsNullOrEmpty(tmp.text)) return false;
                st.Kind = UiState.KindTmp;
                st.Text = tmp.text;
                st.Font = Fonts.IdOf(tmp.font);
                st.FontSize = tmp.fontSize; // converted to screen space below
                st.Alignment = (byte)Mathf.Clamp(TmpAlignToIndex((int)tmp.alignment), 0, 255);
                st.FontStyle = (byte)tmp.fontStyle;
                st.LineSpacing = tmp.lineSpacing;
                st.RichText = tmp.richText;
            }
            else if (raw != null)
            {
                return false; // RawImage textures are runtime-generated; not replicated
            }
            else
            {
                return false;
            }

            Color c = g.color;
            // CanvasGroup alpha and CanvasRenderer alpha (CrossFadeAlpha, used for weapon icons and prompts).
            c.a *= GroupAlpha(g.transform) * g.canvasRenderer.GetAlpha();
            if (c.a <= 0.002f) return false;
            st.Color = c;
            st.Material = g.material != null && g.material != Graphic.defaultGraphicMaterial ? MaterialSync.HostId(g.material) : 0;

            g.rectTransform.GetWorldCorners(corners);
            for (int i = 0; i < 4; i++)
            {
                if (cam != null) corners[i] = RectTransformUtility.WorldToScreenPoint(cam, corners[i]);
            }
            // corners: 0 bottom-left, 1 top-left, 2 top-right, 3 bottom-right
            Vector2 c0 = corners[0], c1 = corners[1], c3 = corners[3];
            Vector2 center = (Vector2)(corners[0] + corners[2]) * 0.5f;
            float w = (c3 - c0).magnitude;
            float h = (c1 - c0).magnitude;
            if (w < 0.5f && h < 0.5f) return false;
            Vector2 right = c3 - c0;
            st.Angle = Mathf.Atan2(right.y, right.x) * Mathf.Rad2Deg;
            st.CX = (center.x - box.x) / box.width;
            st.CY = (center.y - box.y) / box.height;
            st.W = w / box.width;
            st.H = h / box.height;
            MaskableGraphic maskable = g as MaskableGraphic;
            Rect clip;
            if ((maskable == null || maskable.maskable) && ClipOf(g.transform.parent, canvas.transform, cam, box, out clip))
            {
                // Bounds of the graphic in the same normalized space; drop it when the mask hides it completely.
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                for (int i = 0; i < 4; i++)
                {
                    minX = Mathf.Min(minX, corners[i].x); maxX = Mathf.Max(maxX, corners[i].x);
                    minY = Mathf.Min(minY, corners[i].y); maxY = Mathf.Max(maxY, corners[i].y);
                }
                Rect bounds = Rect.MinMaxRect((minX - box.x) / box.width, (minY - box.y) / box.height, (maxX - box.x) / box.width, (maxY - box.y) / box.height);
                if (clip.xMax <= bounds.xMin || clip.xMin >= bounds.xMax || clip.yMax <= bounds.yMin || clip.yMin >= bounds.yMax) return false;
                if (clip.xMin > bounds.xMin || clip.yMin > bounds.yMin || clip.xMax < bounds.xMax || clip.yMax < bounds.yMax)
                {
                    st.Clip = true;
                    st.ClipX0 = clip.xMin; st.ClipY0 = clip.yMin; st.ClipX1 = clip.xMax; st.ClipY1 = clip.yMax;
                    lastClippedCount++;
                }
            }
            if (st.Kind == UiState.KindText || st.Kind == UiState.KindTmp)
            {
                // Canvas units -> screen pixels, valid for overlay, camera and world-space canvases alike.
                float localHeight = g.rectTransform.rect.height;
                float pixelsPerUnit = localHeight > 0.001f ? h / localHeight : canvas.scaleFactor;
                st.FontSize = st.FontSize * pixelsPerUnit / Mathf.Max(1f, box.height);
            }
            st.CanvasOrder = (short)canvas.sortingOrder;
            st.Depth = g.depth;
            return true;
        }

        private struct ClipEntry
        {
            public bool Has;
            public Rect Rect;
        }

        private static readonly Dictionary<int, ClipEntry> clipCache = new Dictionary<int, ClipEntry>();
        private static readonly Vector3[] maskCorners = new Vector3[4];

        /// <summary>
        /// The clip a UGUI Mask or RectMask2D on t or its ancestors (up to the canvas) applies to t's children, as an
        /// axis-aligned rect normalized to the letterbox. Stencil masks are approximated by their rect (the HUD weapon
        /// slot is rectangular). Cached per transform for the frame.
        /// </summary>
        private static bool ClipOf(Transform t, Transform canvasRoot, Camera cam, Rect box, out Rect clip)
        {
            clip = default(Rect);
            if (t == null) return false;
            int key = t.GetInstanceID();
            ClipEntry e;
            if (clipCache.TryGetValue(key, out e)) { clip = e.Rect; return e.Has; }
            bool has = t != canvasRoot && ClipOf(t.parent, canvasRoot, cam, box, out clip);
            RectTransform rt = t as RectTransform;
            if (rt != null && ClipsChildren(t))
            {
                rt.GetWorldCorners(maskCorners);
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                for (int i = 0; i < 4; i++)
                {
                    Vector3 p = cam != null ? (Vector3)RectTransformUtility.WorldToScreenPoint(cam, maskCorners[i]) : maskCorners[i];
                    minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                    minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
                }
                Rect own = Rect.MinMaxRect((minX - box.x) / box.width, (minY - box.y) / box.height, (maxX - box.x) / box.width, (maxY - box.y) / box.height);
                clip = has ? Rect.MinMaxRect(Mathf.Max(clip.xMin, own.xMin), Mathf.Max(clip.yMin, own.yMin), Mathf.Min(clip.xMax, own.xMax), Mathf.Min(clip.yMax, own.yMax)) : own;
                has = true;
            }
            clipCache[key] = new ClipEntry { Has = has, Rect = clip };
            return has;
        }

        private static bool ClipsChildren(Transform t)
        {
            Mask mask = t.GetComponent<Mask>();
            if (mask != null && mask.MaskEnabled()) return true;
            RectMask2D rect = t.GetComponent<RectMask2D>();
            return rect != null && rect.IsActive();
        }

        /// <summary>TextAlignmentOptions are bit flags; map to a compact 0..N index understood by both sides.</summary>
        public static int TmpAlignToIndex(int value)
        {
            for (int i = 0; i < TmpAlignments.Length; i++) if (TmpAlignments[i] == value) return i;
            return 0;
        }

        public static readonly int[] TmpAlignments = (int[])Enum.GetValues(typeof(TMPro.TextAlignmentOptions));
    }

    /// <summary>Small id allocator; released ids rest before reuse so late packets can't hit a recycled id.</summary>
    public sealed class IdPool
    {
        private ushort next = 1;
        private readonly Queue<ushort> free = new Queue<ushort>();

        public ushort Take()
        {
            if (free.Count > 512) return free.Dequeue();
            if (next == ushort.MaxValue) return free.Count > 0 ? free.Dequeue() : (ushort)1;
            return next++;
        }

        public void Release(ushort id) { free.Enqueue(id); }
    }
}
