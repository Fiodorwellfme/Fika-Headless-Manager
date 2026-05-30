using FikaHeadlessManager.Models;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace FikaHeadlessManager;

public static class Program
{
    private static Settings? Settings { get; set; }
    private static string? StartArguments
    {
        get
        {
            if (Settings == null)
            {
                Log("Settings were null when trying to generate StartArguments?", ConsoleColor.Red);
                return string.Empty;
            }

            if (string.IsNullOrEmpty(Settings.ProfileId))
            {
                Log("ProfileId was null!", ConsoleColor.Red);
                return string.Empty;
            }

            if (Settings.BackendUrl == null)
            {
                Log("BackendUrl was null!", ConsoleColor.Red);
                return string.Empty;
            }

            var graphicsArgs = WithGraphics ? string.Empty : " -nographics -batchmode";
            var logArg = !Settings.ExtraLogging ? string.Empty : " -logfile Headless.log";
            var titleArg = !string.IsNullOrEmpty(Settings.Title) ? $" -title=\"{Settings.Title}\"" : string.Empty;

            return $"-token={Settings.ProfileId} " +
                   $"-config={{'BackendUrl':'{Settings.BackendUrl.OriginalString}','Version':'live'}}" +
                   graphicsArgs +
                   logArg +
                   titleArg +
                   " --enable-console true";
        }
    }
    private static bool WithGraphics { get; set; }
    private static Process? TarkovProcess { get; set; }
    private static IntPtr TarkovWindow { get; set; }
    private static TrayToggle? ActiveTrayToggle { get; set; }
    private static bool IsManagerHidden { get; set; }
    private static int CleanupStarted;

    private static async Task Main()
    {
        OwnedConsole.Start();
        ConsoleCloseHandler.Start(Cleanup);
        AppDomain.CurrentDomain.ProcessExit += CurrentDomain_ProcessExit;

        if (!File.Exists("EscapeFromTarkov.exe"))
        {
            Log("Unable to find 'EscapeFromTarkov.exe'.\n" +
                "Make sure you are running Fika Headless Manager from a valid SPT install folder!", ConsoleColor.Red);
            Console.ReadKey(true);
            Environment.Exit(1);
        }

        if (!File.Exists(@"BepInEx\plugins\Fika\Fika.Headless.dll"))
        {
            Log("Unable to find 'Fika.Headless.dll'.\n" +
                "Please revisit the documentation and install Fika Headless using Fika-Installer!", ConsoleColor.Red);
            Console.ReadKey(true);
            Environment.Exit(1);
        }

        const string configPath = "HeadlessConfig.json";
        if (!File.Exists(configPath))
        {
            Log("Unable to find the configuration file 'HeadlessConfig.json'.\nMake sure that you have configured the headless correctly!", ConsoleColor.Red);
            Console.ReadKey(true);
            Environment.Exit(1);
        }

        try
        {
            await using var fileStream = File.OpenRead(configPath);
            Settings = await JsonSerializer.DeserializeAsync<Settings>(fileStream)
                       ?? throw new InvalidOperationException("Failed to deserialize configuration.");
        }
        catch (Exception ex)
        {
            Log($"Error loading configuration: {ex.Message}", ConsoleColor.Red);
            Console.ReadKey(true);
            Environment.Exit(1);
        }

        if (!string.IsNullOrEmpty(Settings.Title))
        {
            Console.Title = $"Headless Manager - {Settings.Title}";
        }

        var trayTitle = !string.IsNullOrEmpty(Settings.Title) ? Settings.Title : "Fika Headless Manager";
        ActiveTrayToggle = TrayToggle.Start(trayTitle, Settings.StartMinimizedToTray, SetManagerHidden);

        _ = Task.Run(GameLoop);
        await Task.Delay(-1); // keep process alive
    }

    private static void CurrentDomain_ProcessExit(object? sender, EventArgs e)
    {
        Cleanup();
    }

    private static void Cleanup()
    {
        if (Interlocked.Exchange(ref CleanupStarted, 1) == 1)
        {
            return;
        }

        var tarkovProcess = TarkovProcess;

        try
        {
            if (tarkovProcess != null && !tarkovProcess.HasExited)
            {
                tarkovProcess.Kill(true);
                tarkovProcess.WaitForExit(5000);
            }
        }
        catch
        {
            // Process may have already exited while the manager is closing.
        }

        ActiveTrayToggle?.Dispose();
        OwnedConsole.Stop();
    }

