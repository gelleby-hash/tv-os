// Manual dev-PC check of the Home key hook and Win32WindowSystem.Activate. Not shipped and not part of TvLauncher.sln.
//
//   dotnet build launcher/tests/manual/DummyWindow
//   dotnet run --project launcher/tests/manual/ManualCheck
//
// It starts two copies of a harmless dummy window: it minimises the first, puts the second in front, then
// brings the first back from a global-hook key press (F24, injected by a separate PowerShell process while
// the second has focus). It closes only the dummies it started.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TvLauncher.Apps;
using TvLauncher.Input;

const int VkF24 = 0x87;
const int VkF23 = 0x86;
const string DummyName = "TvosDummyWindow";

var manualDir = FindManualDir();
var dummyExe = Path.Combine(manualDir, "DummyWindow", "bin", "Debug", "net10.0-windows", DummyName + ".exe");
if (!File.Exists(dummyExe))
    return Fail($"Build the dummy first: {dummyExe} is missing.");

var windows = new Win32WindowSystem();
var started = new List<Process>();
var failures = 0;
var notVerified = 0;

try
{
    // 1. The hook installs on its own thread and unhooks without hanging (needs no input).
    var watch = Stopwatch.StartNew();
    new KeyboardHook([VkF24], VkF23, []).Dispose();
    Log($"KeyboardHook install and dispose took {watch.ElapsedMilliseconds} ms");
    Check(true, "KeyboardHook installed and unhooked cleanly");

    // 2. The window the hook will bring back, minimised.
    var target = await StartDummy(started, dummyExe);
    Native.ShowWindow(target, Native.SW_MINIMIZE);
    await Task.Delay(500);
    Check(Native.IsIconic(target), "the target dummy is minimised");

    // 3. A second dummy takes the focus; an injected F24 with no hook reaches it (control).
    var dummy = await StartDummy(started, dummyExe);
    // Windows does not let a process started from this background one take the focus by itself.
    Log($"dummy in front by itself: {Native.GetForegroundWindow() == dummy}; activating it");
    windows.Activate(dummy);
    if (!await TryWaitFor(() => Native.GetForegroundWindow() == dummy, TimeSpan.FromSeconds(5)))
    {
        // Seen at night on the dev PC: a running screen saver rejects SendInput and every foreground change.
        var saver = Process.GetProcesses().Any(p => p.ProcessName.StartsWith("scrnsave", StringComparison.OrdinalIgnoreCase));
        notVerified++;
        Log($"NOT VERIFIED (steps 3 and 4): could not bring the dummy to the front; foreground window 0x{Native.GetForegroundWindow():X}, screen saver running: {saver}. Rerun while someone is at the PC.");
    }
    else
    {
        await InjectF24();
        await Task.Delay(500);
        Log($"control press, dummy title: '{WindowTitle(dummy)}'");
        Check(WindowTitle(dummy).EndsWith("F24=1"), "without the hook the dummy receives F24");

        // 4. Hook installed: the press is swallowed and its handler brings the target back to the front.
        var pressed = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The hook treats injected keys as real (the remote's are flagged so), which is what this check relies on.
        using var hook = new KeyboardHook([VkF24], VkF23, []);
        hook.HomePressed += (_, _) =>
        {
            var at = Stopwatch.GetTimestamp();
            windows.Activate(target);
            pressed.TrySetResult(at);
        };
        var injectedAt = await InjectF24();
        var eventAt = await pressed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var activateStart = Stopwatch.GetElapsedTime(injectedAt, eventAt);
        await WaitFor(() => Task.FromResult(Native.GetForegroundWindow() == target), TimeSpan.FromSeconds(3), "target dummy in front");
        var front = Stopwatch.GetElapsedTime(eventAt);
        Log($"key press -> HomePressed handler: {activateStart.TotalMilliseconds:0} ms; handler -> target in front (seen by polling): <= {front.TotalMilliseconds:0} ms");
        Check(Native.GetForegroundWindow() == target && !Native.IsIconic(target), "Activate from the hook restored the target dummy and brought it to the front");
        await Task.Delay(300);
        Log($"dummy title after hooked press: '{WindowTitle(dummy)}'");
        Check(WindowTitle(dummy).EndsWith("F24=1"), "the hook swallowed F24 (dummy did not receive it)");
    }
}
catch (Exception ex)
{
    failures++;
    Log("ERROR: " + ex);
}
finally
{
    foreach (var p in started)
    {
        try { p.Kill(); p.WaitForExit(5000); } catch (InvalidOperationException) { }
        p.Dispose();
    }
    Log($"closed the {started.Count} dummy window(s) this check started");
}

if (failures > 0)
    Log($"{failures} CHECK(S) FAILED");
else if (notVerified > 0)
    Log($"ALL CHECKS THAT RAN PASSED, BUT {notVerified} PART(S) NOT VERIFIED");
else
    Log("ALL CHECKS PASSED");
return failures > 0 ? 1 : notVerified > 0 ? 3 : 0;

static async Task<bool> TryWaitFor(Func<bool> condition, TimeSpan limit)
{
    var watch = Stopwatch.StartNew();
    while (!condition())
    {
        if (watch.Elapsed > limit)
            return false;
        await Task.Delay(50);
    }
    return true;
}

void Check(bool ok, string what)
{
    Log((ok ? "PASS " : "FAIL ") + what);
    if (!ok)
        failures++;
}

static void Log(string line) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");

static int Fail(string why)
{
    Log(why);
    return 2;
}

static string FindManualDir()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (Directory.Exists(Path.Combine(dir.FullName, "DummyWindow")))
            return dir.FullName;
    throw new DirectoryNotFoundException("DummyWindow not found above " + AppContext.BaseDirectory);
}

static async Task WaitFor(Func<Task<bool>> condition, TimeSpan limit, string what)
{
    var watch = Stopwatch.StartNew();
    while (!await condition())
    {
        if (watch.Elapsed > limit)
            throw new TimeoutException($"timed out waiting for {what}");
        await Task.Delay(50);
    }
}

// Starts a dummy, records it for closing, and returns its window once it has one.
static async Task<IntPtr> StartDummy(List<Process> started, string exe)
{
    var process = Process.Start(exe)!;
    started.Add(process);
    var hwnd = IntPtr.Zero;
    await WaitFor(() =>
    {
        process.Refresh();
        return Task.FromResult((hwnd = process.MainWindowHandle) != IntPtr.Zero);
    }, TimeSpan.FromSeconds(15), DummyName + " window");
    return hwnd;
}

static string WindowTitle(IntPtr hwnd)
{
    var text = new StringBuilder(256);
    Native.GetWindowText(hwnd, text, text.Capacity);
    return text.ToString();
}

// Presses F24 from a separate PowerShell process, as a remote would from outside this one, and
// returns the Stopwatch timestamp taken just before the press (QPC is system-wide).
static async Task<long> InjectF24()
{
    const string script = """
        Add-Type -Name K -Namespace T -MemberDefinition '[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);'
        $t = [System.Diagnostics.Stopwatch]::GetTimestamp()
        [T.K]::keybd_event(0x87, 0, 0, [UIntPtr]::Zero)
        [T.K]::keybd_event(0x87, 0, 2, [UIntPtr]::Zero)
        $t
        """;
    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {encoded}")
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    using var ps = Process.Start(psi)!;
    var output = await ps.StandardOutput.ReadToEndAsync();
    await ps.WaitForExitAsync();
    return long.Parse(output.Trim());
}

static class Native
{
    public const int SW_MINIMIZE = 6;

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
}
