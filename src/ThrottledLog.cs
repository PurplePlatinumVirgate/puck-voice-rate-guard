using System;
using System.Collections.Generic;
using UnityEngine;

namespace VoiceRateGuard
{
    // At most one log line per client id and reason every 10 s. The next line
    // for the same client and reason shows how many were not logged in between.
    // When no line comes for 10 s, GuardTicker writes the pending count, so the
    // count of a short attack is not lost.
    //
    // Due() does not allocate. Build the log text only when it returns true.
    internal sealed class ThrottledLog
    {
        const double IntervalSeconds = 10;

        // All logs, for GuardTicker. Main thread only.
        static readonly List<ThrottledLog> all = new List<ThrottledLog>();

        sealed class Entry
        {
            internal double LastAt;
            internal int Skipped;
            internal string Text;   // the last line, without its count
        }

        readonly Dictionary<(ulong, string), Entry> entries = new Dictionary<(ulong, string), Entry>();
        readonly List<(ulong, string)> idle = new List<(ulong, string)>();
        readonly string verb;
        double nextSweep;

        // verb: "blocked" or "ignored", as in the voice start lines.
        internal ThrottledLog(string verb)
        {
            this.verb = verb;
            all.Add(this);
        }

        // kind: a constant reason name without details ("size", not
        // "size:17000"). Returns true when a line is due: then call Write with
        // the same client and kind, and the skipped count.
        internal bool Due(ulong client, string kind, out int skipped)
        {
            double now = Clock.Now;
            Sweep(now);
            var key = (client, kind);
            if (!entries.TryGetValue(key, out Entry e))
            {
                entries[key] = new Entry { LastAt = now };
                skipped = 0;
                return true;
            }
            if (now - e.LastAt < IntervalSeconds)
            {
                e.Skipped++;
                skipped = 0;
                return false;
            }
            skipped = e.Skipped;
            e.Skipped = 0;
            e.LastAt = now;
            return true;
        }

        internal void Write(ulong client, string kind, string text, int skipped)
        {
            if (entries.TryGetValue((client, kind), out Entry e)) e.Text = text;
            Debug.LogWarning(text + (skipped > 0 ? " (+" + skipped + " more " + verb + " since last line)" : ""));
        }

        // Writes the pending count of each entry with no line for 10 s. The
        // line has no "+": it is not a new block, only the final count.
        void FlushIdle(double now)
        {
            foreach (var pair in entries)
            {
                Entry e = pair.Value;
                if (e.Skipped == 0 || e.Text == null || now - e.LastAt < IntervalSeconds) continue;
                Debug.LogWarning(e.Text + " (final count: " + e.Skipped + " more " + verb + " since last line)");
                e.Skipped = 0;
                e.LastAt = now;
            }
        }

        internal static void FlushAll(double now)
        {
            for (int i = 0; i < all.Count; i++) all[i].FlushIdle(now);
        }

        // Removes entries with no pending count that are idle for more than
        // IdleEvictSeconds, at most every 30 s. NGO gives each reconnect a new
        // client id, so without this the table grows with every reconnect.
        void Sweep(double now)
        {
            if (now < nextSweep) return;
            nextSweep = now + 30;
            foreach (var pair in entries)
                if (pair.Value.Skipped == 0 && now - pair.Value.LastAt > VoiceDataLimits.IdleEvictSeconds) idle.Add(pair.Key);
            foreach (var key in idle) entries.Remove(key);
            idle.Clear();
        }

        internal void Clear()
        {
            entries.Clear();
            idle.Clear();
            nextSweep = 0;
        }

        internal static string Who(PlayerVoiceRecorder recorder)
        {
            ulong owner = 0;
            string steamId = "?", name = "?";
            // Unity null checks: the recorder or its Player can be gone mid-despawn.
            if (recorder)
            {
                owner = recorder.OwnerClientId;
                var player = recorder.Player;
                if (player)
                {
                    steamId = player.SteamId.Value.ToString();
                    name = player.Username.Value.ToString();
                }
            }
            return "owner=" + owner + " steamId=" + steamId + " name=\"" + name + "\"";
        }
    }

    // Writes pending log counts once per second (see ThrottledLog).
    internal sealed class GuardTicker : MonoBehaviour
    {
        double next;

        void Update()
        {
            double now = Clock.Now;
            if (now < next) return;
            next = now + 1;
            try
            {
                ThrottledLog.FlushAll(now);
            }
            catch (Exception e)
            {
                GuardLog.ErrorOnce(ref failed, "[voice_rate_guard] log flush failed: " + e);
            }
        }

        bool failed;
    }

    // Error lines for the guards. A failure inside Debug.LogError is counted,
    // not thrown into RPC dispatch.
    internal static class GuardLog
    {
        internal static int LogFailures;

        internal static void Error(string text)
        {
            try
            {
                Debug.LogError(text);
            }
            catch (Exception)
            {
                LogFailures++;
            }
        }

        // Writes the line only the first time; flag is the caller's own state.
        internal static void ErrorOnce(ref bool flag, string text)
        {
            if (flag) return;
            flag = true;
            Error(text);
        }
    }
}
