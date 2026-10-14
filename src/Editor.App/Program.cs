using System.Diagnostics;
using StbEditor.App.Graphics;
using StbEditor.App.Platform;
using StbEditor.App.Services;
using StbEditor.App.Ui;
using Vortice.Mathematics;

namespace StbEditor.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Russian number formatting groups digits with U+202F, which the game's Futura lacks.
        var culture = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.CurrentCulture.Clone();
        culture.NumberFormat.NumberGroupSeparator = " ";
        System.Globalization.CultureInfo.CurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;

        Log.Initialize(AppPaths.LogFile);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashReport.Write(e.ExceptionObject as Exception);

        try
        {
            return Run(CommandLine.Parse(args));
        }
        catch (Exception ex)
        {
            var report = CrashReport.Write(ex);
            Win32.MessageBox(0, $"{ex.Message}\n\nReport: {report}", "STB Editor", 0x10);
            return 1;
        }
    }

    /// <summary>The game whose build fonts the UI uses; a failed detection just means no build fonts.</summary>
    private static StbEditor.Data.GameInstall? FontGame(Settings settings, CommandLine options)
    {
        try
        {
            var folder = options.Game ?? settings.GameFolder ?? StbEditor.Data.GameInstall.FindInstalledGame();
            return folder is null ? null : StbEditor.Data.GameInstall.Detect(folder, options.Mo2 ?? settings.Mo2Folder, options.Profile ?? settings.Mo2Profile);
        }
        catch
        {
            return null;
        }
    }

    private static int Run(CommandLine options)
    {
        Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        var settings = Settings.Load();
        var window = new MainWindow("STB Editor", 1600, 960);
        using var graphics = new GraphicsDevice(window.Handle, window.ClientWidth, window.ClientHeight);
        using var imgui = new ImGuiLayer(window.Handle, graphics, window.DpiScale, AppPaths.ImGuiIni, UiFonts.Find(FontGame(settings, options)));
        Log.Info($"UI font: {imgui.FontSource}");

        Log.Info($"STB Editor {typeof(Program).Assembly.GetName().Version} on {graphics.AdapterName}");

        using var editor = new EditorApp(settings, imgui, graphics, options);

        window.MessageFilter = imgui.HandleMessage;
        window.Resized += graphics.Resize;
        window.Closing += editor.ConfirmClose;
        window.Show(maximized: options.Screenshot is null);

        Win32.timeBeginPeriod(1);
        var frames = 0;
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed;

        while (window.PumpMessages())
        {
            if (window.IsMinimized)
            {
                MainWindow.WaitForInput(100);
                continue;
            }

            var now = clock.Elapsed;
            var delta = (float)(now - last).TotalSeconds;
            last = now;

            imgui.NewFrame();
            editor.Frame(delta);

            graphics.BeginFrame(new Color4(0.09f, 0.09f, 0.10f, 1f));
            editor.RenderScene();
            imgui.Render();

            // An autotest's extra frame (before / after pictures), taken without stopping.
            if (editor.ShotRequest is { } extra)
            {
                Screenshot.SaveBmp(extra, graphics.Width, graphics.Height, graphics.ReadBackBuffer());
                Log.Info($"screenshot saved to {extra}");
                editor.ShotRequest = null;
            }

            if (options.Screenshot is { } shot && editor.ReadyForScreenshot(++frames))
            {
                Screenshot.SaveBmp(shot, graphics.Width, graphics.Height, graphics.ReadBackBuffer());
                Log.Info($"screenshot saved to {shot}");
                if (editor.RenderWindow.Stats is { } stats) Log.Info($"render stats: {stats.Visible}/{stats.Objects} objects, {stats.DrawCalls} draws, {stats.Triangles / 1000}k tris, {stats.Grass} grass tufts, BOS {editor.RenderWindow.BosSwapped} swapped");
                break;
            }

            graphics.Present(vsync: true);
            LimitFrameRate(clock, now, settings.FpsLimit, window.IsForeground);

            if (window.CloseRequested)
            {
                var decision = editor.PollClose();
                if (decision == true) break;
                if (decision == false) window.CloseRequested = false;
            }

            // Nothing animating and no background job: do not spin at 60 FPS.
            if (!editor.IsBusy && !ImGuiLayer.WantsInput && options.Screenshot is null)
                MainWindow.WaitForInput(editor.IdleWaitMilliseconds);
        }

        // Autotests and screenshots do not leave their view behind for the user.
        if (options.Screenshot is null && !Environment.GetCommandLineArgs().Any(a => a.StartsWith("--autotest", StringComparison.Ordinal)))
            editor.RenderWindow.RememberView(editor);
        settings.Save();
        Log.Info("exit");
        return 0;
    }

    /// <summary>
    /// Vsync alone renders at the monitor's rate (144 on many screens) while anything moves; cap it,
    /// and to 15 FPS when another window has focus — nobody looks at the editor then.
    /// </summary>
    private static void LimitFrameRate(Stopwatch clock, TimeSpan frameStart, int limit, bool foreground)
    {
        if (!foreground) limit = limit is > 0 and < 15 ? limit : 15;
        if (limit <= 0) return;
        var wait = 1.0 / limit - (clock.Elapsed - frameStart).TotalSeconds;
        if (wait > 0.001) Thread.Sleep(TimeSpan.FromSeconds(wait));
    }
}
