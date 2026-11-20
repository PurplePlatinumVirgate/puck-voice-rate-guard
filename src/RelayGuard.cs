using System;
using Unity.Netcode;

namespace VoiceRateGuard
{
    // Server role. An out-of-range value is not relayed to clients. The RPC
    // handler reads sampleRate before this call, so a skip cannot misalign the
    // reader.
    internal static class RelayGuard
    {
        static readonly ThrottledLog log = new ThrottledLog("blocked");

        internal static int BlockedTotal;

        internal static bool Prefix(PlayerVoiceRecorder __instance, uint sampleRate, RpcParams rpcParams)
        {
            if (SampleRateRange.IsAllowed(sampleRate)) return StartLimit.ServerAllows(__instance, rpcParams);

            BlockedTotal++;
            try
            {
                ulong sender = rpcParams.Receive.SenderClientId;
                // Build the text only when a line is due (see ThrottledLog).
                if (log.Due(sender, "samplerate", out int skipped))
                    log.Write(sender, "samplerate",
                        "[voice_rate_guard] BLOCKED voice start: sampleRate=" + sampleRate +
                        " (allowed " + SampleRateRange.Min + "-" + SampleRateRange.Max + ")" +
                        " client=" + sender + " " + ThrottledLog.Who(__instance), skipped);
            }
            catch (Exception e)
            {
                // Never let logging change the outcome or throw into RPC dispatch.
                GuardLog.Error("[voice_rate_guard] log failed: " + e.Message);
            }
            return false;
        }

        internal static void Reset()
        {
            log.Clear();
            BlockedTotal = 0;
        }
    }
}
