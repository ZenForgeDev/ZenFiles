using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ZenFiles.Services;

/// <summary>
/// Gjør ZenFiles til standard filbehandler for mapper og Win+E, kun for din bruker (HKCU, ingen admin).
/// Lagrer opprinnelige verdier i en sikkerhetskopi og kan reverseres helt.
/// </summary>
public static class DefaultFileManager
{
    static readonly string[] Classes = { "Folder", "Directory", "Drive" };
    static readonly string[] Verbs = { "open", "explore", "opennewwindow" };
    const string WinEVerb = @"Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow";

    static string BackupPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenFiles", "default-backup.json");

    sealed class Entry
    {
        public bool Existed { get; set; }
        public string? Default { get; set; }
        public string? Delegate { get; set; }
    }

    static string Exe => Environment.ProcessPath ?? throw new InvalidOperationException("Finner ikke programstien.");

    /// <summary>Alle verb-nøkler vi endrer, med kommandoen de skal få.</summary>
    static IEnumerable<(string VerbKey, string Command)> Targets()
    {
        foreach (var c in Classes)
            foreach (var v in Verbs)
                yield return ($@"Software\Classes\{c}\shell\{v}", $"\"{Exe}\" \"%1\"");
        yield return (WinEVerb, $"\"{Exe}\"");   // Win+E uten argument
    }

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Folder\shell\open\command");
            var v = k?.GetValue("") as string;
            return v != null && v.Contains(Exe, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void Enable()
    {
        // Sikkerhetskopi av eksisterende HKCU-verdier – kun første gang, så vi aldri overskriver originalen med vår egen.
        if (!File.Exists(BackupPath))
        {
            var backup = new Dictionary<string, Entry>();
            foreach (var (verbKey, _) in Targets())
            {
                using var k = Registry.CurrentUser.OpenSubKey(verbKey + @"\command");
                backup[verbKey] = k == null
                    ? new Entry { Existed = false }
                    : new Entry { Existed = true, Default = k.GetValue("") as string, Delegate = k.GetValue("DelegateExecute") as string };
            }
            Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
            File.WriteAllText(BackupPath, JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
        }

        foreach (var (verbKey, command) in Targets())
        {
            using var k = Registry.CurrentUser.CreateSubKey(verbKey + @"\command");
            k.SetValue("", command, RegistryValueKind.String);
            k.SetValue("DelegateExecute", "", RegistryValueKind.String);   // tom = ikke bruk Explorers handler
        }
        Notify();
    }

    public static void Disable()
    {
        Dictionary<string, Entry> backup = new();
        try
        {
            if (File.Exists(BackupPath))
                backup = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(BackupPath)) ?? new();
        }
        catch { }

        foreach (var (verbKey, _) in Targets())
        {
            if (backup.TryGetValue(verbKey, out var e) && e.Existed)
            {
                using var k = Registry.CurrentUser.CreateSubKey(verbKey + @"\command");
                if (e.Default != null) k.SetValue("", e.Default, RegistryValueKind.String); else k.DeleteValue("", false);
                if (e.Delegate != null) k.SetValue("DelegateExecute", e.Delegate, RegistryValueKind.String);
                else k.DeleteValue("DelegateExecute", false);
            }
            else
            {
                Registry.CurrentUser.DeleteSubKeyTree(verbKey, false);
            }
        }

        try { File.Delete(BackupPath); } catch { }
        Notify();
    }

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    static void Notify() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
}
