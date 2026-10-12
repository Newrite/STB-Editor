using System.Numerics;
using Hexa.NET.ImGui;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using StbEditor.Data;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// Papyrus source of a script (CK: Edit Source / New Script): edited here, saved next to the active
/// file (its mod's Scripts\Source) and compiled into its Scripts folder; a compiled script is seen by
/// the Scripts tab at once. A script from another mod is saved as a copy in the active file's mod.
/// </summary>
internal sealed class ScriptSourceWindows
{
    private sealed class Entry
    {
        public required int Id { get; init; }
        public required string Name { get; init; }
        /// <summary>Where the source was read from (null: a new script).</summary>
        public string? Origin { get; set; }
        public string Text = string.Empty;
        public string Saved { get; set; } = string.Empty;
        public Task<PapyrusResult>? Compiling { get; set; }
        public PapyrusResult? Result { get; set; }
        /// <summary>Run once after the first successful compile (attach a new script to its record).</summary>
        public Action? OnCompiled { get; set; }
        public bool Focus { get; set; } = true;
    }

    private readonly List<Entry> _open = [];
    private int _next;

    public bool IsBusy => _open.Any(e => e.Compiling is not null);

    /// <summary>Autotests: the newest window's last compile.</summary>
    public PapyrusResult? LastResult => _open.LastOrDefault()?.Result;

    public static string DefaultExtends(string signature) => signature switch
    {
        "QUST" => "Quest",
        "NPC_" or "ACHR" => "Actor",
        "MGEF" => "ActiveMagicEffect",
        "PERK" => "Perk",
        "PACK" => "Package",
        "SCEN" => "Scene",
        "INFO" => "TopicInfo",
        _ => "ObjectReference",
    };

    /// <summary>Opens a script's source; a script with no source starts as "Scriptname X extends Y" when <paramref name="extends"/> is given.</summary>
    public void Open(EditorApp app, string name, string? extends = null, Action? onCompiled = null)
    {
        if (_open.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            existing.Focus = true;
            existing.OnCompiled ??= onCompiled;
            return;
        }
        var entry = new Entry { Id = _next++, Name = name, OnCompiled = onCompiled };
        var path = OwnSource(app, name) is { } own && File.Exists(own) ? own : app.Papyrus?.SourcePath(name);
        if (path is not null)
        {
            entry.Origin = path;
            entry.Text = entry.Saved = File.ReadAllText(path);
        }
        else if (extends is not null)
        {
            entry.Text = $"Scriptname {name} extends {extends}\r\n\r\n";
        }
        _open.Add(entry);
    }

    /// <summary>Where this script's source goes: the active file's mod (null without an active file).</summary>
    private static string? OwnSource(EditorApp app, string name) =>
        app.ActivePlugin is { } active && app.Papyrus is { } papyrus ? Path.Combine(papyrus.OutputFor(active.Path).Sources, name + ".psc") : null;

    public void CloseAll() => _open.Clear();

    /// <summary>Autotests: type into the newest window and press "Save and compile".</summary>
    public void CompileForTest(EditorApp app, string text)
    {
        if (_open.LastOrDefault() is not { } entry || OwnSource(app, entry.Name) is not { } own) return;
        entry.Text = text;
        StartCompile(app, entry, own);
    }

    public void Draw(EditorApp app)
    {
        for (var i = 0; i < _open.Count; i++)
        {
            var entry = _open[i];
            Poll(app, entry);
            ImGui.SetNextWindowSize(app.Scaled(760, 620), ImGuiCond.FirstUseEver);
            if (entry.Focus) { ImGui.SetNextWindowFocus(); entry.Focus = false; }
            var open = true;
            var dirty = entry.Text != entry.Saved ? " *" : "";
            if (ImGui.Begin($"{TF($"Script: {entry.Name}")}{dirty}###script{entry.Id}", ref open, ImGuiWindowFlags.NoSavedSettings))
                DrawEntry(app, entry);
            ImGui.End();
            if (!open) _open.RemoveAt(i--);
        }
    }

