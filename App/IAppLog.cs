namespace HdrBrightControl
{
    public interface IAppLog
    {
        void Log(string tag, string message);
        void WriteLine(string line);
    }
}
