using MediaDevices;
using ZenFiles.Models;

namespace ZenFiles.Services;

/// <summary>
/// Filsystem for en mobil/nettbrett koblet til med USB (MTP/PTP via Windows Portable Devices, pakken «MediaDevices»).
/// Stiene bruker «/» som skilletegn (som SFTP), og «/» er listen over lagringsområder på enheten.
/// Siden MTP ikke kan lese eller skrive i biter, går alle overføringer via en midlertidig fil.
/// </summary>
public sealed class PhoneFileSystem : IFileSystem, IThumbnailSource, IDisposable
{
    readonly MediaDevice _dev;

    public string DeviceName { get; }
    public bool IsRemote => true;
    public string DisplayName => DeviceName;

    public static string TempRoot => Path.Combine(Path.GetTempPath(), "ZenFiles", "phone");

    internal PhoneFileSystem(MediaDevice dev, string name)
    {
        _dev = dev;
        DeviceName = name;
    }

    /// <summary>Biblioteket bruker «\» og vil ha stien som på enheten.</summary>
    static string Dev(string path) => string.IsNullOrEmpty(path) ? "\\" : path.Replace('/', '\\');

    static bool IsRoot(string path) => string.IsNullOrEmpty(path) || path == "/";

    public string Combine(string dir, string name) =>
        IsRoot(dir) ? "/" + name : dir.TrimEnd('/') + "/" + name;

    public string? Parent(string path)
    {
        if (IsRoot(path)) return null;
        var t = path.TrimEnd('/');
        var i = t.LastIndexOf('/');
        return i <= 0 ? "/" : t[..i];
    }

    public Task<List<FileItem>> ListAsync(string path, bool showHidden) => Task.Run(() =>
    {
        var res = new List<FileItem>();
        var dir = IsRoot(path) ? _dev.GetRootDirectory() : _dev.GetDirectoryInfo(Dev(path));
        foreach (var e in dir.EnumerateFileSystemInfos())
        {
            bool isDir, hidden;
            try
            {
                var a = e.Attributes;
                isDir = (a & MediaFileAttributes.Directory) != 0;
                hidden = (a & MediaFileAttributes.Hidden) != 0;
            }
            catch { isDir = false; hidden = false; }

            if (IsRoot(path)) { isDir = true; hidden = false; }   // lagringsområder på toppnivå er alltid mapper

            var name = e.Name;
            if (!showHidden && (hidden || name.StartsWith('.'))) continue;

            long size = 0;
            DateTime modified = default;
            if (!isDir)
            {
                try { size = (long)e.Length; } catch { }
            }
            try { modified = e.LastWriteTime ?? default; } catch { }

            res.Add(new FileItem
            {
                Name = name,
                FullPath = Combine(path, name),
                IsDirectory = isDir,
                Remote = true,
                Size = size,
                Modified = modified
            });
        }
        return res;
    });

    public Task<byte[]?> GetThumbnailAsync(string path) => Task.Run<byte[]?>(() =>
    {
        try
        {
            using var ms = new MemoryStream();
            _dev.DownloadThumbnail(Dev(path), ms);
            return ms.Length > 0 ? ms.ToArray() : null;
        }
        catch { return null; }
    });

    public Task<bool> ExistsAsync(string path) => Task.Run(() =>
        IsRoot(path) || _dev.DirectoryExists(Dev(path)) || _dev.FileExists(Dev(path)));

    public Task<Stream> OpenReadAsync(string path) => Task.Run<Stream>(() =>
    {
        Directory.CreateDirectory(TempRoot);
        var tmp = Path.Combine(TempRoot, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        using (var o = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            _dev.DownloadFile(Dev(path), o);
        // Slettes av seg selv når strømmen lukkes.
        return new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    });

    public Task<Stream> OpenWriteAsync(string path) =>
        Task.FromResult<Stream>(new PhoneUploadStream(this, Dev(path)));

    internal void Upload(string tempFile, string devicePath) => _dev.UploadFile(tempFile, devicePath);

    public Task CreateDirectoryAsync(string path) => Task.Run(() =>
    {
        if (!_dev.DirectoryExists(Dev(path))) _dev.CreateDirectory(Dev(path));
    });

    public Task DeleteAsync(string path, bool isDirectory) => Task.Run(() =>
    {
        if (isDirectory) _dev.DeleteDirectory(Dev(path), true);
        else _dev.DeleteFile(Dev(path));
    });

    public Task RenameAsync(string oldPath, string newPath) => Task.Run(() =>
    {
        var newName = newPath.TrimEnd('/');
        newName = newName[(newName.LastIndexOf('/') + 1)..];
        _dev.Rename(Dev(oldPath), newName);
    });

    public void Dispose()
    {
        try { if (_dev.IsConnected) _dev.Disconnect(); } catch { }
        try { _dev.Dispose(); } catch { }
    }
}

/// <summary>
/// Skriver til en midlertidig fil og sender den til enheten når strømmen lukkes.
/// (En egen Stream i stedet for FileStream, slik at både Dispose og DisposeAsync laster opp.)
/// </summary>
sealed class PhoneUploadStream : Stream
{
    readonly PhoneFileSystem _fs;
    readonly string _dest;
    readonly string _tmp;
    FileStream? _f;
    bool _done;

    public PhoneUploadStream(PhoneFileSystem fs, string devicePath)
    {
        _fs = fs;
        _dest = devicePath;
        Directory.CreateDirectory(PhoneFileSystem.TempRoot);
        _tmp = Path.Combine(PhoneFileSystem.TempRoot, Guid.NewGuid().ToString("N") + ".upload");
        _f = new FileStream(_tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _f?.Length ?? 0;
    public override long Position
    {
        get => _f?.Position ?? 0;
        set => throw new NotSupportedException();
    }

    public override void Flush() => _f?.Flush();
    public override Task FlushAsync(CancellationToken ct) => _f?.FlushAsync(ct) ?? Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => _f!.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _f!.Write(buffer);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        _f!.WriteAsync(buffer, offset, count, ct);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        _f!.WriteAsync(buffer, ct);

    void Finish()
    {
        if (_done) return;
        _done = true;
        try
        {
            _f?.Dispose();
            _f = null;
            _fs.Upload(_tmp, _dest);
        }
        finally
        {
            try { File.Delete(_tmp); } catch { }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Finish();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await Task.Run(Finish);
        await base.DisposeAsync();
    }
}
