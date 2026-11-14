namespace ZenFiles.Models;

/// <summary>Lagret SSH/SFTP-tilkobling. Passord/passfrase lagres kryptert med Windows DPAPI (kun din brukerkonto).</summary>
public sealed class ConnectionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public bool UseKey { get; set; }
    public string KeyPath { get; set; } = "";
    public string? EncryptedSecret { get; set; }
    /// <summary>SHA256-fingeravtrykk for vertsnøkkelen (stoler på første tilkobling, avviser endringer).</summary>
    public string? HostKeySha256 { get; set; }
}
