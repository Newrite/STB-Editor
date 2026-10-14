using System.Runtime.InteropServices;
using static StbEditor.App.Platform.Win32;

namespace StbEditor.App.Platform;

/// <summary>The single top-level Win32 window. Everything inside is drawn with D3D11 + ImGui.</summary>
internal sealed unsafe class MainWindow
{
    private const string ClassName = "STBEditorMainWindow";

    // Kept alive for the lifetime of the window: the native side only holds a function pointer.
    private readonly WndProc _wndProc;

    public nint Handle { get; }
    public int ClientWidth { get; private set; }
    public int ClientHeight { get; private set; }
    public bool IsMinimized { get; private set; }

    /// <summary>This window has keyboard focus (another app is not in front).</summary>
    public bool IsForeground => GetForegroundWindow() == Handle;
    public bool CloseRequested { get; set; }

    /// <summary>Raised from the window procedure; return true to swallow the message.</summary>
    public Func<nint, uint, nint, nint, bool>? MessageFilter { get; set; }

    public event Action<int, int>? Resized;
    public event Func<bool>? Closing;

    public MainWindow(string title, int width, int height)
    {
        _wndProc = Procedure;
        var instance = GetModuleHandle(null);

        // app.ico next to the exe (the exe carries it too, for Explorer and MO2); the Windows default if it is missing.
        static nint AppIcon(bool large)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "app.ico");
            var size = GetSystemMetrics(large ? 11 : 49); // SM_CXICON / SM_CXSMICON
            var icon = File.Exists(path) ? LoadImage(0, path, IMAGE_ICON, size, size, LR_LOADFROMFILE) : 0;
            return icon != 0 ? icon : LoadIcon(0, IDI_APPLICATION);
        }

        fixed (char* className = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = CS_HREDRAW | CS_VREDRAW | CS_OWNDC,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = instance,
                hCursor = LoadCursor(0, IDC_ARROW),
                hIcon = AppIcon(large: true),
                hIconSm = AppIcon(large: false),
                lpszClassName = className,
            };

            if (RegisterClassEx(wc) == 0)
                throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        }

        Handle = CreateWindowEx(0, ClassName, title, WS_OVERLAPPEDWINDOW,
            CW_USEDEFAULT, CW_USEDEFAULT, width, height, 0, 0, instance, 0);

        if (Handle == 0)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        Main = Handle;

        GetClientRect(Handle, out var rect);
        ClientWidth = rect.Width;
        ClientHeight = rect.Height;
    }

    public void Show(bool maximized)
    {
        ShowWindow(Handle, maximized ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL);
        UpdateWindow(Handle);
    }

    public float DpiScale => GetDpiForWindow(Handle) / 96f;

    /// <summary>Pumps pending messages. Returns false once the window is gone.</summary>
    public bool PumpMessages()
    {
        while (PeekMessage(out var msg, 0, 0, 0, PM_REMOVE))
        {
            if (msg.message == WM_QUIT) return false;
            TranslateMessage(msg);
            DispatchMessage(msg);
        }

        return true;
    }

    /// <summary>Sleeps until input arrives or the timeout passes — keeps an idle editor off the CPU.</summary>
    public static void WaitForInput(uint milliseconds) => MsgWaitForMultipleObjects(0, 0, false, milliseconds, QS_ALLINPUT);

    /// <summary>The main window (for <see cref="Wake"/>).</summary>
    public static nint Main { get; private set; }

    /// <summary>Ends an idle wait from another thread (a request from the Claude bridge): posts WM_NULL to the main window.</summary>
    public static void Wake()
    {
        if (Main != 0) PostMessage(Main, 0, 0, 0);
    }

    private nint Procedure(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (MessageFilter?.Invoke(hwnd, msg, wParam, lParam) == true) return 1;

        switch (msg)
        {
            case WM_SIZE:
                IsMinimized = (uint)wParam == SIZE_MINIMIZED;
                if (!IsMinimized)
                {
                    ClientWidth = LoWord(lParam);
                    ClientHeight = HiWord(lParam);
                    Resized?.Invoke(ClientWidth, ClientHeight);
                }
                return 0;

            case WM_GETMINMAXINFO:
                var info = (MINMAXINFO*)lParam;
                info->ptMinTrackSizeX = 800;
                info->ptMinTrackSizeY = 500;
                return 0;

            case WM_SYSCOMMAND when ((uint)wParam & 0xFFF0) == SC_KEYMENU:
                // Alt alone would otherwise open the (absent) system menu and freeze input.
                return 0;

            case WM_CLOSE:
                if (Closing?.Invoke() == false)
                {
                    CloseRequested = true;
                    return 0;
                }
                DestroyWindow(hwnd);
                return 0;

            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
