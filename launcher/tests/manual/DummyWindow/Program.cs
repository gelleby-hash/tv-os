using System.Windows;
using System.Windows.Input;

namespace TvosDummyWindow;

// A plain window that counts the F24 key presses it receives in its title, so the check can tell
// whether the hook swallowed a press. With --ignore-close it refuses to close, to exercise CloseAll's kill.
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var ignoreClose = args.Contains("--ignore-close");
        var presses = 0;
        var window = new Window { Width = 480, Height = 240, Title = "tv-os dummy window F24=0" };
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.F24)
                window.Title = $"tv-os dummy window F24={++presses}";
        };
        if (ignoreClose)
            window.Closing += (_, e) => e.Cancel = true;
        new Application().Run(window);
    }
}