    private static async Task<bool> StartGame()
    {
        var logMessage = $"Starting headless client {(WithGraphics ? "with" : "without")} graphics and {(Settings!.ExtraLogging ? "extra logging" : "no extra logging")}.";
        if (!string.IsNullOrEmpty(Settings.Title))
        {
            logMessage += $" Using custom title: '{Settings.Title}'";
        }
        Log(logMessage);

        var logFile = Path.Combine(Environment.CurrentDirectory, @"BepInEx\LogOutput.log");

        if (File.Exists(logFile))
        {
            try
            {
                await Task.Run(() => File.Move(logFile, logFile.Replace(".log", "_prev.log"), true));
            }
            catch (Exception ex)
            {
                Log($"Could not archive the previous log file:\n{ex.Message}", ConsoleColor.Red);
            }
        }

        var startInfo = new ProcessStartInfo
        {
            Arguments = StartArguments,
            UseShellExecute = true,
            FileName = "EscapeFromTarkov.exe",
            WindowStyle = (!WithGraphics && Settings!.StartMinimized) ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal
        };

        TarkovProcess = Process.Start(startInfo);
        if (TarkovProcess != null && IsManagerHidden)
        {
            _ = HideProcessWindowWhenReady(TarkovProcess);
        }

        return TarkovProcess != null;
    }

    private static void SetManagerHidden(bool hidden)
    {
        IsManagerHidden = hidden;

        if (TarkovProcess == null || TarkovProcess.HasExited)
        {
            return;
        }

        _ = SetProcessWindowHidden(TarkovProcess, hidden);
    }

    private static bool SetProcessWindowHidden(Process process, bool hidden)
    {
        var window = hidden ? GetProcessWindow(process) : TarkovWindow;
        if (window == IntPtr.Zero)
        {
            window = GetProcessWindow(process);
        }

        if (window == IntPtr.Zero)
        {
            return false;
        }

        ShowWindow(window, hidden ? SW_HIDE : SW_RESTORE);
        if (!hidden)
        {
            SetForegroundWindow(window);
        }

        return true;
    }

    private static IntPtr GetProcessWindow(Process process)
    {
        process.Refresh();
        var window = process.MainWindowHandle;
        if (window != IntPtr.Zero)
        {
            TarkovWindow = window;
        }

        return TarkovWindow;
    }

    private static async Task HideProcessWindowWhenReady(Process process)
    {
        for (var i = 0; i < 20 && IsManagerHidden && !process.HasExited; i++)
        {
            if (SetProcessWindowHidden(process, true))
            {
                return;
            }

            await Task.Delay(250);
        }
    }

    private static async Task GameLoop()
    {
        while (true)
        {
            var success = await IsServerAccessible(Settings!.BackendUrl);
            if (!success)
            {
                Log("Press any key to exit...");
                Console.ReadKey(true);
                Environment.Exit(1);
            }

            WithGraphics = await WaitForGraphicsInput();

            var started = await StartGame();
            if (!started)
            {
                Log("Could not start the headless client!", ConsoleColor.Red);
                Console.ReadKey();
                Environment.Exit(1);
            }

            await TarkovProcess!.WaitForExitAsync();
            TarkovProcess = null;
            TarkovWindow = IntPtr.Zero;

            Log("Game exited, restarting...");
        }
    }

    private static async Task<bool> WaitForGraphicsInput()
    {
        Log("Press 'g' to start with graphics or wait 3 seconds...");

        var delayTask = Task.Delay(3000);

        while (!delayTask.IsCompleted)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                return key.Key == ConsoleKey.G;
            }
            await Task.Delay(50); // small delay to avoid busy looping
        }

        return false;
    }

    private static void Log(string message, ConsoleColor color = ConsoleColor.White)
    {
        if (color is not ConsoleColor.White)
        {
            Console.ForegroundColor = color;
        }

        Console.WriteLine(message);
        Console.ResetColor();
    }

    private static async Task<bool> IsServerAccessible(Uri? BackendUrl, string ApiEndpoint = "fika/presence/get")
    {
        HttpClientHandler InsecureHandler = new()
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

        HttpClient client = new(InsecureHandler);

        try
        {
            client.DefaultRequestHeaders.Add("responsecompressed", "0");

            var response = await client.GetAsync($"{BackendUrl}{ApiEndpoint}");

            if (response.IsSuccessStatusCode)
            {
                return true;
            }
            else
            {
                Log($"Could not access {BackendUrl}{ApiEndpoint}\nEnsure Fika Server mod is installed. Please review the installation process in the documentation.", ConsoleColor.Red);
                return false;
            }
        }
        catch
        {
            Log($"Could not reach SPT.Server at {BackendUrl}\nPlease ensure SPT.Server is running and accessible.", ConsoleColor.Red);
            return false;
        }
        finally
        {
            client.Dispose();
            InsecureHandler.Dispose();
        }
    }

    private const int SW_HIDE = 0;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}

internal static class OwnedConsole
{
    internal static void Start()
    {
        if (!AllocConsole())
        {
            return;
        }

        Console.SetIn(new StreamReader(Console.OpenStandardInput()));
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    internal static void Stop()
    {
        FreeConsole();
    }

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();
}

internal static class ConsoleCloseHandler
{
    private static HandlerRoutine? Handler;
    private static Action? Cleanup;

