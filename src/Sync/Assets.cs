using System;
using System.Collections.Generic;
using CupheadOnline.Net;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>
    /// Instance ids differ between processes, so assets are identified by a stable string key.
    /// Both machines load the same scene through SceneLoader, so the same Sprites/Materials/Fonts are in memory.
    /// </summary>
    public static class AssetKeys
    {
        public static string Sprite(Sprite s)
        {
            Rect r = s.rect;
            Vector2 p = s.pivot;
            return s.name + "|" + Mathf.RoundToInt(r.width) + "x" + Mathf.RoundToInt(r.height) + "|" + Mathf.RoundToInt(p.x) + "," + Mathf.RoundToInt(p.y);
        }

        /// <summary>Host key: identity plus "#AtlasTag" when the sprite lives in a late-bound SpriteAtlas.</summary>
        public static string SpriteWithAtlas(Sprite s)
        {
            string key = Sprite(s);
            string atlas = AtlasTag(s);
            return atlas != null ? key + "#" + atlas : key;
        }

        /// <summary>Packed atlas textures are named "sactx-WxH-FORMAT-AtlasName-hash" (observed at runtime).</summary>
        public static string AtlasTag(Sprite s)
        {
            Texture2D tex = s.texture;
            if (tex == null || !s.packed) return null;
            string n = tex.name;
            if (!n.StartsWith("sactx-", StringComparison.Ordinal)) return null;
            string[] parts = n.Split('-');
            if (parts.Length < 5) return null;
            return string.Join("-", parts, 3, parts.Length - 4);
        }

        public static string Material(Material m)
        {
            string name = m.name;
            if (name.EndsWith(" (Instance)", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 11);
            return name + "|" + (m.shader != null ? m.shader.name : "");
        }

        public static string Font(UnityEngine.Object f)
        {
            return f.GetType().Name + "|" + f.name;
        }
    }

    /// <summary>Host side: hands out small integer ids and emits definitions the first time an id is used.</summary>
    public sealed class HostAssetTable<T> where T : UnityEngine.Object
    {
        private readonly Dictionary<int, int> idByInstance = new Dictionary<int, int>();
        private readonly List<string> keys = new List<string> { null }; // id 0 = none
        private readonly List<bool> sent = new List<bool> { true };
        private readonly List<int> pending = new List<int>();
        private readonly Func<T, string> keyOf;
        private readonly MsgType defType;

        public HostAssetTable(Func<T, string> keyOf, MsgType defType)
        {
            this.keyOf = keyOf;
            this.defType = defType;
        }

        public int IdOf(T asset)
        {
            if (asset == null) return 0;
            int instance = asset.GetInstanceID();
            int id;
            if (!idByInstance.TryGetValue(instance, out id))
            {
                id = keys.Count;
                keys.Add(keyOf(asset));
                sent.Add(false);
                idByInstance[instance] = id;
            }
            if (!sent[id])
            {
                sent[id] = true;
                pending.Add(id);
            }
            return id;
        }

        /// <summary>Forget what the client knows (new client joined).</summary>
        public void ResetSent()
        {
            for (int i = 1; i < sent.Count; i++) sent[i] = false;
            pending.Clear();
        }

        /// <summary>Must be called before the snapshot that references new ids (reliable data arrives in order, but snapshots are unreliable — the client re-resolves late arrivals).</summary>
        public void FlushDefs(Connection connection)
        {
            if (pending.Count == 0) return;
            try
            {
                NetWriter w = new NetWriter(1024);
                NetWriter entry = new NetWriter(256);
                int count = 0;
                w.U16(0);
                for (int i = 0; i < pending.Count; i++)
                {
                    int id = pending[i];
                    entry.Reset();
                    entry.VarU((uint)id);
                    string key = keys[id];
                    if (key.Length > 300) key = key.Substring(0, 300); // keys are names; never let one break the batch
                    entry.Str(key);
                    if (count > 0 && w.Length + entry.Length > 900)
                    {
                        Send(connection, w, count);
                        w.Reset();
                        w.U16(0);
                        count = 0;
                    }
                    w.Bytes(entry.Buffer, 0, entry.Length);
                    count++;
                }
                if (count > 0) Send(connection, w, count);
            }
            finally
            {
                pending.Clear();
            }
        }

        private void Send(Connection connection, NetWriter w, int count)
        {
            w.Buffer[0] = (byte)count;
            w.Buffer[1] = (byte)(count >> 8);
            connection.SendReliable(defType, w);
        }
    }

    /// <summary>Client side: id -> key from definitions, key -> loaded asset via an index of everything in memory.</summary>
    public sealed class ClientAssetTable<T> where T : UnityEngine.Object
    {
        private readonly Dictionary<int, string> keyById = new Dictionary<int, string>();
        private readonly Dictionary<int, T> resolved = new Dictionary<int, T>();
        private Dictionary<string, T> index;
        private readonly Func<T, string> keyOf;
        private readonly Func<string, T> fallback;
        /// <summary>Optional: an indexed asset that exists but cannot be drawn yet (unbound atlas sprite).</summary>
        public Func<T, bool> Usable;
        private float lastIndexBuild = -100f;
        private float rebuildInterval = 0.5f;
        private bool missedSinceIndex;
        public int Version { get; private set; }

        public ClientAssetTable(Func<T, string> keyOf, Func<string, T> fallback = null)
        {
            this.keyOf = keyOf;
            this.fallback = fallback;
        }

        public void ReadDefs(NetReader r)
        {
            int count = r.U16();
            for (int i = 0; i < count; i++)
            {
                int id = (int)r.VarU();
                keyById[id] = r.Str();
                resolved.Remove(id);
            }
            rebuildInterval = 0.5f; // new names may refer to freshly loaded assets
            Version++;
        }

        public void ClearAll()
        {
            keyById.Clear();
            resolved.Clear();
            index = null;
            Version++;
        }

        /// <summary>Scene changed: loaded assets changed, rebuild lazily.</summary>
        public void Invalidate()
        {
            resolved.Clear();
            index = null;
            rebuildInterval = 0.5f;
            Version++;
        }

        /// <summary>True when some id resolved to an asset that is not drawable yet (retry later).</summary>
        public bool NeedsRetry(T asset)
        {
            return asset == null || (Usable != null && !Usable(asset));
        }

        public string KeyOf(int id)
        {
            string key;
            return keyById.TryGetValue(id, out key) ? key : "?" + id;
        }

        public bool Knows(int id) { return id == 0 || keyById.ContainsKey(id); }

        public T Get(int id)
        {
            if (id == 0) return null;
            T asset;
            if (resolved.TryGetValue(id, out asset) && asset != null && (Usable == null || Usable(asset))) return asset;
            string key;
            if (!keyById.TryGetValue(id, out key)) return null;
            string lookup = key;
            int hash = key.IndexOf('#');
            if (hash >= 0) lookup = key.Substring(0, hash);
            if (index == null || (missedSinceIndex && Time.unscaledTime - lastIndexBuild > rebuildInterval))
            {
                if (index != null) rebuildInterval = Mathf.Min(8f, rebuildInterval * 2f); // repeated misses: back off
                BuildIndex();
            }
            T indexed;
            index.TryGetValue(lookup, out indexed);
            if (indexed != null && (Usable == null || Usable(indexed)))
            {
                resolved[id] = indexed;
                return indexed;
            }
            missedSinceIndex = indexed == null;
            asset = fallback != null ? fallback(key) : null;
            if (asset != null)
            {
                resolved[id] = asset;
                return asset;
            }
            return indexed; // may still bind later; not cached so we retry
        }

        private void BuildIndex()
        {
            index = new Dictionary<string, T>();
            T[] all = Resources.FindObjectsOfTypeAll<T>();
            for (int i = 0; i < all.Length; i++)
            {
                T a = all[i];
                if (a == null) continue;
                string k;
                try { k = keyOf(a); }
                catch { continue; }
                if (!index.ContainsKey(k)) index[k] = a;
            }
            lastIndexBuild = Time.unscaledTime;
            missedSinceIndex = false;
        }
    }
}
