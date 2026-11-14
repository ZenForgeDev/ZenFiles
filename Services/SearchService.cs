using System.Collections.Concurrent;
using System.Data.OleDb;

namespace ZenFiles.Services;

public readonly record struct SearchHit(string Path, string Name, bool IsDir, long Size, long Ticks);

public sealed record SearchOutcome(string Source, int Count, bool Limited);

/// <summary>
/// Dypt søk i lokale filer. Lag 1: Windows Search-indeksen (umiddelbart, ingen admin).
/// Lag 2: ZenFiles' egen indeks (parallell gjennomgang + FileSystemWatcher) når mappen ikke er indeksert av Windows.
/// Treff = alle ord i søket finnes i navnet (uavhengig av store/små bokstaver).
/// </summary>
public static class SearchService
{
    public const int MaxHits = 5000;

    public static async Task<SearchOutcome> RunAsync(string? scope, string query, Action<SearchHit> onHit,
        CancellationToken ct, bool forceOwnIndex = false)
    {
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return new("", 0, false);

        using var limitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int count = 0;
        bool limited = false;
        void Hit(SearchHit h)
        {
            var n = Interlocked.Increment(ref count);
            if (n > MaxHits) { limited = true; limitCts.Cancel(); return; }
            onHit(h);
        }

        string source;
        try
        {
            if (!forceOwnIndex && scope != null && await Task.Run(() => WindowsIndex.TrySearch(scope, tokens, Hit, limitCts.Token), limitCts.Token))
                source = "Windows-søkeindeksen";
            else
            {
                await FileIndex.SearchAsync(scope, tokens, Hit, limitCts.Token);
                source = "ZenFiles-indeksen";
            }
        }
        catch (OperationCanceledException) when (limited && !ct.IsCancellationRequested)
        {
            source = forceOwnIndex || scope == null ? "ZenFiles-indeksen" : "søkeindeks";
        }
        return new(source, Math.Min(count, MaxHits), limited);
    }

    internal static bool Matches(string name, string[] tokens)
    {
        foreach (var t in tokens)
            if (name.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0) return false;
        return true;
    }
}

// ───────── Lag 1: Windows Search ─────────

static class WindowsIndex
{
    const string Conn = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";

