using System.Diagnostics;
using System.Numerics;
using Hexa.NET.ImGui;
using StbEditor.App.Ui;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// A framework config (SPID, KID, BOS, OAR…) as text, the line that names the record highlighted and scrolled to; it opens
/// in the default program or in Explorer when it is a loose file (MO2 mod folder).
/// </summary>
internal sealed class ConfigFileWindow
{
    private bool _open;
    private string _path = "";
    private int _line;
    private string[] _lines = [];
    private string _where = "";
    private string? _loose;
    private bool _scroll;

    public void Open(EditorApp app, string assetPath, int line)
    {
        if (app.Session is not { } session) return;
        _path = assetPath;
        _line = line;
        var bytes = session.Assets.ReadAllBytes(assetPath);
        _lines = bytes is null ? [T("(the file could not be read)")] : System.Text.Encoding.UTF8.GetString(bytes).TrimStart('﻿').Replace("\r\n", "\n").Split('\n');
        var source = session.Assets.Find(assetPath);
        _loose = source?.LoosePath;
        _where = source is null ? "?" : source.InArchive ? TF($"{source.Container}, in {Path.GetFileName(source.ArchivePath)}") : source.Container;
        _open = true;
        _scroll = true;
    }

    public void Draw(EditorApp app)
    {
        if (!_open) return;
        ImGui.SetNextWindowSize(app.Scaled(820, 560), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(T("Config file") + "###configfile", ref _open, ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.TextUnformatted(_path);
            ImGui.TextColored(Theme.TextDim, TF($"{_where} · line {_line}"));
            ImGui.BeginDisabled(_loose is null || !File.Exists(_loose));
            if (ImGui.SmallButton(T("Open in the default program"))) Shell(_loose!, null);
            ImGui.SameLine();
            if (ImGui.SmallButton(T("Show in Explorer"))) Shell("explorer.exe", $"/select,\"{_loose}\"");
            ImGui.EndDisabled();
            if (_loose is null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(T("The file is inside an archive (BSA)"));
            ImGui.SameLine();
            if (ImGui.SmallButton(T("Copy path"))) ImGui.SetClipboardText(_loose ?? _path);

            if (ImGui.BeginChild("##configtext", Vector2.Zero, ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar))
            {
                var height = ImGui.GetTextLineHeightWithSpacing();
                if (_scroll) { ImGui.SetScrollY(Math.Max(0, (_line - 1) * height - ImGui.GetWindowHeight() / 3)); _scroll = false; }
                var clipper = new ImGuiListClipper();
                clipper.Begin(_lines.Length);
                while (clipper.Step())
                    for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    {
                        var current = i + 1 == _line;
                        if (current)
                        {
                            var at = ImGui.GetCursorScreenPos();
                            ImGui.GetWindowDrawList().AddRectFilled(at, at + new Vector2(ImGui.GetContentRegionAvail().X + ImGui.GetScrollX(), height), ImGui.GetColorU32(Theme.Alpha(Theme.Accent, 0.25f)));
                        }
                        ImGui.TextColored(Theme.TextDim, $"{i + 1,5}");
                        ImGui.SameLine();
                        ImGui.TextColored(current ? Theme.Accent : Theme.Text, _lines[i]);
                    }
                clipper.End();
            }
            ImGui.EndChild();
        }
        ImGui.End();
    }

    private static void Shell(string file, string? arguments)
    {
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, Arguments = arguments ?? "" }); }
        catch (Exception e) { Services.Log.Warn($"open {file}: {e.Message}"); }
    }
}
