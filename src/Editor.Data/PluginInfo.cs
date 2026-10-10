using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using StbEditor.Data.Raw;

namespace StbEditor.Data;

/// <summary>One plugin of the open workspace.</summary>
public sealed class PluginInfo
{
    public required PluginLocation Location { get; init; }
    public required ModKey ModKey { get; init; }

    /// <summary>Position among active plugins (as loaded), -1 if not loaded.</summary>
    public int LoadIndex { get; internal set; } = -1;

    public PluginHeader? Header { get; internal set; }
    public PluginStringEncoding StringEncoding { get; internal set; }

    /// <summary>Holds both UTF-8 and code-page strings.</summary>
    public bool MixedEncoding { get; internal set; }

    public ISkyrimModGetter? Mod { get; internal set; }
    public string? Error { get; internal set; }

    /// <summary>Index prefix as the game assigns it: 0x0E000000 or 0xFE00A000.</summary>
    public uint FormIdPrefix { get; internal set; }

    public string Name => Location.Name;
    public bool IsActive => Location.Enabled && Location.Exists;
    public bool IsLoaded => Mod is not null;

    /// <summary>ESL flag or .esl extension: loads into the FE slot.</summary>
    public bool IsLight => PluginFileName.IsEsl(Name) || (Header?.IsLightFlagged ?? false);

    /// <summary>ESM flag or .esm/.esl extension: sorted before regular plugins.</summary>
    public bool IsMaster => PluginFileName.IsEsm(Name) || PluginFileName.IsEsl(Name) || (Header?.IsMasterFlagged ?? false);

    public string IndexLabel => LoadIndex < 0
        ? "--"
        : IsLight ? $"FE{(FormIdPrefix >> 12) & 0xFFF:X3}" : $"{FormIdPrefix >> 24:X2}";

    public override string ToString() => Name;
}
