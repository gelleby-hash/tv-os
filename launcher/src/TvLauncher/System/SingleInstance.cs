// Not TvLauncher.System: a namespace of that name would hide the real System namespace inside the project.
namespace TvLauncher.SystemActions;

/// <summary>
/// One launcher per signed-in session. A second copy would install a second Home hook (the newer one swallows
/// the key) and leave a topmost full-screen window over every app the family opens.
/// </summary>
public static class SingleInstance
{
    public const string MutexName = @"Local\TvLauncher";

    /// <summary>
    /// Takes the named mutex, or returns null when another holder already has it. Keep the result referenced
    /// for as long as the instance runs; Windows releases it when the process ends, even after a crash.
    /// </summary>
    public static IDisposable? TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        if (createdNew)
            return new Held(mutex);
        mutex.Dispose();
        return null;
    }

    private sealed class Held(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Disposed from a thread that does not own it: closing the handle below still frees the name.
            }
            mutex.Dispose();
        }
    }
}
