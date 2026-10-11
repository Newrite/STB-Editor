using System.Numerics;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.D3D11;
using Hexa.NET.ImGui.Backends.Win32;
using StbEditor.App.Graphics;
using HexaDevice = Hexa.NET.ImGui.Backends.D3D11.ID3D11Device;
using HexaContext = Hexa.NET.ImGui.Backends.D3D11.ID3D11DeviceContext;

namespace StbEditor.App.Ui;

/// <summary>ImGui context with the official Win32 + D3D11 backends, fonts and the editor theme.</summary>
internal sealed unsafe class ImGuiLayer : IDisposable
{
    private readonly ImGuiContextPtr _context;
    private readonly nint _iniPath;

    public ImFontPtr DefaultFont { get; }
    public ImFontPtr MonoFont { get; }

    /// <summary>Display font for record titles (Sovngarde when the build has it).</summary>
    public ImFontPtr HeaderFont { get; }

    public string FontSource { get; }

    public ImGuiLayer(nint hwnd, GraphicsDevice graphics, float dpiScale, string iniFile, UiFonts fonts)
    {
        _context = ImGui.CreateContext();
        ImGui.SetCurrentContext(_context);

        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.DockingEnable;
        io.ConfigWindowsMoveFromTitleBarOnly = true;

        Directory.CreateDirectory(Path.GetDirectoryName(iniFile)!);
        _iniPath = Marshal.StringToCoTaskMemUTF8(iniFile);
        io.IniFilename = (byte*)_iniPath;

        // Futura Condensed runs narrow; a little larger reads like the game's menus.
        var fontSize = MathF.Round((fonts.Body is null ? 16f : 18f) * dpiScale);
        DefaultFont = fonts.Body is { } body ? io.Fonts.AddFontFromFileTTF(body, fontSize) : AddFont(io, ["segoeui.ttf", "arial.ttf"], fontSize);
        MonoFont = AddFont(io, ["consola.ttf", "cour.ttf"], MathF.Round(15f * dpiScale));
        HeaderFont = fonts.Header is { } header ? io.Fonts.AddFontFromFileTTF(header, MathF.Round(22f * dpiScale)) : DefaultFont;
        FontSource = fonts.Body ?? "Segoe UI";

        Theme.Apply(dpiScale);

        ImGuiImplWin32.SetCurrentContext(_context);
        ImGuiImplWin32.Init((void*)hwnd);

        ImGuiImplD3D11.SetCurrentContext(_context);
        ImGuiImplD3D11.Init(
            new ID3D11DevicePtr((HexaDevice*)graphics.Device.NativePointer),
            new ID3D11DeviceContextPtr((HexaContext*)graphics.Context.NativePointer));
    }

    private static ImFontPtr AddFont(ImGuiIOPtr io, string[] candidates, float size)
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var name in candidates)
        {
            var path = Path.Combine(fonts, name);
            if (File.Exists(path)) return io.Fonts.AddFontFromFileTTF(path, size);
        }

        return io.Fonts.AddFontDefault();
    }

    /// <summary>Feeds a window message to ImGui. True when ImGui consumed it.</summary>
    public bool HandleMessage(nint hwnd, uint msg, nint wParam, nint lParam) =>
        ImGuiImplWin32.WndProcHandler(hwnd, msg, (nuint)wParam, lParam) != 0;

    /// <summary>Autotests: mouse position / left button to feed ImGui instead of the real mouse.</summary>
    public static (System.Numerics.Vector2 Position, bool Down)? TestMouse { get; set; }

    /// <summary>Autotests: text typed into the focused field this frame, and keys pressed (released the next frame).</summary>
    public static string? TestText { get; set; }
    public static Queue<ImGuiKey> TestKeys { get; } = new();
    private static ImGuiKey? _testKeyDown;

    public void NewFrame()
    {
        ImGuiImplD3D11.NewFrame();
        ImGuiImplWin32.NewFrame();
        if (TestMouse is { } mouse)
        {
            var io = ImGui.GetIO();
            io.AddMousePosEvent(mouse.Position.X, mouse.Position.Y);
            io.AddMouseButtonEvent(0, mouse.Down);
        }
        if (TestText is { } typed)
        {
            foreach (var ch in typed) ImGui.GetIO().AddInputCharacter(ch);
            TestText = null;
        }
        if (_testKeyDown is { } down)
        {
            ImGui.GetIO().AddKeyEvent(down, false);
            _testKeyDown = null;
        }
        else if (TestKeys.TryDequeue(out var key))
        {
            ImGui.GetIO().AddKeyEvent(key, true);
            _testKeyDown = key;
        }
        ImGui.NewFrame();
    }

    public void Render()
    {
        ImGui.Render();
        ImGuiImplD3D11.RenderDrawData(ImGui.GetDrawData());
    }

    public static bool WantsInput
    {
        get
        {
            var io = ImGui.GetIO();
            return io.WantCaptureMouse || io.WantCaptureKeyboard;
        }
    }

    public void Dispose()
    {
        ImGuiImplD3D11.Shutdown();
        ImGuiImplWin32.Shutdown();
        // cimguizmo keeps its own pointer to the context; left dangling, its static state frees through
        // it when the DLL unloads (access violation on exit, only after the gizmo was used).
        Hexa.NET.ImGuizmo.ImGuizmo.SetImGuiContext(null);
        ImGui.DestroyContext(_context);
        Marshal.FreeCoTaskMem(_iniPath);
    }
}

