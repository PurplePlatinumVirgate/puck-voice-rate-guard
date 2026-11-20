namespace VoiceRateGuard
{
    // The sample rates that both roles allow.
    //
    // SteamUser.GetVoiceOptimalSampleRate() returns 11025-48000 Hz on a normal
    // client. 8000 leaves room for the lowest common voice rate.
    internal static class SampleRateRange
    {
        internal const uint Min = 8000;
        internal const uint Max = 48000;

        internal static bool IsAllowed(uint sampleRate) => sampleRate >= Min && sampleRate <= Max;
    }
}
