using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace VoiceRateGuard
{
    // Player side. A malformed or excessive voice packet is ignored; only that
    // packet of that player's voice is lost here.
    internal static class ReceiveDataGuard
    {
        static readonly BucketTable speakers = new BucketTable(
            VoiceDataLimits.PacketsPerSecond, VoiceDataLimits.PacketBurst,
            VoiceDataLimits.BytesPerSecond, VoiceDataLimits.ByteBurst);
        static readonly ThrottledLog log = new ThrottledLog("ignored");
        static bool failed;

        internal static int BlockedTotal;

        internal static bool Prefix(PlayerVoiceRecorder __instance, byte[] voiceData, ConcurrentQueue<float> ___audioQueue)
        {
            string reason;
            ulong owner;
            int size = 0;
            try
            {
                if (!RpcStage.IsExecute(__instance)) return true;

                owner = __instance.OwnerClientId;
                if (voiceData == null)
                {
                    reason = "null";
                }
                else if ((size = voiceData.Length) == 0)
                {
                    reason = "empty";
                }
                else if (size > VoiceDataLimits.MaxVoiceBytes)
                {
                    reason = "size";
                }
                else
                {
                    double now = Clock.Now;
                    TokenBucket bucket = speakers.Get(owner, now);
                    if (bucket.IsRepeat(voiceData))
                    {
                        reason = "repeat";
                    }
                    else if (!bucket.Has(size, now))
                    {
                        reason = "rate";
                    }
                    else
                    {
                        // Keep at most MaxQueuedSeconds of audio waiting for
                        // playback, with room for one more packet.
                        long rate = __instance.SampleRate;
                        if (rate != 0 && ___audioQueue != null &&
                            ___audioQueue.Count > rate * (VoiceDataLimits.MaxQueuedSeconds - 1))
                        {
                            // The game also queues the local player's own voice,
                            // which never plays. Cap it without a log line.
                            if (__instance.IsOwner) return false;
                            reason = "queue";
                        }
                        else
                        {
                            bucket.Take(size);
                            return true;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                // Fail open: the game then behaves as without this guard.
                if (!failed)
                {
                    failed = true;
                    GuardLog.Error("[voice_rate_guard] voice data check failed: " + e);
                }
                return true;
            }

            BlockedTotal++;
            try
            {
                // Build the text only when a line is due (see ThrottledLog).
                if (log.Due(owner, reason, out int skipped))
                    log.Write(owner, reason, "[voice_rate_guard] IGNORED voice data: reason=" +
                        (reason == "size" ? "size:" + size : reason) + " " + ThrottledLog.Who(__instance), skipped);
            }
            catch (Exception e)
            {
                GuardLog.Error("[voice_rate_guard] log failed: " + e.Message);
            }
            return false;
        }

        internal static void Reset()
        {
            speakers.Clear();
            log.Clear();
            failed = false;
            BlockedTotal = 0;
        }
    }
}
