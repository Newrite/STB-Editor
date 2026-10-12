using System.Diagnostics;
using StbEditor.Assets;
using StbEditor.Data;
using StbEditor.Data.Catalog;

namespace StbEditor.App.Services;

/// <summary>Everything loaded for one game + profile.</summary>
internal sealed class EditorSession : IDisposable
{
    public required GameInstall Game { get; init; }
    public required Workspace Workspace { get; init; }
    public required RecordCatalog Catalog { get; init; }
    public required AssetFileSystem Assets { get; init; }
    public required TimeSpan LoadTime { get; init; }

    private Task<DoorIndex>? _doors;

    /// <summary>Teleport door → its cell; built in the background the first time it is asked for.</summary>
    public Task<DoorIndex> Doors => _doors ??= System.Threading.Tasks.Task.Run(() =>
    {
        var clock = Stopwatch.StartNew();
        var index = DoorIndex.Build(Workspace);
        Log.Info($"door index: {index.Count:N0} teleport doors in {clock.Elapsed.TotalSeconds:0.0} s");
        return index;
    });

    private Task<DialogueIndex>? _dialogue;

    /// <summary>Quest → its topics and branches; built in the background the first time a quest's Dialogue tab is shown.</summary>
    public Task<DialogueIndex> Dialogue => _dialogue ??= System.Threading.Tasks.Task.Run(() =>
    {
        var index = DialogueIndex.Build(Workspace, Catalog, _closing.Token);
        Log.Info($"dialogue index: {index.TopicCount:N0} quest topics in {index.BuildTime.TotalSeconds:0.0} s");
        return index;
    }, _closing.Token);

    private Task<UseIndex>? _uses;
    private readonly CancellationTokenSource _closing = new();

    /// <summary>Use Info (who points at a record); built in the background after loading, from a per-plugin disk cache when it can.</summary>
    public Task<UseIndex> Uses => _uses ??= System.Threading.Tasks.Task.Run(() =>
    {
        var folder = Path.Combine(AppPaths.Root, "cache", "uses");
        var index = UseIndex.Build(Workspace, folder, cancel: _closing.Token);
        Log.Info($"use index: {index.Count:N0} links in {index.BuildTime.TotalSeconds:0.0} s ({index.CachedPlugins} plugins from cache), {Log.Memory()}");
        return index;
    }, _closing.Token);

    /// <summary>
    /// Stops the background index first: it reads plugin files (a save is about to replace one) and
    /// must not cache what it scans once the plugins are closed.
    /// </summary>
    public void Dispose()
    {
        _closing.Cancel();
        try { _uses?.Wait(TimeSpan.FromSeconds(30)); _dialogue?.Wait(TimeSpan.FromSeconds(30)); }
        catch (AggregateException) { } // cancelled or failed: either way it is done
        Workspace.Dispose();
    }
}

/// <summary>Opens a session on a worker thread; the UI polls <see cref="Status"/> and <see cref="Result"/>.</summary>
internal sealed class SessionLoader
{
    private readonly CancellationTokenSource _cancel = new();
    private volatile string _status = "Starting";

    public Task<EditorSession> Task { get; }
    public string Status => _status;
    public bool IsRunning => !Task.IsCompleted;
    public EditorSession? Result => Task.IsCompletedSuccessfully ? Task.Result : null;
    public Exception? Error => Task.Exception?.GetBaseException();

    public SessionLoader(GameInstall game, string? extraPlugin = null, IReadOnlyCollection<string>? only = null)
    {
        Task = System.Threading.Tasks.Task.Run(() => Load(game, extraPlugin, only, _cancel.Token));
    }

    public void Cancel() => _cancel.Cancel();

    private EditorSession Load(GameInstall game, string? extraPlugin, IReadOnlyCollection<string>? only, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        var progress = new Progress(this);

        Log.Info($"opening {game.DisplayName} at {game.GameFolder}, MO2 {(game.Mo2 is { } m ? $"{m.Folder} / {m.ProfileName}" : "none")}, language {game.Language}");

        var workspace = Workspace.Open(game, progress, cancel, extraPlugin, only);
        try
        {
            foreach (var plugin in workspace.Plugins.Where(p => p.Error is not null))
                Log.Warn($"{plugin.Name}: {plugin.Error}");
            foreach (var plugin in workspace.Active.Where(p => p.MixedEncoding))
                Log.Info($"{plugin.Name}: mixed UTF-8 / code-page strings, read per string");

            var catalog = RecordCatalog.Build(workspace, AppPaths.CatalogCache, progress, cancel);
            foreach (var error in catalog.Errors) Log.Warn($"catalog: {error}");
            Log.Info($"catalog: {catalog.ByKey.Count} records in {catalog.BuildTime.TotalSeconds:0.0} s ({catalog.CachedPlugins} plugins cached), {Log.Memory()}");

            var assets = AssetFileSystem.Build(workspace, progress, cancel);
            foreach (var error in assets.Errors) Log.Warn($"assets: {error}");
            Log.Info($"assets: {assets.LooseCount} loose files, {assets.ArchivedCount} files in {assets.LoadedArchives.Count} archives");

            Log.Info($"loaded {workspace.Active.Count} plugins in {clock.Elapsed.TotalSeconds:0.0} s");
            return new EditorSession
            {
                Game = game,
                Workspace = workspace,
                Catalog = catalog,
                Assets = assets,
                LoadTime = clock.Elapsed,
            };
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private sealed class Progress(SessionLoader owner) : IProgress<string>
    {
        public void Report(string value) => owner._status = value;
    }
}
