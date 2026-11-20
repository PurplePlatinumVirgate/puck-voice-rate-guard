namespace VoiceRateGuard
{
    // Limits for voice data packets. Normal voice (measured 2026-11-12, B1235):
    // one packet every ~50 ms (20 per second), 18-671 bytes, ~190 bytes on
    // average. A frame hitch gives one larger packet, not more packets.
    internal static class VoiceDataLimits
    {
        internal const int MaxVoiceBytes = 16384;

        // Per player: 1.5 times the normal packet rate, 3 s of burst.
        internal const double PacketsPerSecond = 30;
        internal const double PacketBurst      = 60;
        internal const double BytesPerSecond   = 12288;
        internal const double ByteBurst        = 24576;

        // All players together, server only.
        internal const double ServerPacketsPerSecond = 600;
        internal const double ServerPacketBurst      = 1200;
        internal const double ServerBytesPerSecond   = 196608;
        internal const double ServerByteBurst        = 393216;

        // Most audio that one player can have waiting for playback.
        internal const uint MaxQueuedSeconds = 2;

        internal const double IdleEvictSeconds = 60;
    }
}
