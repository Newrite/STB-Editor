using System.Numerics;
using Hexa.NET.ImGui;

namespace StbEditor.App.Ui;

/// <summary>
/// Input fields that report a value once the user is done with it (focus left after an edit), so
/// one change is one undo step. ImGui's numeric inputs do not allow EnterReturnsTrue.
/// </summary>
internal static class Fields
{
    private static readonly Dictionary<uint, object> Pending = new();

    public static bool Int(string label, int value, out int result, int step = 1)
    {
        var id = ImGui.GetID(label);
        var v = Pending.TryGetValue(id, out var p) ? (int)p : value;
        if (ImGui.InputInt(label, ref v, step, step * 10)) Pending[id] = v;
        return Done(id, value, out result);
    }

    public static bool Float(string label, float value, out float result, string format = "%.4g")
    {
        var id = ImGui.GetID(label);
        var v = Pending.TryGetValue(id, out var p) ? (float)p : value;
        if (ImGui.InputFloat(label, ref v, 0, 0, format)) Pending[id] = v;
        return Done(id, value, out result);
    }

    public static bool Float3(string label, Vector3 value, out Vector3 result, string format = "%.2f")
    {
        var id = ImGui.GetID(label);
        var v = Pending.TryGetValue(id, out var p) ? (Vector3)p : value;
        if (ImGui.InputFloat3(label, ref v, format)) Pending[id] = v;
        return Done(id, value, out result);
    }

    public static bool Text(string label, string value, out string result, uint max = 512)
    {
        var id = ImGui.GetID(label);
        var v = Pending.TryGetValue(id, out var p) ? (string)p : value;
        if (ImGui.InputText(label, ref v, max)) Pending[id] = v;
        return Done(id, value, out result);
    }

    private static bool Done<T>(uint id, T value, out T result)
    {
        if (ImGui.IsItemDeactivatedAfterEdit() && Pending.Remove(id, out var done))
        {
            result = (T)done;
            return true;
        }
        if (!ImGui.IsItemActive()) Pending.Remove(id);
        result = value;
        return false;
    }
}
