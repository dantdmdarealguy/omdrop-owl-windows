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

    static void Write(string level, string msg)
    {
        lock (Gate)
            Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {level}: {msg}");
    }
}
