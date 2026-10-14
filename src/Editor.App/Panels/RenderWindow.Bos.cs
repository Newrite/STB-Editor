using System.Numerics;
using Mutagen.Bethesda.Plugins;
using StbEditor.Core;
using StbEditor.Render;
using static StbEditor.App.Ui.Loc;

namespace StbEditor.App.Panels;

/// <summary>
/// Base Object Swapper preview («BOS» on the toolbar): loaded objects that a _SWAP.ini line swaps are drawn as the swap base
/// (the first one when the line picks at random), moved / turned / scaled by the line's properties, tinted; transform-only
/// lines move the object's own model. Picking and editing still work on the original references.
/// </summary>
internal sealed partial class RenderWindow
{
    private bool _showBos;
    private (Scene?, object?, int, int) _bosKey;
    private object? _bosRulesFor;
    private (Dictionary<FormKey, BosRule> ByReference, Dictionary<FormKey, BosRule> ByBase) _bosRules;
    private readonly Dictionary<SceneObject, (BosRule Rule, FormKey Shown)> _bosInfo = [];

    /// <summary>How many loaded objects the preview draws swapped.</summary>
    public int BosSwapped => _bosInfo.Count;

    public bool ShowBos { get => _showBos; set => _showBos = value; }

    /// <summary>The BOS line that changes an object in the preview, if any.</summary>
    public (BosRule Rule, FormKey Shown)? BosOf(SceneObject obj) => _bosInfo.TryGetValue(obj, out var info) ? info : null;

    private void UpdateBos(EditorApp app)
    {
        if (_renderer?.Options is not { } options) return;
        if (!_showBos || _scene is null || app.Session is not { } session)
        {
            if (options.Swaps.Count > 0) options.Swaps.Clear();
            _bosInfo.Clear();
            _bosKey = default;
            return;
        }
        var task = session.Frameworks;
        if (!task.IsCompletedSuccessfully) return;
        var key = (_scene, (object?)task.Result, _scene.Objects.Count, app.Document.History.Version);
        if (key == _bosKey) return;
        _bosKey = key;
        if (!ReferenceEquals(_bosRulesFor, task.Result))
        {
            _bosRules = BosRules.Build(task.Result);
            _bosRulesFor = task.Result;
        }
        options.Swaps.Clear();
        _bosInfo.Clear();
        var builder = Builder(session);
        foreach (var obj in _scene.Objects)
        {
            if (obj.IsDeleted) continue;
            if (!_bosRules.ByReference.TryGetValue(obj.Reference, out var rule) && !_bosRules.ByBase.TryGetValue(obj.Base, out rule)) continue;
            var (position, rotation, scale) = BosRules.Apply(rule.Properties, obj.Position, obj.Rotation, obj.Scale);
            if (rule.TransformOnly)
            {
                options.Swaps[obj] = new SceneSwap(obj.ModelPath, SceneBuilder.ReferenceTransform(position, rotation, scale * obj.ModelScale).ToMatrix());
                _bosInfo[obj] = (rule, obj.Base);
                continue;
            }
            var swap = rule.Swaps[0];
            var placed = builder.CreatePlaced(FormKey.Null, swap, position, rotation, scale, obj.Cell, false);
            options.Swaps[obj] = new SceneSwap(placed.ModelPath, placed.World);
            _bosInfo[obj] = (rule, swap);
        }
    }

    /// <summary>The overlay's lines about the preview: how many are swapped, and what the selected object becomes.</summary>
    private IEnumerable<string> BosLines(EditorApp app)
    {
        if (!_showBos) yield break;
        if (app.Session?.Frameworks is not { IsCompletedSuccessfully: true }) { yield return T("BOS: reading framework configs…"); yield break; }
        yield return TF($"BOS: {_bosInfo.Count:N0} object(s) shown swapped (tinted)");
        if (_selection.Count != 1 || BosOf(_selection.First()) is not { } info) yield break;
        var shown = app.Session.Catalog.Get(info.Shown)?.EditorId ?? app.Session.Workspace.FormIdText(info.Shown);
        var source = $"{info.Rule.Source.Mod} · {Path.GetFileName(info.Rule.Source.File)}:{info.Rule.Source.Line}";
        var line = info.Rule.TransformOnly ? TF($"BOS: moved / turned / scaled ({info.Rule.Properties}) — {source}") : TF($"BOS: becomes {shown} — {source}");
        if (info.Rule.Swaps.Count > 1) line += "  " + TF($"(one of {info.Rule.Swaps.Count} at random)");
        if (info.Rule.Chance < 100) line += "  " + TF($"(chance {info.Rule.Chance:0}%)");
        if (info.Rule.Conditional) line += "  " + T("(with conditions the editor does not check)");
        yield return line;
    }
}
