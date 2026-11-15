using System.Net.Http;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace ZenFiles.Services;

public sealed record UpdateInfo(string Version, string Url);

/// <summary>
/// Sjekker GitHub etter nyere versjon av ZenFiles. Bruker det åpne API-et (ingen innlogging) og laster aldri ned eller
/// installerer noe selv: appen varsler, og «Last ned» åpner release-siden i nettleseren.
/// </summary>
public static class UpdateChecker
{
    const string Api = "https://api.github.com/repos/ZenForgeDev/ZenFiles/releases?per_page=10";

    public static event Action? Changed;
    public static UpdateInfo? Available { get; private set; }

    /// <summary>Versjonen brukeren har trykket «Senere» på. Varselet vises ikke igjen før appen startes på nytt.</summary>
    public static string? Dismissed { get; private set; }

    static DispatcherQueue? _dq;
    static bool _started;
    static readonly SemaphoreSlim _gate = new(1, 1);

    public static string CurrentVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "";
            v = v.Split('+')[0];
            return string.IsNullOrEmpty(v) ? "0.0.0" : v;
        }
    }

    /// <summary>Starter sjekk litt etter oppstart og så hver 12. time så lenge innstillingen er på.</summary>
    public static void StartBackground(DispatcherQueue dq)
    {
        _dq = dq;
        if (_started) return;
        _started = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            while (true)
            {
                if (AppSettings.UpdateCheck) await CheckAsync();
                await Task.Delay(TimeSpan.FromHours(12));
            }
        });
    }

    public static void Dismiss()
    {
        Dismissed = Available?.Version;
        Raise();
    }

    /// <summary>Slått av i innstillinger: fjern et eventuelt varsel.</summary>
    public static void Clear()
    {
        Available = null;
        Raise();
    }

    static void Raise() => _dq?.TryEnqueue(() => Changed?.Invoke());

    static bool TryParse(string tag, out Version v)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        int cut = t.IndexOfAny(new[] { '-', '+' });
        if (cut >= 0) t = t[..cut];
        return Version.TryParse(t, out v!);
    }

    /// <summary>Spør GitHub. Returnerer en kort melding til innstillingene; selve varselet settes via Changed.</summary>
    public static async Task<string> CheckAsync()
    {
        await _gate.WaitAsync();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ZenFiles/" + CurrentVersion);
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await http.GetStringAsync(Api);

            if (!TryParse(CurrentVersion, out var current)) current = new Version(0, 0, 0);
            Version? best = null; string tagBest = "", urlBest = "";
            using var doc = JsonDocument.Parse(json);
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                var tag = r.GetProperty("tag_name").GetString() ?? "";
                if (!TryParse(tag, out var v)) continue;
                if (best == null || v > best)
                {
                    best = v; tagBest = tag.TrimStart('v', 'V');
                    urlBest = r.GetProperty("html_url").GetString() ?? "";
                }
            }

            if (best != null && best > current && urlBest.StartsWith("https://github.com/", StringComparison.Ordinal))
            {
                Available = new UpdateInfo(tagBest, urlBest);
                Raise();
                return $"Ny versjon {tagBest} er tilgjengelig.";
            }
            Available = null;
            Raise();
            return $"Du har siste versjon ({CurrentVersion}).";
        }
        catch (Exception ex)
        {
            return "Kunne ikke sjekke: " + ex.Message;
        }
        finally { _gate.Release(); }
    }
}
