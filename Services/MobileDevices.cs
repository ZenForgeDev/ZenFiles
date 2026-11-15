using MediaDevices;
using Microsoft.UI.Dispatching;

namespace ZenFiles.Services;

/// <summary>En tilkoblet enhet slik den vises i sidepanelet.</summary>
public sealed record PhoneInfo(string Id, string Name, string Kind);

/// <summary>
/// Holder oversikt over mobiler og nettbrett koblet til med USB (Windows Portable Devices). Biblioteket har ingen
/// hendelse for tilkobling, så vi spør Windows hvert tredje sekund mens funksjonen er slått på.
/// </summary>
public static class MobileDevices
{
    public static event Action? Changed;
    public static IReadOnlyList<PhoneInfo> Devices { get; private set; } = Array.Empty<PhoneInfo>();

    static DispatcherQueue? _dq;
    static CancellationTokenSource? _cts;

    public static bool Enabled => AppSettings.MobileDevices;

    /// <summary>Kalles ved oppstart (og når innstillingen endres). Starter eller stopper overvåkingen.</summary>
    public static void Apply(DispatcherQueue dq)
    {
        _dq = dq;
        if (AppSettings.MobileDevices) Start();
        else Stop();
    }

    static void Start()
    {
        if (_cts != null) return;
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var list = Scan();
                if (list != null && !Same(list, Devices))
                {
                    Devices = list;
                    _dq?.TryEnqueue(() => Changed?.Invoke());
                }
                try { await Task.Delay(3000, ct); } catch { break; }
            }
        }, ct);
    }

    static void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        if (Devices.Count > 0)
        {
            Devices = Array.Empty<PhoneInfo>();
        }
        _dq?.TryEnqueue(() => Changed?.Invoke());
    }

    static bool Same(IReadOnlyList<PhoneInfo> a, IReadOnlyList<PhoneInfo> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First == p.Second);

    /// <summary>Null hvis Windows ikke svarte (da beholder vi forrige liste i stedet for å blinke).</summary>
    static List<PhoneInfo>? Scan()
    {
        try
        {
            var res = new List<PhoneInfo>();
            var devices = MediaDeviceManager.Instance.GetDevices();
            if (devices == null) return res;
            foreach (var d in devices)
            {
                var name = !string.IsNullOrWhiteSpace(d.FriendlyName) ? d.FriendlyName!
                         : !string.IsNullOrWhiteSpace(d.Description) ? d.Description! : "Enhet";
                var apple = (d.Manufacturer ?? "").Contains("Apple", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("iPad", StringComparison.OrdinalIgnoreCase);
                res.Add(new PhoneInfo(d.DeviceId, name, apple ? "Apple" : "Android"));
            }
            return res;
        }
        catch { return null; }
    }

    /// <summary>Kobler til enheten og gir et filsystem. Kaster med en forståelig melding hvis det feiler.</summary>
    public static Task<PhoneFileSystem> OpenAsync(string deviceId) => Task.Run(() =>
    {
        var dev = MediaDeviceManager.Instance.GetDevices()?.FirstOrDefault(d => d.DeviceId == deviceId)
                  ?? throw new InvalidOperationException("Fant ikke enheten. Er den fortsatt koblet til?");
        var name = !string.IsNullOrWhiteSpace(dev.FriendlyName) ? dev.FriendlyName! : dev.Description ?? "Enhet";
        dev.Connect();
        return new PhoneFileSystem(dev, name);
    });
}
