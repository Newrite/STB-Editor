using System.Numerics;
using Hexa.NET.ImGui;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>A dockable editor window.</summary>
internal abstract class Panel
{
    /// <summary>Visible title; also the ImGui window id, so it must stay stable.</summary>
    public abstract string Name { get; }

    public bool IsOpen { get; set; } = true;

    public void Draw(EditorApp app)
    {
        var open = IsOpen;
        ImGui.SetNextWindowSize(new Vector2(500, 400), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(Title(Name), ref open, WindowFlags))
            DrawContent(app);
        ImGui.End();
        IsOpen = open;
    }

    protected virtual ImGuiWindowFlags WindowFlags => ImGuiWindowFlags.None;

    protected abstract void DrawContent(EditorApp app);

    public virtual void OnSessionChanged(EditorApp app) { }

    /// <summary>Placeholder for panels that need loaded data.</summary>
    protected static bool RequireSession(EditorApp app)
    {
        if (app.Session is not null) return true;

        var text = app.Loader is { } loader ? loader.Status + "..." : T("No data loaded. File → Data...");
        var size = ImGui.CalcTextSize(text);
        var avail = ImGui.GetContentRegionAvail();
        ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(Math.Max(0, (avail.X - size.X) / 2), Math.Max(0, avail.Y / 2 - size.Y)));
        ImGui.TextColored(Theme.TextDim, text);
        return false;
    }
}

internal static class StatusBar
{
    public static void Draw(EditorApp app)
    {
        var height = ImGui.GetFrameHeight();
        var flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.MenuBar;

        if (ImGuiP.BeginViewportSideBar("##StatusBar", ImGui.GetMainViewport(), ImGuiDir.Down, height, flags))
        {
            if (ImGui.BeginMenuBar())
            {
                if (app.Loader is { } loader)
                {
                    ImGui.TextColored(Theme.Accent, Spinner());
                    ImGui.Text(loader.Status);
                }
                else if (app.Session is { } session)
                {
                    ImGui.TextColored(Theme.Good, T("Ready"));
                    ImGui.TextColored(Theme.TextDim,
                        TF($"{session.Catalog.ByKey.Count:N0} records · {session.Assets.ArchivedCount + session.Assets.LooseCount:N0} assets · loaded in {session.LoadTime.TotalSeconds:0.0} s"));
                    if (app.Saver.IsBusy) ImGui.TextColored(Theme.Warning, T("· saving…"));
                    else if (app.ActivePlugin is { } active)
                        ImGui.TextColored(Theme.Text, TF($"· Active file: {active.Name}{(active.IsNew ? " (new)" : "")}"));
                    else
                        ImGui.TextColored(Theme.TextDim, T("· Active file: none (Ctrl+S asks)"));
                    if (app.Document.PendingCount > 0)
                        ImGui.TextColored(Theme.Warning, TF($"· {app.Document.PendingCount} unsaved"));
                }
                else
                {
                    ImGui.TextColored(Theme.TextDim, T("No data loaded"));
                }

                var last = Log.Lines.LastOrDefault(l => l.Level != LogLevel.Info);
                if (last is not null)
                {
                    var text = last.Text.Length > 120 ? last.Text[..120] + "…" : last.Text;
                    ImGui.SameLine(ImGui.GetWindowWidth() - ImGui.CalcTextSize(text).X - 24);
                    ImGui.TextColored(last.Level == LogLevel.Error ? Theme.Error : Theme.Warning, text);
                }

                ImGui.EndMenuBar();
            }
        }
        ImGui.End();
    }

    private static string Spinner() => "|/-\\"[(int)(ImGui.GetTime() * 8) % 4].ToString();
}

/// <summary>
/// First-run dock layout: render view in the middle, Object Window and Cell View on the left,
/// Inspector on the right, log and load order at the bottom. Later runs use the saved layout.
/// </summary>
internal static class DefaultLayout
{
    private static bool _reset;

    public static void RequestReset() => _reset = true;

    public static void BuildIfMissing(uint dockId, string iniFile)
    {
        // Layouts saved before the newest change (language-independent window IDs, 0.4) get the new default once.
        if (File.Exists(iniFile) && !_reset && File.ReadAllText(iniFile).Contains("###Navmesh]")) return;
        Build(dockId);
    }

    public static void ApplyPendingReset(uint dockId)
    {
        if (!_reset) return;
        Build(dockId);
    }

    private static unsafe void Build(uint dockId)
    {
        _reset = false;
        var viewport = ImGui.GetMainViewport();

        ImGuiP.DockBuilderRemoveNode(dockId);
        ImGuiP.DockBuilderAddNode(dockId, (ImGuiDockNodeFlags)ImGuiDockNodeFlagsPrivate.Space | ImGuiDockNodeFlags.PassthruCentralNode);
        ImGuiP.DockBuilderSetNodeSize(dockId, viewport.WorkSize);

        uint left = 0, right = 0, bottom = 0, center = dockId, leftBottom = 0;
        ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Left, 0.36f, &left, &center);
        ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Right, 0.34f, &right, &center);
        ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Down, 0.25f, &bottom, &center);
        ImGuiP.DockBuilderSplitNode(left, ImGuiDir.Down, 0.40f, &leftBottom, &left);

        ImGuiP.DockBuilderDockWindow("###Object Window", left);
        ImGuiP.DockBuilderDockWindow("###Cell View", leftBottom);
        uint rightBottom = 0;
        ImGuiP.DockBuilderSplitNode(right, ImGuiDir.Down, 0.42f, &rightBottom, &right);
        ImGuiP.DockBuilderDockWindow("###Inspector", right);
        ImGuiP.DockBuilderDockWindow("###Preview", rightBottom);
        ImGuiP.DockBuilderDockWindow("###Landscape", rightBottom);
        ImGuiP.DockBuilderDockWindow("###Navmesh", rightBottom);
        ImGuiP.DockBuilderDockWindow("###Render Window", center);
        ImGuiP.DockBuilderDockWindow("###Log", bottom);
        ImGuiP.DockBuilderDockWindow("###Load Order", bottom);
        ImGuiP.DockBuilderFinish(dockId);
    }
}

internal sealed class LogPanel : Panel
{
    private int _seenVersion;
    private bool _autoScroll = true;
    private bool _showInfo = true;

    public override string Name => "Log";

    protected override void DrawContent(EditorApp app)
    {
        ImGui.Checkbox(T("Info"), ref _showInfo);
        ImGui.SameLine();
        ImGui.Checkbox(T("Auto-scroll"), ref _autoScroll);
        ImGui.SameLine();
        if (ImGui.SmallButton(T("Open log file")))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true });

        ImGui.BeginChild("##log", new Vector2(0, 0), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar);
        ImGui.PushFont(app.Ui.MonoFont, 0);
        foreach (var line in Log.Lines)
        {
            if (!_showInfo && line.Level == LogLevel.Info) continue;
            var color = line.Level switch
            {
                LogLevel.Error => Theme.Error,
                LogLevel.Warning => Theme.Warning,
                _ => Theme.TextDim,
            };
            ImGui.TextColored(color, $"{line.Time:HH:mm:ss}  {line.Text}");
        }
        ImGui.PopFont();

        if (_autoScroll && _seenVersion != Log.Version) ImGui.SetScrollHereY(1f);
        _seenVersion = Log.Version;
        ImGui.EndChild();
    }
}