    private static void DrawEntry(EditorApp app, Entry entry)
    {
        var own = OwnSource(app, entry.Name);
        var problem = app.Papyrus?.Problem;
        var busy = entry.Compiling is not null;

        ImGui.BeginDisabled(own is null || busy);
        if (ImGui.Button(T("Save"))) Save(entry, own!);
        ImGui.SameLine();
        ImGui.BeginDisabled(problem is not null);
        if (ImGui.Button(busy ? T("compiling…") : T("Save and compile"))) StartCompile(app, entry, own!);
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (own is null) ImGui.TextColored(Theme.Warning, T("Choose the active file first: scripts are saved next to it"));
        else if (entry.Origin is not null && !string.Equals(Path.GetFullPath(entry.Origin), Path.GetFullPath(own), StringComparison.OrdinalIgnoreCase))
        {
            ImGui.TextColored(Theme.Warning, T("a copy is saved into the active file's folder"));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(TF($"Read from {entry.Origin}\nSaved to {own}\nThe copy overrides the original where its mod wins (MO2 priority)."));
        }
        else ImGui.TextColored(Theme.TextDim, own);
        if (problem is not null) ImGui.TextColored(Theme.Warning, TF($"Papyrus: {problem}"));
        if (entry.Origin is null && entry.Text.Length == 0)
            ImGui.TextColored(Theme.Warning, T("No source of this script in the load order (only the compiled .pex): put the sources into Scripts\\Source to edit it."));

        var errors = entry.Result is { Success: false } failed ? failed.Errors : [];
        var errorHeight = entry.Result is null ? 0 : Math.Min(errors.Count + 1, 8) * ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        ImGui.InputTextMultiline("##source", ref entry.Text, 1 << 20, new Vector2(-1, -errorHeight), ImGuiInputTextFlags.AllowTabInput);

        if (entry.Result is { } result)
        {
            if (result.Success) ImGui.TextColored(Theme.TextDim, TF($"Compiled {result.PexPath}"));
            else
            {
                ImGui.TextColored(Theme.Error, TF($"Compile failed: {errors.Count} error(s)"));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(result.Output.Length > 3000 ? result.Output[..3000] + "…" : result.Output);
                foreach (var error in errors.Take(7))
                    ImGui.TextColored(Theme.Error, $"{Path.GetFileName(error.File)}({error.Line},{error.Column}): {error.Message}");
            }
        }
    }

    private static void Save(Entry entry, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, entry.Text);
        entry.Saved = entry.Text;
        entry.Origin = path;
        Log.Info($"saved {path}");
    }

    private static void StartCompile(EditorApp app, Entry entry, string path)
    {
        if (app.Papyrus is not { } papyrus || app.ActivePlugin is not { } active) return;
        Save(entry, path);
        var scripts = papyrus.OutputFor(active.Path).Scripts;
        entry.Result = null;
        entry.Compiling = Task.Run(() => papyrus.Compile(path, scripts));
    }

    private static void Poll(EditorApp app, Entry entry)
    {
        if (entry.Compiling is not { IsCompleted: true } task) return;
        entry.Compiling = null;
        entry.Result = task.IsCompletedSuccessfully ? task.Result : new PapyrusResult(false, task.Exception?.GetBaseException().Message ?? "failed", [], null);
        if (!entry.Result.Success)
        {
            Log.Warn($"{entry.Name}: compile failed, {entry.Result.Errors.Count} error(s)");
            return;
        }
        Log.Info($"{entry.Name}: compiled {entry.Result.PexPath}");
        if (app.Session is { } session && entry.Result.PexPath is { } pex) session.Assets.AddLoose($@"scripts\{entry.Name}.pex", pex, T("compiled"));
        ScriptsEditor.Library?.Forget(entry.Name);
        var attach = entry.OnCompiled;
        entry.OnCompiled = null;
        attach?.Invoke();
    }
}
