namespace GpuSwitcher.Core;

public static class Log
{
    static readonly object Gate = new();
    public static string Dir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GpuSwitcher");
    static string FilePath => Path.Combine(Dir, "log.txt");

    public static event Action<string>? Message;

    public static void Write(string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 2_000_000) File.Move(FilePath, FilePath + ".old", true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { }
        }
        Message?.Invoke(msg);
    }
}