    /// <summary>Returnerer false hvis indeksen ikke kan brukes (da tar egen indeks over).</summary>
    public static bool TrySearch(string scope, string[] tokens, Action<SearchHit> onHit, CancellationToken ct)
    {
        foreach (var t in tokens)
            if (t.IndexOfAny(new[] { '%', '_', '[', ']' }) >= 0) return false;   // LIKE-jokertegn: la egen indeks ta det

        try
        {
            using var conn = new OleDbConnection(Conn);
            conn.Open();

            var url = "file:" + scope.Replace('\\', '/');
            var isRoot = scope.Length <= 3 && scope.EndsWith('\\');
            if (!isRoot) url = url.TrimEnd('/');
            url = url.Replace("'", "''");

            // Er mappen i det hele tatt indeksert? Ellers ville vi bare fått 0 treff uten å vite hvorfor.
            using (var probe = conn.CreateCommand())
            {
                probe.CommandText = $"SELECT TOP 1 System.ItemUrl FROM SystemIndex WHERE SCOPE='{url}'";
                probe.CommandTimeout = 15;
                using var pr = probe.ExecuteReader();
                if (!pr.Read()) return false;
            }

            var where = string.Join(" AND ", tokens.Select(t => $"System.ItemName LIKE '%{t.Replace("'", "''")}%'"));
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"SELECT TOP {SearchService.MaxHits + 1} System.ItemPathDisplay, System.ItemName, System.Size, System.DateModified, System.ItemType " +
                $"FROM SystemIndex WHERE SCOPE='{url}' AND System.ItemUrl LIKE 'file:%' AND {where}";
            cmd.CommandTimeout = 30;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (r.IsDBNull(0)) continue;
                var path = r.GetString(0);
                var name = r.IsDBNull(1) ? System.IO.Path.GetFileName(path) : r.GetString(1);
                long size = r.IsDBNull(2) ? 0 : Convert.ToInt64(r.GetValue(2));
                long ticks = r.IsDBNull(3) ? 0 : Convert.ToDateTime(r.GetValue(3)).Ticks;
                var isDir = !r.IsDBNull(4) && string.Equals(r.GetString(4), "Directory", StringComparison.OrdinalIgnoreCase);
                onHit(new SearchHit(path, name, isDir, size, ticks));
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }   // tjenesten av, ikke tilgjengelig, syntaksfeil osv.
    }
}

// ───────── Lag 2: egen indeks ─────────

static class FileIndex
{
    struct Entry { public string Name; public int Parent; public bool IsDir; public long Size; public long Ticks; }

    sealed class Snapshot : IDisposable
    {
        public string Root = "";
        public Entry[] Entries = Array.Empty<Entry>();
        public DateTime Built;
        public volatile bool Dirty;
        public FileSystemWatcher? Watcher;
        public void Dispose() { try { Watcher?.Dispose(); } catch { } }
    }

    static readonly Dictionary<string, Snapshot> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly object Gate = new();
    const int MaxRoots = 6;
    static readonly TimeSpan MaxStale = TimeSpan.FromSeconds(60);

    public static async Task SearchAsync(string? scope, string[] tokens, Action<SearchHit> onHit, CancellationToken ct)
    {
        if (scope != null) { await SearchRootAsync(scope, tokens, onHit, ct); return; }

        var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName);
        await Task.WhenAll(drives.Select(d => SearchRootAsync(d, tokens, onHit, ct)));
    }

    static string Norm(string p) => p.Length <= 3 ? p : p.TrimEnd('\\');

    static async Task SearchRootAsync(string scope, string[] tokens, Action<SearchHit> onHit, CancellationToken ct)
    {
        scope = Norm(scope);
        var snap = FindUsable(scope);
        if (snap != null)
        {
            await Task.Run(() => SearchSnapshot(snap, scope, tokens, onHit, ct), ct);
            return;
        }
        await BuildAndSearchAsync(scope, tokens, onHit, ct);
    }

    static Snapshot? FindUsable(string scope)
    {
        lock (Gate)
        {
            foreach (var s in Cache.Values)
            {
                var covers = scope.Equals(s.Root, StringComparison.OrdinalIgnoreCase) ||
                             scope.StartsWith(s.Root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
                if (!covers) continue;
                if (s.Dirty && DateTime.UtcNow - s.Built > MaxStale) continue;
                return s;
            }
        }
        return null;
    }

    static void SearchSnapshot(Snapshot s, string scope, string[] tokens, Action<SearchHit> onHit, CancellationToken ct)
    {
        var e = s.Entries;
        var filter = !scope.Equals(s.Root, StringComparison.OrdinalIgnoreCase);
        var prefix = scope.TrimEnd('\\') + "\\";
        Parallel.ForEach(Partitioner.Create(1, e.Length, Math.Max(1000, e.Length / 32)),
            new ParallelOptions { CancellationToken = ct }, range =>
        {
            for (int i = range.Item1; i < range.Item2; i++)
            {
                if (!SearchService.Matches(e[i].Name, tokens)) continue;
                var path = PathOf(e, i);
                if (filter && !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (e[i].IsDir ? !Directory.Exists(path) : !File.Exists(path)) continue;   // slettet siden indeksen ble bygd
                onHit(new SearchHit(path, e[i].Name, e[i].IsDir, e[i].Size, e[i].Ticks));
            }
        });
    }

    static string PathOf(Entry[] e, int i)
    {
        var parts = new Stack<string>();
        for (int k = i; k >= 0; k = e[k].Parent) parts.Push(e[k].Name);
        var root = parts.Pop();
        return parts.Count == 0 ? root : System.IO.Path.Combine(root, string.Join('\\', parts));
    }

    static async Task BuildAndSearchAsync(string root, string[] tokens, Action<SearchHit> onHit, CancellationToken ct)
    {
        var snap = new Snapshot { Root = root, Built = DateTime.UtcNow };
        try
        {
            snap.Watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024
            };
            snap.Watcher.Created += (_, _) => snap.Dirty = true;
            snap.Watcher.Deleted += (_, _) => snap.Dirty = true;
            snap.Watcher.Renamed += (_, _) => snap.Dirty = true;
            snap.Watcher.Error += (_, _) => snap.Dirty = true;
            snap.Watcher.EnableRaisingEvents = true;
        }
        catch { snap.Watcher?.Dispose(); snap.Watcher = null; snap.Dirty = true; }   // uten watcher: gjenbruk bare i 60 s

        var entries = new List<Entry> { new() { Name = root, Parent = -1, IsDir = true } };
        var stack = new ConcurrentStack<(string Path, int Idx)>();
        int pending = 1;
        stack.Push((root, 0));
        var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = 0 };

        void Worker()
        {
            var spin = new SpinWait();
            while (!ct.IsCancellationRequested)
            {
                if (!stack.TryPop(out var job))
                {
                    if (Volatile.Read(ref pending) == 0) return;
                    spin.SpinOnce(); continue;
                }
                try
                {
                    var found = new List<(string Name, bool Dir, long Size, long Ticks, bool Link)>();
                    foreach (var fi in new DirectoryInfo(job.Path).EnumerateFileSystemInfos("*", opts))
                    {
                        var dir = (fi.Attributes & FileAttributes.Directory) != 0;
                        found.Add((fi.Name, dir, dir ? 0 : ((FileInfo)fi).Length, fi.LastWriteTime.Ticks,
                                   (fi.Attributes & FileAttributes.ReparsePoint) != 0));
                    }
                    int first;
                    lock (entries)
                    {
                        first = entries.Count;
                        foreach (var f in found)
                            entries.Add(new Entry { Name = f.Name, Parent = job.Idx, IsDir = f.Dir, Size = f.Size, Ticks = f.Ticks });
                    }
                    for (int k = 0; k < found.Count; k++)
                    {
                        var f = found[k];
                        var full = System.IO.Path.Combine(job.Path, f.Name);
                        if (SearchService.Matches(f.Name, tokens)) onHit(new SearchHit(full, f.Name, f.Dir, f.Size, f.Ticks));
                        if (f.Dir && !f.Link)
                        {
                            Interlocked.Increment(ref pending);
                            stack.Push((full, first + k));
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch { /* mappe uten tilgang e.l. */ }
                finally { Interlocked.Decrement(ref pending); }
            }
        }

        var n = Math.Clamp(Environment.ProcessorCount, 2, 8);
        await Task.WhenAll(Enumerable.Range(0, n).Select(_ => Task.Run(Worker)));

        if (ct.IsCancellationRequested) { snap.Dispose(); ct.ThrowIfCancellationRequested(); return; }   // ufullstendig: kast

        snap.Entries = entries.ToArray();
        snap.Built = DateTime.UtcNow;
        lock (Gate)
        {
            if (Cache.TryGetValue(root, out var old)) old.Dispose();
            Cache[root] = snap;
            while (Cache.Count > MaxRoots)
            {
                var oldest = Cache.Values.OrderBy(x => x.Built).First();
                Cache.Remove(oldest.Root);
                oldest.Dispose();
            }
        }
    }
}
