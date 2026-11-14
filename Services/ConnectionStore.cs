using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZenFiles.Models;

namespace ZenFiles.Services;

public static class ConnectionStore
{
    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenFiles", "connections.json");

    public static List<ConnectionProfile> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            return JsonSerializer.Deserialize<List<ConnectionProfile>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { return new(); }
    }

    public static void Save(List<ConnectionProfile> profiles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
        }
        catch { return null; }
    }
}
