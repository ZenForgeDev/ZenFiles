using Microsoft.VisualBasic.FileIO;
using ZenFiles.Models;

namespace ZenFiles.Services;

public sealed class LocalFileSystem : IFileSystem
{
    public static readonly LocalFileSystem Instance = new();

    public bool IsRemote => false;
    public string DisplayName => "Denne PC-en";

    public string Combine(string dir, string name) => System.IO.Path.Combine(dir, name);

    public string? Parent(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;          // "Denne PC-en" er toppen
        var p = System.IO.Path.GetDirectoryName(path.TrimEnd('\\'));
        return p ?? "";                                        // stasjonsrot -> Denne PC-en
    }

    public Task<List<FileItem>> ListAsync(string path, bool showHidden) => Task.Run(() =>
    {
        var res = new List<FileItem>();

        if (string.IsNullOrEmpty(path))
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady) continue;
                var label = string.IsNullOrEmpty(d.VolumeLabel) ? "Lokal disk" : d.VolumeLabel;
                res.Add(new FileItem
                {
                    Name = $"{label} ({d.Name.TrimEnd('\\')})",
                    FullPath = d.Name,
                    IsDirectory = true,
                    IsDrive = true,
                    Size = d.TotalSize
                });
            }
            return res;
        }

        var di = new DirectoryInfo(path);
        foreach (var e in di.EnumerateFileSystemInfos())
        {
            bool hidden = (e.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
            if (hidden && !showHidden) continue;
            bool dir = (e.Attributes & FileAttributes.Directory) != 0;
            res.Add(new FileItem
            {
                Name = e.Name,
                FullPath = e.FullName,
                IsDirectory = dir,
                Size = dir ? 0 : (e is FileInfo fi ? fi.Length : 0),
                Modified = e.LastWriteTime
            });
        }
        return res;
    });

    public Task<bool> ExistsAsync(string path) => Task.FromResult(File.Exists(path) || Directory.Exists(path));

    public Task<Stream> OpenReadAsync(string path) =>
        Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, true));

    public Task<Stream> OpenWriteAsync(string path) =>
        Task.FromResult<Stream>(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true));

    public Task CreateDirectoryAsync(string path) => Task.Run(() => Directory.CreateDirectory(path));

    /// <summary>Lokale filer går til papirkurven.</summary>
    public Task DeleteAsync(string path, bool isDirectory) => Task.Run(() =>
    {
        if (isDirectory)
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        else
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    });

    public Task RenameAsync(string oldPath, string newPath) => Task.Run(() =>
    {
        if (Directory.Exists(oldPath)) Directory.Move(oldPath, newPath);
        else File.Move(oldPath, newPath);
    });
}
