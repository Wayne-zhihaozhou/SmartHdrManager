namespace HdrBrightControl
{
    /// <summary>Which Windows HDR API is used for switching.</summary>
    public enum HdrApiKind { Unknown, Classic, Modern }

    /// <summary>Result of an HDR switch attempt.</summary>
    public readonly struct HdrSwitchResult
    {
        public bool Ok { get; }
        public string Error { get; }

        private HdrSwitchResult(bool ok, string error) { Ok = ok; Error = error; }

        public static HdrSwitchResult Success() => new(true, string.Empty);
        public static HdrSwitchResult Failure(string error) => new(false, error);
    }
}
