using Mutagen.Bethesda.Plugins;

namespace StbEditor.App.Ui;

/// <summary>A form being dragged from the Object Window (ImGui payloads carry bytes; the key lives here).</summary>
internal static class FormDrag
{
    public const string PayloadType = "STB_FORM";

    public static FormKey? Current { get; set; }
}
