namespace Omdrop;

public static class Log
{
    static readonly object Gate = new();
    public static bool Verbose { get; set; }

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg) => Write("ERROR", msg);
    public static void Debug(string msg)
    {
        if (Verbose)
            Write("DEBUG", msg);
    }

    /// Also append to this file (a service has no console worth writing to).
    public static string? FilePath { get; set; }

    /// Hears every line, for UIs that show a log.
    public static event Action<string>? Line;

    static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {level}: {msg}";
        lock (Gate)
        {
            Console.Error.WriteLine(line);
            if (FilePath != null)
            {
                try
                {
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}");
                }
                catch (IOException)
                {
                }
            }
        }
        Line?.Invoke(line);
    }
}
