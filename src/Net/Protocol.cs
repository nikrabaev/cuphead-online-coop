using System;
using System.Text;

namespace CupheadOnline.Net
{
    public enum MsgType : byte
    {
        Hello = 1,        // client -> host  (reliable)  protocol version, player name, game version
        Welcome = 2,      // host -> client  (reliable)  host name
        Reject = 3,       // host -> client  (reliable)  reason
        Bye = 4,          // either          (unreliable, sent several times)
        Ping = 5,         // either          (unreliable) timestamp
        Pong = 6,         // either          (unreliable) echoed timestamp
        Input = 10,       // client -> host  (unreliable) remote controller state
        SceneReady = 11,  // client -> host  (reliable)  client finished loading scene serial N
        Scene = 20,       // host -> client  (reliable)  scene to mirror
        SpriteDef = 21,   // host -> client  (reliable)  sprite id -> name/texture
        MaterialDef = 22, // host -> client  (reliable)  material id -> name/shader
        Snapshot = 23,    // host -> client  (unreliable) renderer states chunk
        Despawn = 24,     // host -> client  (reliable)  renderer ids gone
        Camera = 25,      // host -> client  (unreliable) camera transforms
        Audio = 26,       // host -> client  (reliable)  AudioManager / PauseManager call replay
        FontDef = 27,     // host -> client  (reliable)  font id -> name (UGUI Font or TMP font asset)
        UiSnapshot = 28,  // host -> client  (unreliable) UGUI graphic states chunk
        UiDespawn = 29,   // host -> client  (reliable)  UGUI graphic ids gone
        CameraDef = 30,   // host -> client  (reliable)  camera id -> static camera settings
        MaterialLayout = 31, // host -> client (reliable) material id -> names of its shader's properties
        MaterialProps = 32,  // host -> client (unreliable) material property values chunk
    }

    public static class Protocol
    {
        public const byte Magic0 = 0xC0;
        public const byte Magic1 = 0x9E;
        public const byte Version = 3;
        public const int HeaderSize = 9; // magic(2) version(1) ack(2) sack(4)
        public const int DefaultPort = 27960;

        public static bool IsHello(byte[] buffer, int length)
        {
            return length >= HeaderSize && buffer[0] == Magic0 && buffer[1] == Magic1 && buffer[2] == Version;
        }

        /// <summary>
        /// A datagram that opens a new connection: its first message is the reliable Hello with sequence 0 (a new
        /// Connection queues Hello before anything else, and reliables are written ahead of unreliables). Hosts adopt
        /// a peer only on this, so stray packets from a peer's previous, dead connection can't claim the slot.
        /// </summary>
        public static bool IsConnectRequest(byte[] buffer, int length)
        {
            return IsHello(buffer, length) && length >= HeaderSize + 4 && buffer[HeaderSize] == 1 &&
                buffer[HeaderSize + 1] == 0 && buffer[HeaderSize + 2] == 0 && buffer[HeaderSize + 3] == (byte)MsgType.Hello;
        }
    }

    public sealed class NetWriter
    {
        public byte[] Buffer;
        public int Length;

        public NetWriter(int capacity = 256)
        {
            Buffer = new byte[capacity];
        }

        public void Reset() { Length = 0; }

        private void Ensure(int extra)
        {
            if (Length + extra <= Buffer.Length) return;
            int size = Buffer.Length * 2;
            while (size < Length + extra) size *= 2;
            Array.Resize(ref Buffer, size);
        }

        public void U8(byte v) { Ensure(1); Buffer[Length++] = v; }
        public void Bool(bool v) { U8(v ? (byte)1 : (byte)0); }
        public void I8(sbyte v) { U8((byte)v); }

        public void U16(ushort v)
        {
            Ensure(2);
            Buffer[Length++] = (byte)v;
            Buffer[Length++] = (byte)(v >> 8);
        }

        public void I16(short v) { U16((ushort)v); }

        public void I32(int v)
        {
            Ensure(4);
            Buffer[Length++] = (byte)v;
            Buffer[Length++] = (byte)(v >> 8);
            Buffer[Length++] = (byte)(v >> 16);
            Buffer[Length++] = (byte)(v >> 24);
        }

        public void U32(uint v) { I32((int)v); }

        public void F32(float v)
        {
            I32(BitConverter.ToInt32(BitConverter.GetBytes(v), 0));
        }

        public void VarU(uint v)
        {
            while (v >= 0x80)
            {
                U8((byte)(v | 0x80));
                v >>= 7;
            }
            U8((byte)v);
        }

        public void Str(string s)
        {
            if (s == null) s = string.Empty;
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            VarU((uint)bytes.Length);
            Bytes(bytes, 0, bytes.Length);
        }

        public void Bytes(byte[] src, int offset, int count)
        {
            Ensure(count);
            System.Buffer.BlockCopy(src, offset, Buffer, Length, count);
            Length += count;
        }

        public byte[] ToArray()
        {
            byte[] copy = new byte[Length];
            System.Buffer.BlockCopy(Buffer, 0, copy, 0, Length);
            return copy;
        }
    }

    public sealed class NetReader
    {
        private byte[] buffer;
        private int position;
        private int end;

        public NetReader(byte[] buffer, int offset, int length)
        {
            Reset(buffer, offset, length);
        }

        public void Reset(byte[] buffer, int offset, int length)
        {
            this.buffer = buffer;
            position = offset;
            end = offset + length;
        }

        public int Remaining { get { return end - position; } }
        public int Position { get { return position; } }

        private void Need(int n)
        {
            if (position + n > end) throw new FormatException("Truncated network message");
        }

        public byte U8() { Need(1); return buffer[position++]; }
        public bool Bool() { return U8() != 0; }
        public sbyte I8() { return (sbyte)U8(); }

        public ushort U16()
        {
            Need(2);
            ushort v = (ushort)(buffer[position] | (buffer[position + 1] << 8));
            position += 2;
            return v;
        }

        public short I16() { return (short)U16(); }

        public int I32()
        {
            Need(4);
            int v = buffer[position] | (buffer[position + 1] << 8) | (buffer[position + 2] << 16) | (buffer[position + 3] << 24);
            position += 4;
            return v;
        }

        public uint U32() { return (uint)I32(); }

        public float F32()
        {
            Need(4);
            float v = BitConverter.ToSingle(buffer, position);
            position += 4;
            return v;
        }

        public uint VarU()
        {
            uint result = 0;
            int shift = 0;
            while (true)
            {
                byte b = U8();
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
                if (shift > 28) throw new FormatException("Bad varint");
            }
        }

        public string Str()
        {
            int n = (int)VarU();
            Need(n);
            string s = Encoding.UTF8.GetString(buffer, position, n);
            position += n;
            return s;
        }

        public void Skip(int n) { Need(n); position += n; }
    }
}
