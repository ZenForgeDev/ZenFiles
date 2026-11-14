using ZenFiles.Models;

namespace ZenFiles.Services;

/// <summary>Felles grensesnitt slik at lokale disker og SFTP-servere oppfører seg likt i UI-et.</summary>
public interface IFileSystem
{
    bool IsRemote { get; }
    string DisplayName { get; }

    string Combine(string dir, string name);
    /// <summary>Overordnet mappe, eller null hvis dette er toppen.</summary>
    string? Parent(string path);

    Task<List<FileItem>> ListAsync(string path, bool showHidden);
    Task<bool> ExistsAsync(string path);
    Task<Stream> OpenReadAsync(string path);
    Task<Stream> OpenWriteAsync(string path);
    Task CreateDirectoryAsync(string path);
    Task DeleteAsync(string path, bool isDirectory);
    Task RenameAsync(string oldPath, string newPath);
}
