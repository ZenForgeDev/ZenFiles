using ZenFiles.Models;

namespace ZenFiles.Services;

/// <summary>Kopiering mellom vilkårlige filsystemer (lokal↔SFTP, SFTP↔SFTP, lokal↔lokal) via strømmer.</summary>
public static class Transfer
{
    public static async Task CopyAsync(
        IFileSystem src, FileItem item, IFileSystem dst, string dstDir,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (ReferenceEquals(src, dst) && (dstDir == item.FullPath ||
            dstDir.StartsWith(item.FullPath.TrimEnd('\\', '/') + (src.IsRemote ? "/" : "\\"), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Kan ikke kopiere en mappe inn i seg selv.");

        var target = await UniqueAsync(dst, dstDir, item.Name);

        if (item.IsDirectory)
        {
            await dst.CreateDirectoryAsync(target);
            foreach (var child in await src.ListAsync(item.FullPath, true))
            {
                ct.ThrowIfCancellationRequested();
                await CopyAsync(src, child, dst, target, progress, ct);
            }
            return;
        }

        progress?.Report(item.Name);
        await using var input = await src.OpenReadAsync(item.FullPath);
        await using var output = await dst.OpenWriteAsync(target);
        await input.CopyToAsync(output, 81920, ct);
    }

    /// <summary>Overskriver aldri stille: gir «navn - kopi», «navn - kopi 2» osv. hvis målet finnes.</summary>
    static async Task<string> UniqueAsync(IFileSystem fs, string dir, string name)
    {
        var candidate = fs.Combine(dir, name);
        if (!await fs.ExistsAsync(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 1; ; i++)
        {
            var n = i == 1 ? $"{stem} - kopi{ext}" : $"{stem} - kopi {i}{ext}";
            candidate = fs.Combine(dir, n);
            if (!await fs.ExistsAsync(candidate)) return candidate;
        }
    }
}
