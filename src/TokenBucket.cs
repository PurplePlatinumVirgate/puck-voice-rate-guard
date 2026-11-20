using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace VoiceRateGuard
{
    // Monotonic seconds. Keeps its precision over a long server uptime.
    internal static class Clock
    {
        static readonly double SecondsPerTick = 1.0 / Stopwatch.Frequency;

        internal static double Now => Stopwatch.GetTimestamp() * SecondsPerTick;
    }

    // A packet budget and a byte budget that refill over time. Starts full.
    internal sealed class TokenBucket
    {
        readonly double packetRate, packetBurst, byteRate, byteBurst;
        double packets, bytes, last;

        internal double LastUse;

        // The last packet from this player. The game never sends the same
        // packet two times in a row.
        internal byte[] LastPacket;

        internal TokenBucket(double packetRate, double packetBurst, double byteRate, double byteBurst, double now)
        {
            this.packetRate = packetRate;
            this.packetBurst = packetBurst;
            this.byteRate = byteRate;
            this.byteBurst = byteBurst;
            packets = packetBurst;
            bytes = byteBurst;
            last = now;
            LastUse = now;
        }

        internal bool Has(int size, double now)
        {
            double dt = now - last;
            if (dt > 0)
            {
                packets = Math.Min(packetBurst, packets + dt * packetRate);
                bytes = Math.Min(byteBurst, bytes + dt * byteRate);
            }
            last = now;
            return packets >= 1 && bytes >= size;
        }

        internal void Take(int size)
        {
            packets -= 1;
            bytes -= size;
        }

        // True if this packet has the same bytes as the last one. Then
        // records it as the last one.
        internal bool IsRepeat(byte[] packet)
        {
            byte[] last = LastPacket;
            LastPacket = packet;
            if (last == null || last.Length != packet.Length) return false;
            if (ReferenceEquals(last, packet)) return true;
            for (int i = 0; i < packet.Length; i++)
                if (last[i] != packet[i]) return false;
            return true;
        }
    }

    // One per-player bucket per client id. Removes buckets that are idle.
    internal sealed class BucketTable
    {
        const double SweepIntervalSeconds = 30;

        readonly Dictionary<ulong, TokenBucket> buckets = new Dictionary<ulong, TokenBucket>();
        readonly List<ulong> idle = new List<ulong>();
        readonly double packetRate, packetBurst, byteRate, byteBurst;
        double nextSweep;

        internal BucketTable(double packetRate, double packetBurst, double byteRate, double byteBurst)
        {
            this.packetRate = packetRate;
            this.packetBurst = packetBurst;
            this.byteRate = byteRate;
            this.byteBurst = byteBurst;
        }

        internal TokenBucket Get(ulong key, double now)
        {
            Sweep(now);
            if (!buckets.TryGetValue(key, out TokenBucket bucket))
            {
                bucket = new TokenBucket(packetRate, packetBurst, byteRate, byteBurst, now);
                buckets[key] = bucket;
            }
            bucket.LastUse = now;
            return bucket;
        }

        void Sweep(double now)
        {
            if (now < nextSweep) return;
            nextSweep = now + SweepIntervalSeconds;
            foreach (var pair in buckets)
                if (now - pair.Value.LastUse > VoiceDataLimits.IdleEvictSeconds) idle.Add(pair.Key);
            foreach (ulong key in idle) buckets.Remove(key);
            idle.Clear();
        }

        internal void Clear()
        {
            buckets.Clear();
            idle.Clear();
            nextSweep = 0;
        }
    }
}
