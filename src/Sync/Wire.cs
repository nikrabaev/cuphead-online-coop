using System;
using CupheadOnline.Net;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>Replicated state of one SpriteRenderer or SpriteMask. Fields are grouped; a bit mask says which groups follow.</summary>
    public struct SpriteState
    {
        public const uint PosXY = 1 << 0;
        public const uint PosZ = 1 << 1;
        public const uint Rot = 1 << 2;
        public const uint Scale = 1 << 3;
        public const uint SpriteId = 1 << 4;
        public const uint ColorBit = 1 << 5;
        public const uint Flags = 1 << 6;
        public const uint Sort = 1 << 7;
        public const uint Mat = 1 << 8;
        public const uint Size = 1 << 9;
        public const uint MaskBit = 1 << 10;
        public const uint All = (1 << 11) - 1;

        public const byte KindSprite = 0, KindMask = 1;

        public float X, Y, Z;
        public Vector3 Euler;
        public float SX, SY;
        public int Sprite;
        public Color32 Color;
        public byte Bits;          // 1 flipX, 2 flipY, 4..8 drawMode, 16..32 maskInteraction
        public int SortingLayer;
        public short SortingOrder;
        public byte Layer;
        public int Material;
        public float W, H;
        // SpriteMask
        public byte Kind;
        public float Cutoff;
        public bool CustomRange;
        public int FrontLayer, BackLayer;
        public short FrontOrder, BackOrder;

        private static SpriteState CaptureTransform(Renderer r)
        {
            Transform t = r.transform;
            Vector3 p = t.position;
            Vector3 s = t.lossyScale;
            SpriteState st = new SpriteState();
            st.X = p.x; st.Y = p.y; st.Z = p.z;
            st.Euler = t.eulerAngles;
            st.SX = s.x; st.SY = s.y;
            st.SortingLayer = r.sortingLayerID;
            st.SortingOrder = (short)Mathf.Clamp(r.sortingOrder, short.MinValue, short.MaxValue);
            st.Layer = (byte)r.gameObject.layer;
            return st;
        }

        public static SpriteState Capture(SpriteRenderer sr, HostAssetTable<Sprite> sprites, Func<Material, int> materialId)
        {
            SpriteState st = CaptureTransform(sr);
            st.Sprite = sprites.IdOf(sr.sprite);
            st.Color = sr.color;
            st.Bits = (byte)((sr.flipX ? 1 : 0) | (sr.flipY ? 2 : 0) | (((int)sr.drawMode & 3) << 2) | (((int)sr.maskInteraction & 3) << 4));
            st.Material = materialId(sr.sharedMaterial);
            if (sr.drawMode != SpriteDrawMode.Simple)
            {
                Vector2 size = sr.size;
                st.W = size.x; st.H = size.y;
            }
            return st;
        }

        /// <summary>A SpriteMask writes the stencil for sprites whose maskInteraction is set (DLC levels use them).</summary>
        public static SpriteState Capture(SpriteMask sm, HostAssetTable<Sprite> sprites)
        {
            SpriteState st = CaptureTransform(sm);
            st.Kind = KindMask;
            st.Sprite = sprites.IdOf(sm.sprite);
            st.Color = new Color32(255, 255, 255, 255);
            st.Cutoff = sm.alphaCutoff;
            st.CustomRange = sm.isCustomRangeActive;
            if (st.CustomRange)
            {
                st.FrontLayer = sm.frontSortingLayerID;
                st.FrontOrder = (short)Mathf.Clamp(sm.frontSortingOrder, short.MinValue, short.MaxValue);
                st.BackLayer = sm.backSortingLayerID;
                st.BackOrder = (short)Mathf.Clamp(sm.backSortingOrder, short.MinValue, short.MaxValue);
            }
            return st;
        }

        private static bool Near(float a, float b, float eps) { return Mathf.Abs(a - b) <= eps; }

        public uint Diff(ref SpriteState o)
        {
            uint m = 0;
            if (!Near(X, o.X, 0.01f) || !Near(Y, o.Y, 0.01f)) m |= PosXY;
            if (!Near(Z, o.Z, 0.01f)) m |= PosZ;
            if (!Near(Euler.x, o.Euler.x, 0.05f) || !Near(Euler.y, o.Euler.y, 0.05f) || !Near(Euler.z, o.Euler.z, 0.05f)) m |= Rot;
            if (!Near(SX, o.SX, 0.0005f) || !Near(SY, o.SY, 0.0005f)) m |= Scale;
            if (Sprite != o.Sprite) m |= SpriteId;
            if (Color.r != o.Color.r || Color.g != o.Color.g || Color.b != o.Color.b || Color.a != o.Color.a) m |= ColorBit;
            if (Bits != o.Bits) m |= Flags;
            if (SortingLayer != o.SortingLayer || SortingOrder != o.SortingOrder || Layer != o.Layer) m |= Sort;
            if (Material != o.Material) m |= Mat;
            if (!Near(W, o.W, 0.001f) || !Near(H, o.H, 0.001f)) m |= Size;
            if (Kind != o.Kind || !Near(Cutoff, o.Cutoff, 0.001f) || CustomRange != o.CustomRange || FrontLayer != o.FrontLayer || FrontOrder != o.FrontOrder || BackLayer != o.BackLayer || BackOrder != o.BackOrder) m |= MaskBit;
            return m;
        }

        /// <summary>Copy the field groups named by mask from o (what was just sent).</summary>
        public void Merge(ref SpriteState o, uint mask)
        {
            if ((mask & PosXY) != 0) { X = o.X; Y = o.Y; }
            if ((mask & PosZ) != 0) Z = o.Z;
            if ((mask & Rot) != 0) Euler = o.Euler;
            if ((mask & Scale) != 0) { SX = o.SX; SY = o.SY; }
            if ((mask & SpriteId) != 0) Sprite = o.Sprite;
            if ((mask & ColorBit) != 0) Color = o.Color;
            if ((mask & Flags) != 0) Bits = o.Bits;
            if ((mask & Sort) != 0) { SortingLayer = o.SortingLayer; SortingOrder = o.SortingOrder; Layer = o.Layer; }
            if ((mask & Mat) != 0) Material = o.Material;
            if ((mask & Size) != 0) { W = o.W; H = o.H; }
            if ((mask & MaskBit) != 0) { Kind = o.Kind; Cutoff = o.Cutoff; CustomRange = o.CustomRange; FrontLayer = o.FrontLayer; FrontOrder = o.FrontOrder; BackLayer = o.BackLayer; BackOrder = o.BackOrder; }
        }

        public void Write(NetWriter w, uint mask)
        {
            if ((mask & PosXY) != 0) { w.F32(X); w.F32(Y); }
            if ((mask & PosZ) != 0) w.F32(Z);
            if ((mask & Rot) != 0)
            {
                bool flat = Mathf.Abs(Mathf.DeltaAngle(Euler.x, 0f)) < 0.01f && Mathf.Abs(Mathf.DeltaAngle(Euler.y, 0f)) < 0.01f;
                w.Bool(!flat);
                if (!flat) { w.F32(Euler.x); w.F32(Euler.y); }
                w.F32(Euler.z);
            }
            if ((mask & Scale) != 0) { w.F32(SX); w.F32(SY); }
            if ((mask & SpriteId) != 0) w.VarU((uint)Sprite);
            if ((mask & ColorBit) != 0) { w.U8(Color.r); w.U8(Color.g); w.U8(Color.b); w.U8(Color.a); }
            if ((mask & Flags) != 0) w.U8(Bits);
            if ((mask & Sort) != 0) { w.I32(SortingLayer); w.I16(SortingOrder); w.U8(Layer); }
            if ((mask & Mat) != 0) w.VarU((uint)Material);
            if ((mask & Size) != 0) { w.F32(W); w.F32(H); }
            if ((mask & MaskBit) != 0)
            {
                w.U8(Kind);
                if (Kind == KindMask)
                {
                    w.F32(Cutoff);
                    w.Bool(CustomRange);
                    if (CustomRange) { w.I32(FrontLayer); w.I16(FrontOrder); w.I32(BackLayer); w.I16(BackOrder); }
                }
            }
        }

        public void Read(NetReader r, uint mask)
        {
            if ((mask & PosXY) != 0) { X = r.F32(); Y = r.F32(); }
            if ((mask & PosZ) != 0) Z = r.F32();
            if ((mask & Rot) != 0)
            {
                bool full = r.Bool();
                if (full) { Euler.x = r.F32(); Euler.y = r.F32(); }
                else { Euler.x = 0f; Euler.y = 0f; }
                Euler.z = r.F32();
            }
            if ((mask & Scale) != 0) { SX = r.F32(); SY = r.F32(); }
            if ((mask & SpriteId) != 0) Sprite = (int)r.VarU();
            if ((mask & ColorBit) != 0) { Color = new Color32(r.U8(), r.U8(), r.U8(), r.U8()); }
            if ((mask & Flags) != 0) Bits = r.U8();
            if ((mask & Sort) != 0) { SortingLayer = r.I32(); SortingOrder = r.I16(); Layer = r.U8(); }
            if ((mask & Mat) != 0) Material = (int)r.VarU();
            if ((mask & Size) != 0) { W = r.F32(); H = r.F32(); }
            if ((mask & MaskBit) != 0)
            {
                Kind = r.U8();
                Cutoff = 0f; CustomRange = false; FrontLayer = BackLayer = 0; FrontOrder = BackOrder = 0;
                if (Kind == KindMask)
                {
                    Cutoff = r.F32();
                    CustomRange = r.Bool();
                    if (CustomRange) { FrontLayer = r.I32(); FrontOrder = r.I16(); BackLayer = r.I32(); BackOrder = r.I16(); }
                }
            }
        }
    }

    /// <summary>Replicated state of one UGUI graphic (Image, Text, or TextMeshProUGUI), in letterbox-normalized screen space.</summary>
    public struct UiState
    {
        public const uint RectBit = 1 << 0;
        public const uint SpriteId = 1 << 1;
        public const uint ColorBit = 1 << 2;
        public const uint Fill = 1 << 3;
        public const uint TextBit = 1 << 4;
        public const uint Order = 1 << 5;
        public const uint KindBit = 1 << 6;
        public const uint Mat = 1 << 7;
        public const uint ClipBit = 1 << 8;
        public const uint All = (1 << 9) - 1;

        public const byte KindImage = 0, KindText = 1, KindTmp = 2, KindRawImage = 3;

        public byte Kind;
        public float CX, CY, W, H, Angle; // normalized to the letterbox rect; W/H unrotated size
        public int Sprite;
        public Color32 Color;
        // Image
        public byte ImageType, FillMethod, FillOrigin;
        public bool FillClockwise, PreserveAspect;
        public float FillAmount;
        // Text
        public string Text;
        public int Font;
        public float FontSize;    // normalized to letterbox height
        public byte Alignment, FontStyle;
        public float LineSpacing;
        public bool RichText;
        // ordering
        public short CanvasOrder;
        public int Depth;
        public int Material;
        // UGUI Mask / RectMask2D clipping, as an axis-aligned rect normalized to the letterbox like CX/CY
        public bool Clip;
        public float ClipX0, ClipY0, ClipX1, ClipY1;

        private static bool Near(float a, float b, float eps) { return Mathf.Abs(a - b) <= eps; }

        public uint Diff(ref UiState o)
        {
            uint m = 0;
            if (Kind != o.Kind) m |= KindBit;
            if (!Near(CX, o.CX, 0.0002f) || !Near(CY, o.CY, 0.0002f) || !Near(W, o.W, 0.0002f) || !Near(H, o.H, 0.0002f) || !Near(Angle, o.Angle, 0.05f)) m |= RectBit;
            if (Sprite != o.Sprite) m |= SpriteId;
            if (Color.r != o.Color.r || Color.g != o.Color.g || Color.b != o.Color.b || Color.a != o.Color.a) m |= ColorBit;
            if (ImageType != o.ImageType || FillMethod != o.FillMethod || FillOrigin != o.FillOrigin || FillClockwise != o.FillClockwise || PreserveAspect != o.PreserveAspect || !Near(FillAmount, o.FillAmount, 0.001f)) m |= Fill;
            if (Text != o.Text || Font != o.Font || !Near(FontSize, o.FontSize, 0.0001f) || Alignment != o.Alignment || FontStyle != o.FontStyle || !Near(LineSpacing, o.LineSpacing, 0.001f) || RichText != o.RichText) m |= TextBit;
            if (CanvasOrder != o.CanvasOrder || Depth != o.Depth) m |= Order;
            if (Material != o.Material) m |= Mat;
            if (Clip != o.Clip || (Clip && (!Near(ClipX0, o.ClipX0, 0.0002f) || !Near(ClipY0, o.ClipY0, 0.0002f) || !Near(ClipX1, o.ClipX1, 0.0002f) || !Near(ClipY1, o.ClipY1, 0.0002f)))) m |= ClipBit;
            return m;
        }

        public void Merge(ref UiState o, uint mask)
        {
            if ((mask & KindBit) != 0) Kind = o.Kind;
            if ((mask & RectBit) != 0) { CX = o.CX; CY = o.CY; W = o.W; H = o.H; Angle = o.Angle; }
            if ((mask & SpriteId) != 0) Sprite = o.Sprite;
            if ((mask & ColorBit) != 0) Color = o.Color;
            if ((mask & Fill) != 0) { ImageType = o.ImageType; FillMethod = o.FillMethod; FillOrigin = o.FillOrigin; FillClockwise = o.FillClockwise; PreserveAspect = o.PreserveAspect; FillAmount = o.FillAmount; }
            if ((mask & TextBit) != 0) { Text = o.Text; Font = o.Font; FontSize = o.FontSize; Alignment = o.Alignment; FontStyle = o.FontStyle; LineSpacing = o.LineSpacing; RichText = o.RichText; }
            if ((mask & Order) != 0) { CanvasOrder = o.CanvasOrder; Depth = o.Depth; }
            if ((mask & Mat) != 0) Material = o.Material;
            if ((mask & ClipBit) != 0) { Clip = o.Clip; ClipX0 = o.ClipX0; ClipY0 = o.ClipY0; ClipX1 = o.ClipX1; ClipY1 = o.ClipY1; }
        }

        public void Write(NetWriter w, uint mask)
        {
            if ((mask & KindBit) != 0) w.U8(Kind);
            if ((mask & RectBit) != 0) { w.F32(CX); w.F32(CY); w.F32(W); w.F32(H); w.F32(Angle); }
            if ((mask & SpriteId) != 0) w.VarU((uint)Sprite);
            if ((mask & ColorBit) != 0) { w.U8(Color.r); w.U8(Color.g); w.U8(Color.b); w.U8(Color.a); }
            if ((mask & Fill) != 0)
            {
                w.U8(ImageType); w.U8(FillMethod); w.U8(FillOrigin);
                w.U8((byte)((FillClockwise ? 1 : 0) | (PreserveAspect ? 2 : 0)));
                w.F32(FillAmount);
            }
            if ((mask & TextBit) != 0)
            {
                w.Str(Text); w.VarU((uint)Font); w.F32(FontSize); w.U8(Alignment); w.U8(FontStyle); w.F32(LineSpacing); w.Bool(RichText);
            }
            if ((mask & Order) != 0) { w.I16(CanvasOrder); w.I32(Depth); }
            if ((mask & Mat) != 0) w.VarU((uint)Material);
            if ((mask & ClipBit) != 0)
            {
                w.Bool(Clip);
                if (Clip) { w.F32(ClipX0); w.F32(ClipY0); w.F32(ClipX1); w.F32(ClipY1); }
            }
        }

        public void Read(NetReader r, uint mask)
        {
            if ((mask & KindBit) != 0) Kind = r.U8();
            if ((mask & RectBit) != 0) { CX = r.F32(); CY = r.F32(); W = r.F32(); H = r.F32(); Angle = r.F32(); }
            if ((mask & SpriteId) != 0) Sprite = (int)r.VarU();
            if ((mask & ColorBit) != 0) Color = new Color32(r.U8(), r.U8(), r.U8(), r.U8());
            if ((mask & Fill) != 0)
            {
                ImageType = r.U8(); FillMethod = r.U8(); FillOrigin = r.U8();
                byte b = r.U8();
                FillClockwise = (b & 1) != 0; PreserveAspect = (b & 2) != 0;
                FillAmount = r.F32();
            }
            if ((mask & TextBit) != 0)
            {
                Text = r.Str(); Font = (int)r.VarU(); FontSize = r.F32(); Alignment = r.U8(); FontStyle = r.U8(); LineSpacing = r.F32(); RichText = r.Bool();
            }
            if ((mask & Order) != 0) { CanvasOrder = r.I16(); Depth = r.I32(); }
            if ((mask & Mat) != 0) Material = (int)r.VarU();
            if ((mask & ClipBit) != 0)
            {
                Clip = r.Bool();
                if (Clip) { ClipX0 = r.F32(); ClipY0 = r.F32(); ClipX1 = r.F32(); ClipY1 = r.F32(); }
                else { ClipX0 = ClipY0 = ClipX1 = ClipY1 = 0f; }
            }
        }
    }

    public static class Letterbox
    {
        /// <summary>Same formula as AbstractCupheadCamera.UpdateRect (decompiled): 16:9 game area centered with overscan.</summary>
        public static Rect Viewport()
        {
            float aspect = (float)Screen.width / Screen.height;
            float overscan = 0f;
            try { overscan = SettingsData.Data.overscan; } catch { }
            float s = 1f - 0.1f * overscan;
            if (aspect > 1.7777778f) return RectFromCenter(0.5f, 0.5f, s * 1.7777778f / aspect, s);
            return RectFromCenter(0.5f, 0.5f, s, s * aspect / 1.7777778f);
        }

        public static Rect Pixels()
        {
            Rect v = Viewport();
            return new Rect(v.x * Screen.width, v.y * Screen.height, v.width * Screen.width, v.height * Screen.height);
        }

        private static Rect RectFromCenter(float cx, float cy, float w, float h)
        {
            return new Rect(cx - w / 2f, cy - h / 2f, w, h);
        }
    }
}
