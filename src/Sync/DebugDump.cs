using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CupheadOnline.Sync
{
    /// <summary>Set CUPHEAD_ONLINE_DEBUG=1 to log every renderer/camera/canvas of each loaded scene (host side).</summary>
    public static class DebugDump
    {
        public static readonly bool Enabled = System.Environment.GetEnvironmentVariable("CUPHEAD_ONLINE_DEBUG") == "1";

        private static string Path(Transform t)
        {
            string p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        public static void Dump(string reason)
        {
            Scene scene = SceneManager.GetActiveScene();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== DebugDump " + reason + " scene=" + scene.name);
            foreach (Camera c in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (c.gameObject.scene.name == null) continue;
                sb.AppendLine(string.Format("CAM {0} scene={1} en={2} depth={3} mask={4:X} rt={5} ortho={6} size={7}",
                    Path(c.transform), c.gameObject.scene.name, c.isActiveAndEnabled, c.depth, c.cullingMask, c.targetTexture != null ? c.targetTexture.name : "-", c.orthographic, c.orthographicSize));
            }
            foreach (Renderer r in Object.FindObjectsOfType<Renderer>())
            {
                SpriteRenderer sr = r as SpriteRenderer;
                sb.AppendLine(string.Format("REN {0} {1} scene={2} en={3} vis={4} layer={5} sort={6}/{7} mat={8} sprite={9}",
                    r.GetType().Name, Path(r.transform), r.gameObject.scene.name, r.enabled, r.isVisible, r.gameObject.layer, r.sortingLayerName, r.sortingOrder,
                    r.sharedMaterial != null ? r.sharedMaterial.name : "-", sr != null && sr.sprite != null ? sr.sprite.name : "-"));
            }
            foreach (Canvas cv in Object.FindObjectsOfType<Canvas>())
            {
                sb.AppendLine(string.Format("CANVAS {0} scene={1} en={2} mode={3} order={4} root={5} layer={6} cam={7} scale={8}", Path(cv.transform), cv.gameObject.scene.name, cv.enabled, cv.renderMode, cv.sortingOrder, cv.isRootCanvas, cv.gameObject.layer,
                    cv.worldCamera != null ? cv.worldCamera.name : "-", cv.transform.lossyScale));
                if (cv.gameObject.scene.name == "DontDestroyOnLoad" && !Path(cv.transform).StartsWith("CupheadOnline.Mirror")) continue;
                foreach (UnityEngine.UI.Graphic g in cv.GetComponentsInChildren<UnityEngine.UI.Graphic>(false))
                {
                    UnityEngine.UI.Image img = g as UnityEngine.UI.Image;
                    Vector3[] c = new Vector3[4];
                    g.rectTransform.GetWorldCorners(c);
                    Texture tex = g.mainTexture;
                    sb.AppendLine(string.Format("  GFX {0} {1} en={2} sprite={3} override={4} tex={10} packed={11} mat={5} color={6} corner0={7} corner2={8} cull={9}",
                        g.GetType().Name, Path(g.transform), g.enabled, img != null && img.sprite != null ? img.sprite.name : "-", img != null && img.overrideSprite != null ? img.overrideSprite.name : "-",
                        g.material != null ? g.material.name + "/" + (g.material.shader != null ? g.material.shader.name : "") : "-", g.color, c[0], c[2], g.canvasRenderer.cull,
                        tex != null ? tex.name + " " + tex.width + "x" + tex.height : "null", img != null && img.overrideSprite != null ? img.overrideSprite.packed.ToString() : "-"));
                    TMPro.TextMeshProUGUI tmp = g as TMPro.TextMeshProUGUI;
                    if (tmp != null)
                    {
                        // TMP_Text.color hides Graphic.color, and its font material is not Graphic.material.
                        Material fm = tmp.fontSharedMaterial;
                        sb.AppendLine(string.Format("    TMP color={0} font={1} fontMat={2} face={3} wrap={4} lines={5}",
                            tmp.color, tmp.font != null ? tmp.font.name : "-", fm != null ? fm.name : "-", tmp.faceColor, tmp.enableWordWrapping, tmp.textInfo != null ? tmp.textInfo.lineCount : 0));
                    }
                }
            }
            Plugin.Log.LogInfo(sb.ToString());
        }
    
        /// <summary>Effects the mirror needs extra care for: particle systems, masks, material instances and property blocks.</summary>
        public static void DumpEffects(string reason)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== EffectsDump " + reason + " scene=" + SceneManager.GetActiveScene().name);
            foreach (ParticleSystem ps in Object.FindObjectsOfType<ParticleSystem>())
            {
                ParticleSystemRenderer r = ps.GetComponent<ParticleSystemRenderer>();
                ParticleSystem.MainModule main = ps.main;
                ParticleSystem.TextureSheetAnimationModule tsa = ps.textureSheetAnimation;
                Material m = r != null ? r.sharedMaterial : null;
                sb.AppendLine(string.Format("PS {0} alive={1} play={2} count={3} max={4} space={5} scaling={6} rmode={7} align={8} mat={9}/{10} tex={11} sort={12}/{13} layer={14} vis={15} en={16} mask={17} tsa={18} {19} {20}x{21} anim={22} sprites={23} col={24} size={25} rot={26} vel={27} lossy={28} minmax={29}/{30} sortMode={31}",
                    Path(ps.transform), ps.IsAlive(), ps.isPlaying, ps.particleCount, main.maxParticles, main.simulationSpace, main.scalingMode,
                    r != null ? r.renderMode.ToString() : "-", r != null ? r.alignment.ToString() : "-",
                    m != null ? m.name : "-", m != null && m.shader != null ? m.shader.name : "-", m != null && m.mainTexture != null ? m.mainTexture.name : "-",
                    r != null ? r.sortingLayerName : "-", r != null ? r.sortingOrder : 0, ps.gameObject.layer, r != null && r.isVisible, r != null && r.enabled, r != null ? r.maskInteraction.ToString() : "-",
                    tsa.enabled, tsa.mode, tsa.numTilesX, tsa.numTilesY, tsa.animation, tsa.mode == ParticleSystemAnimationMode.Sprites ? tsa.spriteCount : 0,
                    ps.colorOverLifetime.enabled, ps.sizeOverLifetime.enabled, ps.rotationOverLifetime.enabled, ps.velocityOverLifetime.enabled, ps.transform.lossyScale,
                    r != null ? r.minParticleSize : 0f, r != null ? r.maxParticleSize : 0f, r != null ? r.sortMode.ToString() : "-"));
            }
            foreach (SpriteMask sm in Object.FindObjectsOfType<SpriteMask>())
            {
                sb.AppendLine(string.Format("SMASK {0} en={1} vis={2} sprite={3} cutoff={4} custom={5} front={6}/{7} back={8}/{9} sort={10}/{11}", Path(sm.transform), sm.enabled, sm.isVisible,
                    sm.sprite != null ? sm.sprite.name : "-", sm.alphaCutoff, sm.isCustomRangeActive, sm.frontSortingLayerID, sm.frontSortingOrder, sm.backSortingLayerID, sm.backSortingOrder, sm.sortingLayerID, sm.sortingOrder));
            }
            MaterialPropertyBlock block = new MaterialPropertyBlock();
            int blocks = 0, instances = 0, masked = 0, blockProps = 0;
            foreach (SpriteRenderer sr in Object.FindObjectsOfType<SpriteRenderer>())
            {
                if (!sr.enabled || !sr.isVisible) continue;
                sr.GetPropertyBlock(block);
                Material m = sr.sharedMaterial;
                bool inst = m != null && m.name.EndsWith(" (Instance)");
                bool hasBlock = !block.isEmpty;
                if (sr.maskInteraction != SpriteMaskInteraction.None) masked++;
                if (inst) instances++;
                if (hasBlock) blocks++;
                string overrides = MaterialSync.DescribeBlock(sr, block);
                if (overrides.Length > 0) blockProps++;
                if (inst || overrides.Length > 0 || sr.maskInteraction != SpriteMaskInteraction.None || (m != null && m.shader != null && m.shader.name != "Sprites/Default"))
                    sb.AppendLine(string.Format("SR {0} mat={1}/{2} inst={3} block={4} mask={5} color={6} overrides={7}", Path(sr.transform), m != null ? m.name : "-", m != null && m.shader != null ? m.shader.name : "-", inst, hasBlock, sr.maskInteraction, sr.color, overrides));
            }
            sb.AppendLine("SR totals: instances=" + instances + " blocks=" + blocks + " masked=" + masked + " blockOverrides=" + blockProps);
            foreach (UnityEngine.UI.Mask mk in Object.FindObjectsOfType<UnityEngine.UI.Mask>())
            {
                UnityEngine.UI.Image img = mk.graphic as UnityEngine.UI.Image;
                sb.AppendLine(string.Format("UIMASK {0} en={1} show={2} sprite={3} children={4}", Path(mk.transform), mk.isActiveAndEnabled, mk.showMaskGraphic, img != null && img.sprite != null ? img.sprite.name : "-", mk.GetComponentsInChildren<UnityEngine.UI.Graphic>().Length));
            }
            foreach (UnityEngine.UI.RectMask2D mk in Object.FindObjectsOfType<UnityEngine.UI.RectMask2D>())
                sb.AppendLine(string.Format("RECTMASK {0} en={1} children={2}", Path(mk.transform), mk.isActiveAndEnabled, mk.GetComponentsInChildren<UnityEngine.UI.Graphic>().Length));
            Plugin.Log.LogInfo(sb.ToString());
        }
}
}
