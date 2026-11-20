using System;
using Unity.Netcode;
using UnityEngine;

namespace VoiceRateGuard
{
    // Server side (dedicated server or listen host). A malformed or excessive
    // voice packet is not relayed to clients.
    internal static class DataGuard
    {
        static readonly BucketTable senders = new BucketTable(
            VoiceDataLimits.PacketsPerSecond, VoiceDataLimits.PacketBurst,
            VoiceDataLimits.BytesPerSecond, VoiceDataLimits.ByteBurst);
        static readonly ThrottledLog log = new ThrottledLog("blocked");
        static TokenBucket server;
        static bool failed;

        internal static int BlockedTotal;

        internal static bool Prefix(PlayerVoiceRecorder __instance, byte[] voiceData, RpcParams rpcParams)
        {
            // Constant strings only: this path can run thousands of times per second.
            string reason;
            ulong sender;
            int size = 0;
            try
            {
                if (!RpcStage.IsExecute(__instance)) return true;

                // The game drops a packet from a non-owner. Do not count it, so
                // it cannot use up the owner's budget.
                sender = rpcParams.Receive.SenderClientId;
                if (sender != __instance.OwnerClientId) return true;

                // The game also drops packets while the player does not
                // record. Do not count them.
                if (!__instance.IsRecording) return true;

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
                    TokenBucket mine = senders.Get(sender, now);
                    if (server == null)
                        server = new TokenBucket(
                            VoiceDataLimits.ServerPacketsPerSecond, VoiceDataLimits.ServerPacketBurst,
                            VoiceDataLimits.ServerBytesPerSecond, VoiceDataLimits.ServerByteBurst, now);

                    if (mine.IsRepeat(voiceData)) reason = "repeat";
                    else if (!mine.Has(size, now)) reason = "rate";
                    else if (!server.Has(size, now)) reason = "rate-global";
                    else
                    {
                        mine.Take(size);
                        server.Take(size);
                        return true;
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
                if (log.Due(sender, reason, out int skipped))
                    log.Write(sender, reason, "[voice_rate_guard] BLOCKED voice data: reason=" +
                        (reason == "size" ? "size:" + size : reason) +
                        " client=" + sender + " " + ThrottledLog.Who(__instance), skipped);
            }
            catch (Exception e)
            {
                GuardLog.Error("[voice_rate_guard] log failed: " + e.Message);
            }
            return false;
        }

        internal static void Reset()
        {
            senders.Clear();
            log.Clear();
            server = null;
            failed = false;
            BlockedTotal = 0;
        }
    }
}
