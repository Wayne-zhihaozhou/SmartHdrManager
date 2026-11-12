namespace HdrBrightControl
{
    public interface IHdrSwitch
    {
        bool TryQuery(out bool supported, out bool enabled);
        HdrSwitchResult Set(bool enable);
        string ApiDescription { get; }
    }
}