    internal static void Start(Action cleanup)
    {
        Cleanup = cleanup;
        Handler = HandleConsoleClose;
        SetConsoleCtrlHandler(Handler, true);
    }

    private static bool HandleConsoleClose(CtrlType ctrlType)
    {
        switch (ctrlType)
        {
            case CtrlType.CtrlC:
            case CtrlType.CtrlBreak:
            case CtrlType.CtrlClose:
            case CtrlType.CtrlShutdown:
                Cleanup?.Invoke();
                return false;
            default:
                return false;
        }
    }

    private delegate bool HandlerRoutine(CtrlType ctrlType);

    private enum CtrlType
    {
        CtrlC = 0,
        CtrlBreak = 1,
        CtrlClose = 2,
        CtrlLogoff = 5,
        CtrlShutdown = 6
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(HandlerRoutine handlerRoutine, bool add);
}

internal sealed class TrayToggle : ApplicationContext
{
    private const int GWL_EXSTYLE = -20;
    private const uint GA_ROOT = 2;
    private const int SW_HIDE = 0;
    private const int SW_RESTORE = 9;
    private const int SWP_NOMOVE = 0x0002;
    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_FRAMECHANGED = 0x0020;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_APPWINDOW = 0x00040000L;

    private readonly IntPtr _window;
    private readonly NotifyIcon _notifyIcon;
    private readonly IntPtr _originalExtendedStyle;
    private readonly Action<bool> _hiddenChanged;
    private bool _isHidden;

    private TrayToggle(IntPtr window, string title, bool startHidden, Action<bool> hiddenChanged)
    {
        _window = window;
        _hiddenChanged = hiddenChanged;
        _originalExtendedStyle = GetWindowLongPtr(_window, GWL_EXSTYLE);

        _notifyIcon = new NotifyIcon
        {
            Icon = GetTrayIcon(),
            Text = NormalizeTrayTitle(title),
            Visible = true
        };

        _notifyIcon.Click += (_, _) => ToggleConsole();

        if (startHidden)
        {
            HideConsole();
        }
    }

    internal static TrayToggle? Start(string title, bool startHidden, Action<bool> hiddenChanged)
    {
        var window = GetConsoleWindow();
        if (window == IntPtr.Zero)
        {
            return null;
        }

        var rootWindow = GetAncestor(window, GA_ROOT);
        if (rootWindow != IntPtr.Zero)
        {
            window = rootWindow;
        }

        using var ready = new ManualResetEventSlim();
        TrayToggle? trayToggle = null;

        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            trayToggle = new TrayToggle(window, title, startHidden, hiddenChanged);
            ready.Set();
            Application.Run(trayToggle);
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        ready.Wait();

        return trayToggle;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _notifyIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ToggleConsole()
    {
        if (_isHidden)
        {
            RestoreConsole();
            return;
        }

        HideConsole();
    }

    private void HideConsole()
    {
        var hiddenStyle = new IntPtr((_originalExtendedStyle.ToInt64() & ~WS_EX_APPWINDOW) | WS_EX_TOOLWINDOW);
        SetWindowLongPtr(_window, GWL_EXSTYLE, hiddenStyle);
        SetWindowPos(_window, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        ShowWindow(_window, SW_HIDE);
        _isHidden = true;
        _hiddenChanged(true);
    }

    private void RestoreConsole()
    {
        SetWindowLongPtr(_window, GWL_EXSTYLE, _originalExtendedStyle);
        SetWindowPos(_window, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        ShowWindow(_window, SW_RESTORE);
        SetForegroundWindow(_window);
        _isHidden = false;
        _hiddenChanged(false);
    }

    private static Icon GetTrayIcon()
    {
        if (Environment.ProcessPath == null)
        {
            return SystemIcons.Application;
        }

        return Icon.ExtractAssociatedIcon(Environment.ProcessPath) ?? SystemIcons.Application;
    }

    private static string NormalizeTrayTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Fika Headless Manager";
        }

        return title.Length <= 63 ? title : title[..63];
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : GetWindowLongPtr32(hWnd, nIndex);
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : SetWindowLongPtr32(hWnd, nIndex, dwNewLong);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, int flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}

internal static class StartupNative
{
    const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x00000800;

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    [ModuleInitializer]
    internal static void Init()
    {
        IntPtr h = LoadLibraryEx("winhttp.dll", IntPtr.Zero, LOAD_LIBRARY_SEARCH_SYSTEM32);

        if (h == IntPtr.Zero)
        {
            // Considering this is a non-fatal error and this patch fix is only for rare cases we continue without throwing
            //throw new Win32Exception(Marshal.GetLastWin32Error());

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed to load winhttp.dll from system folder.");
            Console.ResetColor();
            Console.WriteLine($"If no other issues are experienced, ignore this error.");
        }
    }
}
