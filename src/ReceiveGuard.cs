using System;

namespace VoiceRateGuard
{
    // Player role. An out-of-range value is ignored; only that player's voice
    // is lost here. The RPC handler reads sampleRate before this call, so a
    // skip cannot misalign the reader.
    internal static class ReceiveGuard
    {
        static readonly ThrottledLog log = new ThrottledLog("ignored");

        internal static int BlockedTotal;

        internal static bool Prefix(PlayerVoiceRecorder __instance, uint sampleRate)
        {
            if (SampleRateRange.IsAllowed(sampleRate)) return StartLimit.PlayerAllows(__instance);

            BlockedTotal++;
            try
            {
                // Unity null check: the recorder can be gone mid-despawn.
                ulong owner = __instance ? __instance.OwnerClientId : 0;
                // Build the text only when a line is due (see ThrottledLog).
                if (log.Due(owner, "samplerate", out int skipped))
                    log.Write(owner, "samplerate",
                        "[voice_rate_guard] IGNORED voice start: sampleRate=" + sampleRate +
                        " (allowed " + SampleRateRange.Min + "-" + SampleRateRange.Max + ") " +
                        ThrottledLog.Who(__instance), skipped);
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
