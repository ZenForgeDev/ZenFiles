using Renci.SshNet;
using ZenFiles.Models;

namespace ZenFiles.Services;

public sealed class SftpFileSystem : IFileSystem, IDisposable
{
    readonly SftpClient _c;
    public ConnectionProfile Profile { get; }

    public bool IsRemote => true;
    public string DisplayName => Profile.Name;
    public string HomePath => _c.WorkingDirectory;

    SftpFileSystem(SftpClient client, ConnectionProfile profile)
    {
        _c = client;
        Profile = profile;
    }

    public static Task<SftpFileSystem> ConnectAsync(ConnectionProfile p, string? secret) => Task.Run(() =>
    {
        AuthenticationMethod auth = p.UseKey
            ? new PrivateKeyAuthenticationMethod(p.User,
                string.IsNullOrEmpty(secret) ? new PrivateKeyFile(p.KeyPath) : new PrivateKeyFile(p.KeyPath, secret))
            : new PasswordAuthenticationMethod(p.User, secret ?? "");

        var client = new SftpClient(new ConnectionInfo(p.Host, p.Port, p.User, auth));
        client.KeepAliveInterval = TimeSpan.FromSeconds(30);

        bool mismatch = false;
        client.HostKeyReceived += (_, e) =>
        {
            var fp = e.FingerPrintSHA256;
            if (string.IsNullOrEmpty(p.HostKeySha256))
            {
                p.HostKeySha256 = fp;           // første tilkobling: husk nøkkelen
                e.CanTrust = true;
            }
            else
            {
                e.CanTrust = p.HostKeySha256 == fp;
                mismatch = !e.CanTrust;
            }
        };

        try
        {
            client.Connect();
        }
        catch when (mismatch)
        {
            client.Dispose();
            throw new InvalidOperationException(
                "Serverens vertsnøkkel er endret siden sist. Det kan bety at noen avlytter tilkoblingen. " +
                "Hvis du vet at serveren er satt opp på nytt: rediger tilkoblingen og lagre den, så nullstilles nøkkelen.");
        }
        return new SftpFileSystem(client, p);
    });

    public string Combine(string dir, string name) =>
        dir == "/" ? "/" + name : dir.TrimEnd('/') + "/" + name;

    public string? Parent(string path)
    {
        if (path == "/" || string.IsNullOrEmpty(path)) return null;
        var t = path.TrimEnd('/');
        var i = t.LastIndexOf('/');
        return i <= 0 ? "/" : t[..i];
    }

    public Task<List<FileItem>> ListAsync(string path, bool showHidden) => Task.Run(() =>
    {
        var res = new List<FileItem>();
        foreach (var f in _c.ListDirectory(path))
        {
            if (f.Name is "." or "..") continue;
            if (!showHidden && f.Name.StartsWith('.')) continue;
            res.Add(new FileItem
            {
                Name = f.Name,
                FullPath = f.FullName,
                IsDirectory = f.IsDirectory,
                Size = f.IsDirectory ? 0 : f.Length,
                Modified = f.LastWriteTime
            });
        }
        return res;
    });

    public Task<bool> ExistsAsync(string path) => Task.Run(() => _c.Exists(path));

    public Task<Stream> OpenReadAsync(string path) => Task.Run<Stream>(() => _c.OpenRead(path));

    public Task<Stream> OpenWriteAsync(string path) => Task.Run<Stream>(() => _c.Create(path));

    public Task CreateDirectoryAsync(string path) => Task.Run(() =>
    {
        if (!_c.Exists(path)) _c.CreateDirectory(path);
    });

    public Task DeleteAsync(string path, bool isDirectory) => Task.Run(() =>
    {
        if (isDirectory) DeleteDir(path);
        else _c.DeleteFile(path);
    });

    void DeleteDir(string path)
    {
        foreach (var f in _c.ListDirectory(path))
        {
            if (f.Name is "." or "..") continue;
            if (f.IsDirectory) DeleteDir(f.FullName);
            else _c.DeleteFile(f.FullName);
        }
        _c.DeleteDirectory(path);
    }

    public Task RenameAsync(string oldPath, string newPath) => Task.Run(() => _c.RenameFile(oldPath, newPath));

    public void Dispose()
    {
        try { if (_c.IsConnected) _c.Disconnect(); } catch { }
        _c.Dispose();
    }
}
