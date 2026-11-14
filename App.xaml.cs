using Microsoft.UI.Xaml;
using ZenFiles.Services;

namespace ZenFiles;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();

        // Nødutgang uten vindu: ZenFiles.exe --restore-default  (setter Windows Utforsker tilbake som standard)
        if (argv.Contains("--restore-default", StringComparer.OrdinalIgnoreCase))
        {
            try { DefaultFileManager.Disable(); } catch { }
            Exit();
            return;
        }

        // Windows sender mappen som skal åpnes som første argument ("%1").
        string? start = null;
        var raw = argv.FirstOrDefault(a => !a.StartsWith("--"));
        if (!string.IsNullOrWhiteSpace(raw))
        {
            raw = raw.Trim().Trim('"');
            if (Directory.Exists(raw)) start = raw;
            else if (File.Exists(raw)) start = Path.GetDirectoryName(raw);
            // Ikke-filsystem-steder (f.eks. ::{GUID}) faller tilbake til Hjem. Vi starter aldri explorer.exe herfra,
            // fordi det ville utløst oss selv på nytt i en evig løkke.
        }

        MainWindow = new MainWindow(start);
        MainWindow.Activate();
    }
}
