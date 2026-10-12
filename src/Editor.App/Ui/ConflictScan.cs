using System.Diagnostics;
using Mutagen.Bethesda.Plugins;
using StbEditor.App.Panels;

namespace StbEditor.App.Ui;

/// <summary>
/// Finds records with real conflicts (xEdit's «Apply Filter to show Conflicts»): every version of
/// the record compared field by field, and a conflict is a plugin's change the winner loses. Runs
/// a few milliseconds per frame so the window never stalls; results last for the loaded session.
/// </summary>
internal sealed class ConflictScan
{
    private readonly Dictionary<FormKey, bool> _results = [];
    private readonly Queue<FormKey> _pending = [];
    private readonly HashSet<FormKey> _queued = [];

    public int Pending => _pending.Count;
    public int Checked { get; private set; }
    public bool IsRunning => _pending.Count > 0;

    /// <summary>Bumped when a scan finds something (lists showing conflicts rebuild).</summary>
    public int Version { get; private set; }

    public void Reset()
    {
        _results.Clear();
        _pending.Clear();
        _queued.Clear();
        Checked = 0;
        Version++;
    }

    /// <summary>True / false once checked; null while not yet.</summary>
    public bool? IsConflict(FormKey key) => _results.TryGetValue(key, out var conflict) ? conflict : null;

    /// <summary>Queues records to check; only ones with three or more versions can conflict (master, a loser, the winner).</summary>
    public void Request(IEnumerable<StbEditor.Data.Catalog.CatalogRecord> records)
    {
        foreach (var record in records)
        {
            if (record.Plugins.Count < 3 || record.IsTransient) continue;
            if (_results.ContainsKey(record.FormKey) || !_queued.Add(record.FormKey)) continue;
            _pending.Enqueue(record.FormKey);
        }
    }

    /// <summary>Checks queued records for about <paramref name="budgetMs"/> milliseconds.</summary>
    public void Step(EditorApp app, double budgetMs = 6)
    {
        if (_pending.Count == 0) return;
        var clock = Stopwatch.StartNew();
        var found = false;
        while (_pending.Count > 0 && clock.Elapsed.TotalMilliseconds < budgetMs)
        {
            var key = _pending.Dequeue();
            _queued.Remove(key);
            var conflict = false;
            try
            {
                var versions = CompareWindows.Versions(app, key);
                if (versions.Count >= 3)
                {
                    var records = versions.Select(v => (object?)v.Record).Reverse().ToList();
                    conflict = RecordCompare.Build(app, records, versions: true, showHeader: false).State == CompareState.Conflict;
                }
            }
            catch (Exception ex)
            {
                StbEditor.App.Services.Log.Warn($"conflict scan: {key}: {ex.Message}");
            }
            _results[key] = conflict;
            Checked++;
            found |= conflict;
        }
        if (found || _pending.Count == 0) Version++;
    }
}
