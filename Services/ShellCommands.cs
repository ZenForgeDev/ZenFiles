using System.Text;
using ZenFiles.Models;

namespace ZenFiles.Services;

/// <summary>Bygger kommandolinjene terminalen starter. SSH-terminalen bruker Windows' innebygde ssh.exe.</summary>
public static class ShellCommands
{
    public static string LocalShell()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (File.Exists(Path.Combine(dir.Trim(), "pwsh.exe"))) return "pwsh.exe"; } catch { }
        }
        return "powershell.exe";
    }

    public static string LocalShellAt(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return LocalShell();
        var safe = folder.Replace("'", "''");
        return $"{LocalShell()} -NoExit -Command \"Set-Location -LiteralPath '{safe}'\"";
    }

    public static string Ssh(ConnectionProfile p, string? remoteDir = null)
    {
        var sb = new StringBuilder("ssh.exe");
        sb.Append($" -p {p.Port}");
        if (p.UseKey && !string.IsNullOrWhiteSpace(p.KeyPath)) sb.Append($" -i \"{p.KeyPath}\"");
        if (remoteDir != null) sb.Append(" -t");
        sb.Append($" {p.User}@{p.Host}");
        if (remoteDir != null)
            sb.Append($" \"cd '{remoteDir.Replace("'", "'\\''")}' && exec $SHELL -l\"");
        return sb.ToString();
    }
}
