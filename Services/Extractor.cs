using System.Diagnostics;
using System.IO.Compression;

namespace ZenFiles.Services;

/// <summary>
/// Pakker ut arkiver på lokale disker. .zip pakkes ut med .NET (innebygd i Windows-delen av appen), andre formater
/// med 7-Zip hvis det er installert, ellers med tar.exe som følger med Windows 10/11 (tar, gz, bz2, xz).
/// </summary>
public static class Extractor
{
    static readonly string[] SevenZipExt = { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".iso", ".cab", ".wim" };
    static readonly string[] TarExt = { ".tar", ".gz", ".tgz", ".bz2", ".xz" };

    public static bool IsArchive(string name)
    {
        var n = name.ToLowerInvariant();
        return SevenZipExt.Any(e => n.EndsWith(e));
    }

    public static string? FindSevenZip()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim(), "7z.exe")); } catch { }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    static string? FindTar()
    {
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        return File.Exists(p) ? p : null;
    }

    /// <summary>Navn på arkivet uten endelse(r), f.eks. «bilder.tar.gz» → «bilder».</summary>
    public static string BaseName(string fileName)
    {
        var n = fileName;
        if (n.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase)
            || n.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase))
            n = n[..n.LastIndexOf(".tar", StringComparison.OrdinalIgnoreCase)];
        else n = Path.GetFileNameWithoutExtension(n);
        return n.Length == 0 ? "utpakket" : n;
    }

    /// <summary>Finner en mappe som ikke finnes fra før: navn, navn (2), navn (3) …</summary>
    public static string UniqueDir(string parent, string name)
    {
        var p = Path.Combine(parent, name);
        for (int i = 2; Directory.Exists(p) || File.Exists(p); i++) p = Path.Combine(parent, $"{name} ({i})");
        return p;
    }

    /// <summary>Pakker ut arkivet til destinasjonsmappen (opprettes ved behov). Kaster ved feil.</summary>
    public static async Task ExtractAsync(string archive, string destDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        var ext = Path.GetExtension(archive).ToLowerInvariant();

        var sevenZip = FindSevenZip();
        if (sevenZip != null)
        {
            await RunAsync(sevenZip, new[] { "x", "-y", "-aou", "-o" + destDir, archive }, ct);
            return;
        }

        if (ext == ".zip")
        {
            await Task.Run(() => ExtractZip(archive, destDir, ct), ct);
            return;
        }

        var tar = FindTar();
        if (tar != null && TarExt.Contains(ext))
        {
            await RunAsync(tar, new[] { "-xf", archive, "-C", destDir }, ct);
            return;
        }

        throw new InvalidOperationException(
            $"Filtypen {ext} krever 7-Zip. Installer 7-Zip (7-zip.org) og prøv igjen.");
    }

    static void ExtractZip(string archive, string destDir, CancellationToken ct)
    {
        var root = Path.GetFullPath(destDir);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;

        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            // Beskyttelse mot «zip slip»: ingenting får skrives utenfor målmappen.
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Arkivet inneholder en ugyldig sti og ble ikke pakket ut: " + entry.FullName);

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || entry.Name.Length == 0)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    static async Task RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Kunne ikke starte " + Path.GetFileName(exe));
        var err = p.StandardError.ReadToEndAsync(ct);
        _ = p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode > 1)   // 7-Zip: 1 = advarsel (f.eks. fil i bruk), ≥2 = feil
        {
            var msg = (await err).Trim();
            throw new InvalidOperationException(msg.Length > 0 ? msg : $"{Path.GetFileName(exe)} avsluttet med kode {p.ExitCode}.");
        }
    }
}
