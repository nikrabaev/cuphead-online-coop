using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace CupheadOnline.Net
{
    public delegate void MessageHandler(MsgType type, NetReader reader);

    /// <summary>
    /// Message layer over an unreliable datagram transport.
    /// Datagram: magic(2) version(1) ack(u16 = next reliable seq expected) sack(u32: bit i = seq ack+1+i already
    /// buffered out of order) then messages:
    ///   flags(u8: bit0 reliable) [seq u16 if reliable] type(u8) length(u16) payload
    /// Reliable messages are delivered in order exactly once; unreliable ones best-effort.
    /// </summary>
    public sealed class Connection : IDisposable
    {
        public const int MaxReliablePayload = 1000;
        private const int MessageOverhead = 6;
        private const double TimeoutSeconds = 12.0;

        private sealed class Pending
        {
            public ushort Seq;
            public MsgType Type;
            public byte[] Payload;
            public double LastSent = -1;
            public double Interval; // per-message resend backoff
        }

        private struct Queued
        {
            public MsgType Type;
            public byte[] Payload;
            public int Length;
        }

        private readonly IDatagramTransport transport;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly LinkedList<Pending> unacked = new LinkedList<Pending>();
        private readonly List<Queued> unreliable = new List<Queued>();
        private readonly Dictionary<ushort, KeyValuePair<MsgType, byte[]>> outOfOrder = new Dictionary<ushort, KeyValuePair<MsgType, byte[]>>();
        private readonly NetWriter datagram = new NetWriter(2048);
        private readonly NetReader reader = new NetReader(new byte[0], 0, 0);
        private ushort nextSendSeq;
        private ushort nextRecvSeq;
        private double lastReceive;
        private double lastSend;
        private bool disposed;

        public event MessageHandler OnMessage;

        public double Now { get { return clock.Elapsed.TotalSeconds; } }
        public double RttSeconds { get; private set; }
        public bool TimedOut { get { return transport.HasPeer && lastReceive > 0 && Now - lastReceive > TimeoutSeconds; } }
        public bool HasReceived { get { return lastReceive > 0; } }
        /// <summary>Test hook: drop all traffic in both directions until this time (DateTime.UtcNow ticks).</summary>
        public static long TestDropUntil;
        private static bool Dropping { get { return TestDropUntil != 0 && DateTime.UtcNow.Ticks < TestDropUntil; } }
        public IDatagramTransport Transport { get { return transport; } }
        public long BytesSent { get; private set; }
        public long BytesReceived { get; private set; }

        public Connection(IDatagramTransport transport)
        {
            this.transport = transport;
            RttSeconds = 0.1;
        }

        public void SendReliable(MsgType type, NetWriter payload)
        {
            if (payload.Length > MaxReliablePayload) throw new ArgumentException("Reliable payload too large: " + payload.Length);
            unacked.AddLast(new Pending { Seq = nextSendSeq++, Type = type, Payload = payload.ToArray() });
        }

        /// <summary>Queues an unreliable message for the next Flush. Payload bytes are copied.</summary>
        public void SendUnreliable(MsgType type, NetWriter payload)
        {
            unreliable.Add(new Queued { Type = type, Payload = payload.ToArray(), Length = payload.Length });
        }

        public int MaxUnreliablePayload { get { return transport.MaxDatagramSize - Protocol.HeaderSize - MessageOverhead; } }

        public int PendingReliableCount { get { return unacked.Count; } }

        public void Receive()
        {
            byte[] data;
            int length;
            while (!disposed && transport.TryReceive(out data, out length))
            {
                BytesReceived += length;
                if (Dropping || !Protocol.IsHello(data, length)) continue;
                reader.Reset(data, 3, length - 3);
                try
                {
                    ushort ack = reader.U16();
                    uint sack = reader.U32();
                    // Acks for messages never sent come from a previous connection to the same peer; ignore those.
                    if ((short)(ack - nextSendSeq) > 0) continue;
                    lastReceive = Now;
                    ProcessAck(ack, sack);
                    while (reader.Remaining > 0 && !disposed)
                    {
                        byte flags = reader.U8();
                        bool reliable = (flags & 1) != 0;
                        ushort seq = reliable ? reader.U16() : (ushort)0;
                        MsgType type = (MsgType)reader.U8();
                        int len = reader.U16();
                        if (len > reader.Remaining) throw new FormatException("message length " + len + " exceeds datagram");
                        int start = reader.Position;
                        if (reliable)
                        {
                            HandleReliable(seq, type, data, start, len);
                        }
                        else
                        {
                            Dispatch(type, data, start, len);
                        }
                        reader.Reset(data, start + len, length - (start + len));
                    }
                }
                catch (FormatException ex)
                {
                    Plugin.Log.LogWarning("Dropping malformed packet: " + ex.Message);
                }
            }
        }

        private void ProcessAck(ushort ack, uint sack)
        {
            // Everything with seq < ack (mod 2^16) was received in order; sack marks later ones already buffered.
            LinkedListNode<Pending> node = unacked.First;
            while (node != null)
            {
                LinkedListNode<Pending> next = node.Next;
                short delta = (short)(node.Value.Seq - ack);
                if (delta < 0 || (delta >= 1 && delta <= 32 && (sack & (1u << (delta - 1))) != 0)) unacked.Remove(node);
                node = next;
            }
        }

        private uint SelectiveAck()
        {
            uint bits = 0;
            if (outOfOrder.Count == 0) return 0;
            for (int i = 0; i < 32; i++)
            {
                if (outOfOrder.ContainsKey((ushort)(nextRecvSeq + 1 + i))) bits |= 1u << i;
            }
            return bits;
        }

        private void HandleReliable(ushort seq, MsgType type, byte[] data, int start, int len)
        {
            short delta = (short)(seq - nextRecvSeq);
            if (delta < 0)
            {
                // Duplicate. A repeated Hello still goes up: the session tells a retransmit from a peer that lost
                // us and redialled (a new connection nonce).
                if (seq == 0 && type == MsgType.Hello) Dispatch(type, data, start, len);
                return;
            }
            if (delta > 0)
            {
                if (delta > 2048) return; // nonsense; never buffer unboundedly
                if (!outOfOrder.ContainsKey(seq))
                {
                    byte[] copy = new byte[len];
                    Buffer.BlockCopy(data, start, copy, 0, len);
                    outOfOrder[seq] = new KeyValuePair<MsgType, byte[]>(type, copy);
                }
                return;
            }
            nextRecvSeq++;
            Dispatch(type, data, start, len);
            KeyValuePair<MsgType, byte[]> buffered;
            while (!disposed && outOfOrder.TryGetValue(nextRecvSeq, out buffered))
            {
                outOfOrder.Remove(nextRecvSeq);
                nextRecvSeq++;
                Dispatch(buffered.Key, buffered.Value, 0, buffered.Value.Length);
            }
        }

        private readonly NetReader dispatchReader = new NetReader(new byte[0], 0, 0);

        private void Dispatch(MsgType type, byte[] data, int start, int len)
        {
            if (type == MsgType.Pong && len >= 8)
            {
                dispatchReader.Reset(data, start, len);
                double sent = BitConverter.ToDouble(data, start);
                double sample = Now - sent;
                if (sample >= 0 && sample < 5) RttSeconds = RttSeconds * 0.85 + sample * 0.15;
                return;
            }
            if (type == MsgType.Ping && len >= 8)
            {
                NetWriter pong = new NetWriter(8);
                pong.Bytes(data, start, 8);
                SendUnreliable(MsgType.Pong, pong);
                return;
            }
            MessageHandler handler = OnMessage;
            if (handler == null) return;
            dispatchReader.Reset(data, start, len);
            try
            {
                handler(type, dispatchReader);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Error handling " + type + ": " + ex);
            }
        }

        private double lastPing;

        /// <summary>Sends queued messages, resends unacked reliables, and keeps the link alive.</summary>
        public void Flush()
        {
            if (!transport.HasPeer) return;
            double now = Now;
            if (now - lastPing > 1.0)
            {
                lastPing = now;
                NetWriter ping = new NetWriter(8);
                ping.Bytes(BitConverter.GetBytes(now), 0, 8);
                SendUnreliable(MsgType.Ping, ping);
            }

            double resendAfter = Math.Max(0.05, RttSeconds * 1.5);
            int max = transport.MaxDatagramSize;
            BeginDatagram();
            int byteBudget = 32 * 1024; // cap reliable bytes per flush (first sends and resends)
            foreach (Pending p in unacked)
            {
                if (p.LastSent >= 0 && now - p.LastSent < p.Interval) continue;
                int size = MessageOverhead + p.Payload.Length;
                if (byteBudget < size) break;
                byteBudget -= size;
                // First resend after ~1.5 RTT, then back off exponentially (cap 1 s).
                p.Interval = p.LastSent < 0 ? resendAfter : Math.Min(1.0, Math.Max(resendAfter, p.Interval * 2));
                if (datagram.Length + size > max) { EmitDatagram(); BeginDatagram(); }
                datagram.U8(1);
                datagram.U16(p.Seq);
                datagram.U8((byte)p.Type);
                datagram.U16((ushort)p.Payload.Length);
                datagram.Bytes(p.Payload, 0, p.Payload.Length);
                p.LastSent = now;
            }
            for (int i = 0; i < unreliable.Count; i++)
            {
                Queued q = unreliable[i];
                int size = MessageOverhead - 2 + q.Length;
                if (Protocol.HeaderSize + size > max) continue; // caller must chunk; drop oversize
                if (datagram.Length + size > max) { EmitDatagram(); BeginDatagram(); }
                datagram.U8(0);
                datagram.U8((byte)q.Type);
                datagram.U16((ushort)q.Length);
                datagram.Bytes(q.Payload, 0, q.Length);
            }
            unreliable.Clear();
            if (datagram.Length > Protocol.HeaderSize || now - lastSend > 0.1)
            {
                EmitDatagram();
            }
        }

        private void BeginDatagram()
        {
            datagram.Reset();
            datagram.U8(Protocol.Magic0);
            datagram.U8(Protocol.Magic1);
            datagram.U8(Protocol.Version);
            datagram.U16(nextRecvSeq);
            datagram.U32(SelectiveAck());
        }

        private void EmitDatagram()
        {
            if (!Dropping) transport.Send(datagram.Buffer, datagram.Length);
            BytesSent += datagram.Length;
            lastSend = Now;
        }

        public void SendBye()
        {
            for (int i = 0; i < 3; i++)
            {
                SendUnreliable(MsgType.Bye, new NetWriter(1));
                Flush();
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            transport.Dispose();
        }
    }
}
