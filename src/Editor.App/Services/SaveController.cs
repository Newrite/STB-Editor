using StbEditor.Core;

namespace StbEditor.App.Services;

/// <summary>
/// Save in three steps: prepare (background: write + verify a temp file), confirm when the
/// writer would change untouched records, commit (close the session's file handles, back up,
/// swap the file in, reload with the same view).
/// </summary>
internal sealed class SaveController
{
    private Task<SaveResult>? _preparing;
    private readonly HashSet<string> _lossyConfirmed = new(StringComparer.OrdinalIgnoreCase);

    public SaveResult? AwaitingConfirmation { get; private set; }
    public string? LastError { get; private set; }
    public bool IsBusy => _preparing is not null;

    public void Start(EditorApp app, ActivePlugin active)
    {
        if (_preparing is not null || app.Session is not { } session) return;

        LastError = null;
        var writer = new PluginWriter(session.Workspace, session.Catalog);
        var document = app.Document;
        Log.Info($"saving {document.ChangedCount} reference change(s) and {document.Drafts.Count} record edit(s) (new, changed, removed) into {active.Path}");
        _preparing = Task.Run(() => writer.Prepare(active, document));
    }

    /// <summary>Called every frame; finishes a save whose preparation completed.</summary>
    public void Poll(EditorApp app)
    {
        if (_preparing is null || !_preparing.IsCompleted) return;

        var result = _preparing.IsCompletedSuccessfully
            ? _preparing.Result
            : new SaveResult { TargetPath = "", Error = _preparing.Exception?.GetBaseException().Message };
        _preparing = null;

        foreach (var warning in result.Warnings) Log.Warn($"save: {warning}");

        if (!result.Success)
        {
            LastError = result.Error;
            Log.Error($"save failed: {result.Error}");
            return;
        }

        if (result.LossyRecords.Count > 0 && !_lossyConfirmed.Contains(result.TargetPath))
        {
            foreach (var line in result.LossyRecords.Take(20)) Log.Warn($"save check: {line}");
            AwaitingConfirmation = result;
            return;
        }

        Commit(app, result);
    }

    public void Confirm(EditorApp app)
    {
        if (AwaitingConfirmation is not { } result) return;
        _lossyConfirmed.Add(result.TargetPath);
        AwaitingConfirmation = null;
        Commit(app, result);
    }

    public void ClearError() => LastError = null;

    public void Cancel()
    {
        if (AwaitingConfirmation is { TempPath: { } temp })
        {
            try { File.Delete(temp); } catch { }
        }
        AwaitingConfirmation = null;
    }

    private static void Commit(EditorApp app, SaveResult result)
    {
        var view = app.RenderWindow.CaptureView();
        var game = app.Session!.Game;

        // The workspace keeps every plugin open (Mutagen overlays): close them before replacing.
        app.CloseSession();

        try
        {
            if (File.Exists(result.TargetPath))
            {
                var backup = Backup(result.TargetPath);
                Log.Info($"backup: {backup}");
            }

            File.Move(result.TempPath!, result.TargetPath, overwrite: true);
            Log.Info($"saved {result.TargetPath}: {result.Overridden} changed, {result.Created} new, {result.Disabled} disabled, {result.Removed} removed");
            // Quests that start with the game need the plugin's SEQ file (as the CK writes it on save).
            try
            {
                if (StbEditor.Data.Raw.SeqFile.Write(result.TargetPath) is { } seq) Log.Info($"SEQ: {seq.Path} ({seq.Count} start-game quest(s))");
            }
            catch (Exception ex) { Log.Warn($"SEQ file not written: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            Log.Error($"could not replace {result.TargetPath}: {ex.Message} (the new file is {result.TempPath})");
        }

        app.AfterSave(game, view, result.TargetPath);
    }

    /// <summary>Copies the plugin to %LOCALAPPDATA%\STBEditor\backups\&lt;plugin&gt;\, keeping the last 30.</summary>
    private static string Backup(string path)
    {
        var name = Path.GetFileName(path);
        var folder = Path.Combine(AppPaths.BackupFolder, name);
        Directory.CreateDirectory(folder);

        var target = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss}_{name}");
        File.Copy(path, target, overwrite: true);

        foreach (var old in Directory.GetFiles(folder).OrderByDescending(f => f).Skip(30))
        {
            try { File.Delete(old); } catch { }
        }

        return target;
    }
}
