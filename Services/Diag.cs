using System.Diagnostics;

namespace ZenFiles.Services;

/// <summary>
/// Enkel feilsøkingslogg i %LOCALAPPDATA%\ZenFiles\diag.log (maks omtrent 400 KB, eldste linjer kastes). Brukes til å finne
/// feil som bare dukker opp av og til. Den inneholder mappestier, men ingen filinnhold.
/// </summary>
public static class Diag
{
    static readonly object _lock = new();
    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenFiles", "diag.log");
    static readonly DateTime _started = DateTime.Now;

    public static void Log(string msg)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 400_000)
                {
                    var lines = File.ReadAllLines(FilePath);
                    File.WriteAllLines(FilePath, lines.Skip(lines.Length / 2));
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }

    /// <summary>Tråd- og ressursstatus for prosessen, til å lime inn i loggen.</summary>
    public static string Env()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return $"up={(DateTime.Now - _started).TotalMinutes:0}min tp={ThreadPool.ThreadCount}/pending={ThreadPool.PendingWorkItemCount} " +
                   $"handles={p.HandleCount} mem={p.WorkingSet64 / 1048576}MB gc={GC.GetTotalMemory(false) / 1048576}MB";
        }
        catch { return "env=?"; }
    }

    public static void Start() => Log($"=== ZenFiles start, v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version} {Env()}");
}
