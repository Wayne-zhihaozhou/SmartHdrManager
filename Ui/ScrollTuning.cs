namespace HdrBrightControl
{
    /// <summary>Scroll tuning constants shared by GameList and SmoothScrollTable.</summary>
    internal static class ScrollTuning
    {
        public const double GlideRate = 8.5;
        public const float Tolerance = 0.3f;
        public const float MaxFrameSeconds = 0.05f;
        public const float MinFrameSeconds = 0.001f;
        public const float FullPageFactor = 0.9f;
        public const float MinWheelStep = 60f;
        public const float WheelLineFactor = 1.6f;
        public const float WheelRowFactor = 1.2f;
    }
}
