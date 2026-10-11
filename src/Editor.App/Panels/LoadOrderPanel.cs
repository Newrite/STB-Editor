using Hexa.NET.ImGui;
using StbEditor.App.Ui;
using StbEditor.Data;
using StbEditor.Data.Raw;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>The load order as the engine sees it: index, flags, header, text encoding, source mod.</summary>
internal sealed class LoadOrderPanel : Panel
{
    private string _filter = string.Empty;
    private bool _showInactive;

    public override string Name => "Load Order";

    protected override void DrawContent(EditorApp app)
    {
        if (!RequireSession(app)) return;
        var ws = app.Session!.Workspace;

        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##filter", T("Filter plugins"), ref _filter, 256);
        ImGui.SameLine();
        ImGui.Checkbox(T("Show inactive"), ref _showInactive);
        ImGui.SameLine();
        ImGui.TextColored(Theme.TextDim,
            TF($"{ws.Active.Count} active · {ws.Active.Count(p => !p.IsLight)} full · {ws.Active.Count(p => p.IsLight)} light · header up to {ws.Game.MaxHeaderVersion:0.00}{(ws.Game.HasBees ? " (BEES)" : "")}"));

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable
            | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Hideable | ImGuiTableFlags.Reorderable;

        if (!ImGui.BeginTable("##plugins", 8, flags)) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(T("Index"));
        ImGui.TableSetupColumn(T("Plugin"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(T("Flags"));
        ImGui.TableSetupColumn(T("Header"));
        ImGui.TableSetupColumn(T("Records"));
        ImGui.TableSetupColumn(T("Text"));
        ImGui.TableSetupColumn(T("Masters"));
        ImGui.TableSetupColumn(T("Source"));
        ImGui.TableHeadersRow();

        IEnumerable<PluginInfo> rows = _showInactive ? ws.Plugins : ws.Active;
        foreach (var plugin in rows)
        {
            if (_filter.Length > 0 && !plugin.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)) continue;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.TextDim, plugin.IndexLabel);

            ImGui.TableNextColumn();
            if (plugin.Error is not null) ImGui.TextColored(Theme.Error, plugin.Name);
            else if (!plugin.IsActive) ImGui.TextColored(Theme.TextDim, plugin.Name);
            else if (plugin.Skipped) ImGui.TextColored(Theme.TextDim, plugin.Name + T("  (not loaded)"));
            else ImGui.Text(plugin.Name);
            if (plugin.Error is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(plugin.Error);

            ImGui.TableNextColumn();
            var flagsText = (plugin.Header?.IsMasterFlagged == true ? "ESM " : "") + (plugin.IsLight ? "ESL " : "") + (plugin.Header?.IsLocalized == true ? "LOC" : "");
            ImGui.Text(flagsText);

            ImGui.TableNextColumn();
            if (plugin.Header is { } header)
            {
                var tooHigh = header.Version > ws.Game.MaxHeaderVersion + 0.001f;
                if (tooHigh) ImGui.TextColored(Theme.Warning, $"{header.Version:0.00}");
                else ImGui.Text($"{header.Version:0.00}");
                if (tooHigh && ImGui.IsItemHovered()) ImGui.SetTooltip(T("Header newer than this game reads without Backported Extended ESL Support"));
            }

            ImGui.TableNextColumn();
            ImGui.Text(plugin.Header?.RecordCount.ToString("N0") ?? "");

            ImGui.TableNextColumn();
            var text = plugin.StringEncoding switch
            {
                PluginStringEncoding.Localized => "strings",
                PluginStringEncoding.Ascii => "ascii",
                PluginStringEncoding.Utf8 => "UTF-8",
                PluginStringEncoding.Ansi => "ANSI",
                _ => "",
            };
            if (plugin.MixedEncoding) ImGui.TextColored(Theme.Warning, text + " +mixed");
            else ImGui.Text(text);

            ImGui.TableNextColumn();
            ImGui.Text(plugin.Header?.Masters.Count.ToString() ?? "");
            if (plugin.Header is { Masters.Count: > 0 } h && ImGui.IsItemHovered()) ImGui.SetTooltip(string.Join("\n", h.Masters));

            ImGui.TableNextColumn();
            ImGui.TextColored(Theme.TextDim, plugin.Location.Source);
        }

        ImGui.EndTable();
    }
}
