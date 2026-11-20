using System;
using Unity.Netcode;
using UnityEngine;

namespace VoiceRateGuard
{
    // Limits how often one player can start voice chat. The game sends a start
    // only when a person presses the talk key. A stop always needs a start
    // before it, so this also limits the stops. Stops are never blocked: a
    // blocked stop would leave the microphone open.
    internal static class StartLimit
    {
        // Above the fastest key tapping by hand (~10-15 per second), with 3 s
        // of burst for presses that a network stall delivers together.
        internal const double StartsPerSecond = 15;
        internal const double StartBurst      = 45;

        // The byte budget is not used (size 0).
        static readonly BucketTable players = new BucketTable(StartsPerSecond, StartBurst, 1, 1);
        static readonly ThrottledLog blockedLog = new ThrottledLog("blocked");
        static readonly ThrottledLog ignoredLog = new ThrottledLog("ignored");
        static bool failed;

        internal static int BlockedTotal;

        // Server role, called by RelayGuard after the sampleRate check passes.
        // Returns false to skip the original: the start is not relayed.
        internal static bool ServerAllows(PlayerVoiceRecorder recorder, RpcParams rpcParams)
        {
            ulong sender;
            try
            {
                if (!RpcStage.IsExecute(recorder)) return true;
                sender = rpcParams.Receive.SenderClientId;
                // Count only the starts that the game accepts; it drops the others.
                if (sender != recorder.OwnerClientId || !recorder.IsEnabled || recorder.IsRecording) return true;
                if (Take(sender)) return true;
            }
            catch (Exception e)
            {
                FailOpen(e);
                return true;
            }

            BlockedTotal++;
            Log(blockedLog, sender, true, recorder);
            return false;
        }

        // Player role, called by ReceiveGuard after the sampleRate check passes.
        // Returns false to skip the original: no clip, no log line, no event.
        internal static bool PlayerAllows(PlayerVoiceRecorder recorder)
        {
            ulong owner;
            try
            {
                // The local player's own starts come from this client's talk key.
                if (!RpcStage.IsExecute(recorder) || recorder.IsOwner) return true;
                owner = recorder.OwnerClientId;
                if (Take(owner)) return true;
            }
            catch (Exception e)
            {
                FailOpen(e);
                return true;
            }

            BlockedTotal++;
            Log(ignoredLog, owner, false, recorder);
            return false;
        }

        static bool Take(ulong key)
        {
            double now = Clock.Now;
            TokenBucket bucket = players.Get(key, now);
            if (!bucket.Has(0, now)) return false;
            bucket.Take(0);
            return true;
        }

        // Builds the text only when a line is due (see ThrottledLog).
        static void Log(ThrottledLog log, ulong key, bool server, PlayerVoiceRecorder recorder)
        {
            try
            {
                if (!log.Due(key, "rate", out int skipped)) return;
                log.Write(key, "rate", server
                    ? "[voice_rate_guard] BLOCKED voice start: reason=rate client=" + key + " " + ThrottledLog.Who(recorder)
                    : "[voice_rate_guard] IGNORED voice start: reason=rate " + ThrottledLog.Who(recorder), skipped);
            }
            catch (Exception e)
            {
                GuardLog.Error("[voice_rate_guard] log failed: " + e.Message);
            }
        }

        static void FailOpen(Exception e)
        {
            // The game then behaves as without this limit.
            if (failed) return;
            failed = true;
            GuardLog.Error("[voice_rate_guard] voice start limit failed: " + e);
        }

        internal static void Reset()
        {
            players.Clear();
            blockedLog.Clear();
            ignoredLog.Clear();
            failed = false;
            BlockedTotal = 0;
        }
    }

    // Player role. The game runs a stop even when that player does not record:
    // it then only writes a log line and stops a silent audio source. This
    // happens after an ignored start, so skip it. The local player is not
    // changed.
    internal static class StopGuard
    {
        static bool failed;

        internal static bool Prefix(PlayerVoiceRecorder __instance)
        {
            try
            {
                if (RpcStage.IsExecute(__instance) && !__instance.IsOwner && !__instance.IsRecording) return false;
            }
            catch (Exception e)
            {
                // Fail open.
                GuardLog.ErrorOnce(ref failed, "[voice_rate_guard] voice stop check failed: " + e);
            }
            return true;
        }
    }
}
