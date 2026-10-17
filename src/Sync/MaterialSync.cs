using System;
using System.Collections.Generic;
using CupheadOnline.Net;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Shader parameters changed at runtime: renderer.material instances (Chess Bishop's _BlurAmount fade, the healer
    /// charm's _Amount flash) and shared materials edited in place (PlayerRecolorHandler's _RecolorFactor). The client
    /// resolves materials by name, so it would otherwise draw the pristine asset values.
    ///
    /// Host: for every material a mirrored renderer uses this frame, read its shader's declared properties and send
    /// the values that changed (unreliable, with periodic full refreshes). Client: a material whose values differ from
    /// its local copy is drawn with a per-id clone carrying the host's values.
    /// </summary>
    public static class MaterialSync
    {
        /// <summary>
        /// Unity 2017 cannot enumerate a shader's properties at runtime, so these are the properties declared by the
        /// game's shaders (read from the serialized shaders in its data files; v = color/vector, f = float/range).
        /// Textures, UGUI stencil/clip state, per-renderer sprite state and TextMesh Pro's own bookkeeping are left out.
        /// </summary>
        private const string DeclaredProperties =
            "_Ambient:f _Amount:f _AmountA:f _AmountB:f _Bevel:f _BevelClamp:f _BevelOffset:f _BevelRoundness:f " +
            "_BevelWidth:f _BlackThreshold:f _BlendValue:f _BlurAmount:f _BlurLerp:f _BumpFace:f _BumpOutline:f _Color:v " +
            "_ColorA:v _ColorB:v _Cutoff:f _CutoffBlue:f _CutoffGreen:f _CutoffMax:f _CutoffMin:f _DarkColor:v _Diffuse:f " +
            "_DiffusePower:f _DimFactor:f _DullBlacksCutoff:f _DullBlacksFactor:f _Emission:v _EmissionColor:v " +
            "_EnableExternalAlpha:f _FaceColor:v _FaceDilate:f _FaceShininess:f _FaceUVSpeedX:f " +
            "_FaceUVSpeedY:f _Factor:f _GlowColor:v _GlowInner:f _GlowOffset:f _GlowOuter:f _GlowPower:f " +
            "_IncludeBlacks:f _InvFade:f _Level:f _LightAngle:f _LightingVector:v _MonochromeBase:v " +
            "_OutlineColor:v _OutlineIncreaseFactor:f _OutlineShininess:f _OutlineSoftness:f _OutlineUVSpeedX:f " +
            "_OutlineUVSpeedY:f _OutlineWidth:f _RecolorFactor:f _ReferenceColor1:v _ReferenceColor2:v " +
            "_ReferenceColor3:v _ReflectFaceColor:v _ReflectOutlineColor:v _Reflectivity:f _Scale:f _ScaleOffsetRes:v " +
            "_ShadowColor:v _Shininess:f _SpecColor:v " +
            "_SpecularColor:v _SpecularPower:f _Texel:f _Threshold:f _Threshold2:f " +
            "_Threshold3:f _ThresholdShoes:f _UnderlayColor:v _UnderlayDilate:f _UnderlayOffsetX:f _UnderlayOffsetY:f " +
            "_UnderlaySoftness:f _value:f";

        private static string[] candidateNames;
        private static bool[] candidateIsVector;

        private static void EnsureCandidates()
        {
            if (candidateNames != null) return;
            string[] parts = DeclaredProperties.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            candidateNames = new string[parts.Length];
            candidateIsVector = new bool[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                int colon = parts[i].IndexOf(':');
                candidateNames[i] = parts[i].Substring(0, colon);
                candidateIsVector[i] = parts[i][colon + 1] == 'v';
            }
        }

        // ------------------------------------------------------------------ host

        private sealed class Layout
        {
            public string[] Names;
            public bool[] IsVector;
        }

        private sealed class HostMat
        {
            public int Id;
            public Material Mat;
            public int ShaderId;
            public Layout Layout;
            public bool LayoutSent;
            public int FullSends;
            public uint SeenFrame;
            public Vector4[] Sent;  // what the client was last sent (floats in .x)
        }

        private static readonly Dictionary<int, Layout> layoutByShader = new Dictionary<int, Layout>();
        private static readonly Dictionary<int, HostMat> hostMats = new Dictionary<int, HostMat>();
        private static readonly List<string> scratchNames = new List<string>();
        private static readonly List<bool> scratchVector = new List<bool>();
        private static uint hostFrame;

        private static Layout LayoutOf(Shader shader)
        {
            Layout layout;
            int key = shader != null ? shader.GetInstanceID() : 0;
            if (layoutByShader.TryGetValue(key, out layout)) return layout;
            EnsureCandidates();
            scratchNames.Clear();
            scratchVector.Clear();
            if (shader != null)
            {
                // HasProperty answers for the shader's declared properties; a throwaway material asks without touching game state.
                Material probe = new Material(shader);
                for (int i = 0; i < candidateNames.Length; i++)
                {
                    if (!probe.HasProperty(candidateNames[i])) continue;
                    scratchNames.Add(candidateNames[i]);
                    scratchVector.Add(candidateIsVector[i]);
                }
                UnityEngine.Object.Destroy(probe);
            }
            layout = new Layout { Names = scratchNames.ToArray(), IsVector = scratchVector.ToArray() };
            layoutByShader[key] = layout;
            return layout;
        }

        /// <summary>Host: the material id for a renderer's material, noting that its parameters must be mirrored this frame.</summary>
        public static int HostId(Material m)
        {
            int id = HostSync.Materials.IdOf(m);
            if (id == 0) return 0;
            HostMat t;
            if (!hostMats.TryGetValue(id, out t))
            {
                t = new HostMat { Id = id, Mat = m, ShaderId = int.MinValue };
                hostMats[id] = t;
            }
            t.SeenFrame = hostFrame;
            int shaderId = m.shader != null ? m.shader.GetInstanceID() : 0;
            if (t.ShaderId != shaderId)
            {
                t.ShaderId = shaderId;
                t.Layout = LayoutOf(m.shader);
                t.Sent = new Vector4[t.Layout.Names.Length];
                t.LayoutSent = false;
            }
            return id;
        }

        public static void BeginHostFrame(uint frame) { hostFrame = frame; }

        /// <summary>A new client knows nothing; resend layouts and values.</summary>
        public static void ResetHost()
        {
            foreach (HostMat t in hostMats.Values) { t.LayoutSent = false; t.FullSends = 0; }
        }

        /// <summary>The client loaded a scene and rebuilt its material copies: resend values.</summary>
        public static void ResendValues()
        {
            foreach (HostMat t in hostMats.Values) t.FullSends = 3;
        }

        private static readonly NetWriter chunk = new NetWriter(1400);
        private static readonly NetWriter entry = new NetWriter(256);

        /// <summary>Host: send the parameters of every material used this frame that changed (or is due a refresh).</summary>
        public static void Flush(Connection connection, ushort serial, uint frame)
        {
            int max = connection.MaxUnreliablePayload;
            chunk.Reset();
            chunk.U16(serial);
            chunk.U32(frame);
            int header = chunk.Length;
            foreach (HostMat t in hostMats.Values)
            {
                if (t.SeenFrame != frame || t.Mat == null || t.Layout.Names.Length == 0) continue;
                if (!t.LayoutSent)
                {
                    SendLayout(connection, t);
                    t.LayoutSent = true;
                    t.FullSends = 3;
                }
                bool full = t.FullSends > 0 || (frame + (uint)t.Id) % 60 == 0;
                if (t.FullSends > 0) t.FullSends--;
                entry.Reset();
                int count = 0;
                string[] names = t.Layout.Names;
                for (int i = 0; i < names.Length; i++)
                {
                    Vector4 v = t.Layout.IsVector[i] ? t.Mat.GetVector(names[i]) : new Vector4(t.Mat.GetFloat(names[i]), 0f, 0f, 0f);
                    if (!full && v == t.Sent[i]) continue;
                    t.Sent[i] = v;
                    entry.VarU((uint)i);
                    entry.F32(v.x);
                    if (t.Layout.IsVector[i]) { entry.F32(v.y); entry.F32(v.z); entry.F32(v.w); }
                    count++;
                }
                if (count == 0) continue;
                if (chunk.Length + entry.Length + 8 > max && chunk.Length > header)
                {
                    connection.SendUnreliable(MsgType.MaterialProps, chunk);
                    chunk.Length = header;
                }
                chunk.VarU((uint)t.Id);
                chunk.VarU((uint)count);
                chunk.Bytes(entry.Buffer, 0, entry.Length);
            }
            if (chunk.Length > header) connection.SendUnreliable(MsgType.MaterialProps, chunk);
        }

        private static void SendLayout(Connection connection, HostMat t)
        {
            NetWriter w = new NetWriter(256);
            w.VarU((uint)t.Id);
            w.VarU((uint)t.Layout.Names.Length);
            for (int i = 0; i < t.Layout.Names.Length; i++)
            {
                w.Str(t.Layout.Names[i]);
                w.Bool(t.Layout.IsVector[i]);
            }
            connection.SendReliable(MsgType.MaterialLayout, w);
        }

        public static string HostSummary()
        {
            int used = 0, props = 0;
            foreach (HostMat t in hostMats.Values)
            {
                if (t.SeenFrame != hostFrame) continue;
                used++;
                props += t.Layout.Names.Length;
            }
            return "materials=" + used + " props=" + props;
        }

        /// <summary>Debug: declared shader properties a renderer's MaterialPropertyBlock overrides with a non-zero value (Animator-driven).</summary>
        public static string DescribeBlock(Renderer r, MaterialPropertyBlock block)
        {
            Material m = r.sharedMaterial;
            if (m == null) return "";
            r.GetPropertyBlock(block);
            if (block.isEmpty) return "";
            Layout layout = LayoutOf(m.shader);
            System.Text.StringBuilder sb = null;
            for (int i = 0; i < layout.Names.Length; i++)
            {
                Vector4 v = layout.IsVector[i] ? block.GetVector(layout.Names[i]) : new Vector4(block.GetFloat(layout.Names[i]), 0f, 0f, 0f);
                if (v == Vector4.zero) continue;
                if (sb == null) sb = new System.Text.StringBuilder();
                sb.Append(layout.Names[i]).Append('=').Append(v.ToString("0.###")).Append(' ');
            }
            return sb != null ? sb.ToString() : "";
        }

        // ------------------------------------------------------------------ client

        private sealed class ClientMat
        {
            public Layout Layout;
            public Vector4[] Values;
            public bool[] Has;
            public bool[] Overridden;
            public uint LastFrame;
            public bool Dirty;
            public Material Base;
            public Material Clone;
            public bool UseClone;
        }

        private static readonly Dictionary<int, ClientMat> clientMats = new Dictionary<int, ClientMat>();

        /// <summary>Client: material ids whose resolved material changed (base -> clone); renderers using them must rebind.</summary>
        public static readonly HashSet<int> Rebind = new HashSet<int>();

        public static void ClearClient()
        {
            foreach (ClientMat cm in clientMats.Values) if (cm.Clone != null) UnityEngine.Object.Destroy(cm.Clone);
            clientMats.Clear();
            Rebind.Clear();
        }

        /// <summary>Client: a new scene loaded its own copies of the materials; clones are rebuilt from them.</summary>
        public static void OnClientSceneChanged()
        {
            foreach (ClientMat cm in clientMats.Values)
            {
                if (cm.Clone != null) UnityEngine.Object.Destroy(cm.Clone);
                cm.Clone = null;
                cm.Base = null;
                cm.UseClone = false;
                cm.Dirty = true;
                if (cm.Overridden != null) Array.Clear(cm.Overridden, 0, cm.Overridden.Length);
            }
        }

        public static void OnLayout(NetReader r)
        {
            int id = (int)r.VarU();
            int n = (int)r.VarU();
            Layout layout = new Layout { Names = new string[n], IsVector = new bool[n] };
            for (int i = 0; i < n; i++)
            {
                layout.Names[i] = r.Str();
                layout.IsVector[i] = r.Bool();
            }
            ClientMat cm;
            if (clientMats.TryGetValue(id, out cm) && cm.Clone != null) UnityEngine.Object.Destroy(cm.Clone);
            clientMats[id] = new ClientMat { Layout = layout, Values = new Vector4[n], Has = new bool[n], Overridden = new bool[n], Dirty = true };
        }

        public static void OnProps(NetReader r)
        {
            r.U16(); // scene serial; material ids are session-wide
            uint frame = r.U32();
            while (r.Remaining > 0)
            {
                int id = (int)r.VarU();
                int count = (int)r.VarU();
                ClientMat cm;
                clientMats.TryGetValue(id, out cm);
                if (cm == null)
                {
                    // Layout not here yet: values cannot be decoded (vector or float); drop the rest of this chunk.
                    return;
                }
                bool stale = frame < cm.LastFrame;
                for (int k = 0; k < count; k++)
                {
                    int i = (int)r.VarU();
                    if (i >= cm.Values.Length) return; // layout changed underneath; resync on the next refresh
                    Vector4 v = new Vector4(r.F32(), 0f, 0f, 0f);
                    if (cm.Layout.IsVector[i]) { v.y = r.F32(); v.z = r.F32(); v.w = r.F32(); }
                    if (stale) continue;
                    if (!cm.Has[i] || cm.Values[i] != v) cm.Dirty = true;
                    cm.Values[i] = v;
                    cm.Has[i] = true;
                }
                if (!stale) cm.LastFrame = frame;
            }
        }

        /// <summary>Client: the material to draw host material id with (a clone when the host changed its parameters).</summary>
        public static Material Resolve(int id)
        {
            Material b = ClientMirror.Materials.Get(id);
            if (b == null) return null;
            ClientMat cm;
            if (!clientMats.TryGetValue(id, out cm)) return b;
            if (cm.Base != b)
            {
                if (cm.Clone != null) UnityEngine.Object.Destroy(cm.Clone);
                cm.Clone = null;
                cm.Base = b;
                cm.UseClone = false;
                Array.Clear(cm.Overridden, 0, cm.Overridden.Length);
                cm.Dirty = true;
            }
            if (cm.Dirty) Refresh(id, cm);
            return cm.UseClone ? cm.Clone : b;
        }

        /// <summary>Client, every frame: apply received values to the clones and note ids that switched to a clone.</summary>
        public static void Update()
        {
            foreach (KeyValuePair<int, ClientMat> kv in clientMats)
            {
                ClientMat cm = kv.Value;
                if (cm.Dirty && cm.Base != null) Refresh(kv.Key, cm);
            }
        }

        private static void Refresh(int id, ClientMat cm)
        {
            cm.Dirty = false;
            Material b = cm.Base;
            if (b == null) return;
            string[] names = cm.Layout.Names;
            bool differs = cm.UseClone;
            for (int i = 0; i < names.Length && !differs; i++)
            {
                if (cm.Has[i] && cm.Values[i] != Read(b, names[i], cm.Layout.IsVector[i])) differs = true;
            }
            if (!differs) return;
            if (cm.Clone == null)
            {
                cm.Clone = new Material(b) { name = b.name };
                Rebind.Add(id);
            }
            cm.UseClone = true;
            for (int i = 0; i < names.Length; i++)
            {
                if (!cm.Has[i]) continue;
                // Once a property was overridden keep writing it, so a return to the asset value is applied too.
                if (!cm.Overridden[i] && cm.Values[i] == Read(b, names[i], cm.Layout.IsVector[i])) continue;
                cm.Overridden[i] = true;
                if (cm.Layout.IsVector[i]) cm.Clone.SetVector(names[i], cm.Values[i]);
                else cm.Clone.SetFloat(names[i], cm.Values[i].x);
            }
        }

        private static Vector4 Read(Material m, string name, bool vector)
        {
            return vector ? m.GetVector(name) : new Vector4(m.GetFloat(name), 0f, 0f, 0f);
        }

        public static string ClientSummary()
        {
            int clones = 0;
            System.Text.StringBuilder sample = null;
            foreach (KeyValuePair<int, ClientMat> kv in clientMats)
            {
                ClientMat cm = kv.Value;
                if (!cm.UseClone) continue;
                clones++;
                if (sample == null) sample = new System.Text.StringBuilder();
                if (sample.Length > 300) continue;
                sample.Append(' ').Append(cm.Base != null ? cm.Base.name : "?").Append('{');
                for (int i = 0; i < cm.Layout.Names.Length; i++)
                {
                    if (!cm.Overridden[i]) continue;
                    sample.Append(cm.Layout.Names[i]).Append('=');
                    Vector4 v = cm.Values[i];
                    sample.Append(cm.Layout.IsVector[i] ? v.ToString("0.##") : v.x.ToString("0.###")).Append(' ');
                }
                sample.Append('}');
            }
            return "matLayouts=" + clientMats.Count + " matClones=" + clones + (sample != null ? sample.ToString() : "");
        }
    }
}
