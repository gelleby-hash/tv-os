using TvLauncher.Diagnostics;
using Windows.Networking.Connectivity;

// Not TvLauncher.System: a namespace of that name would hide the real System namespace inside the project.
namespace TvLauncher.SystemActions;

public enum NetState { Internet, Local, None }

/// <summary>Where the network state comes from; Windows in the launcher, a fake in the tests.</summary>
public interface INetworkSource
{
    NetState Read();

    /// <summary>Raised when Windows says the connection changed, on a thread-pool thread.</summary>
    event Action? Changed;
}

/// <summary>What Windows says: internet, only the local network (or a limited connection), or nothing.</summary>
public sealed class WindowsNetworkSource : INetworkSource, IDisposable
{
    private readonly NetworkStatusChangedEventHandler _handler;
    private Action? _changed;

    public WindowsNetworkSource()
    {
        _handler = _ => _changed?.Invoke();
    }

    public event Action? Changed
    {
        add
        {
            if (_changed is null)
                NetworkInformation.NetworkStatusChanged += _handler;
            _changed += value;
        }
        remove
        {
            _changed -= value;
            if (_changed is null)
                NetworkInformation.NetworkStatusChanged -= _handler;
        }
    }

    public NetState Read()
    {
        try
        {
            return FromLevel(NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel());
        }
        catch (Exception)
        {
            // Windows could not say; showing "No network" for a read that failed would be a guess either way.
            return NetState.Internet;
        }
    }

    // A captive portal or a router with no way out reads as constrained: the apps cannot reach their services.
    internal static NetState FromLevel(NetworkConnectivityLevel? level) => level switch
    {
        NetworkConnectivityLevel.InternetAccess => NetState.Internet,
        NetworkConnectivityLevel.ConstrainedInternetAccess or NetworkConnectivityLevel.LocalAccess => NetState.Local,
        _ => NetState.None,
    };

    public void Dispose()
    {
        NetworkInformation.NetworkStatusChanged -= _handler;
        _changed = null;
    }
}

/// <summary>
/// Tells the home page what the network is like. Windows can raise several changes for one real change, so only
/// a different state is posted and logged.
/// </summary>
public sealed class NetworkWatcher : IDisposable
{
    private readonly INetworkSource _source;
    private readonly Action<object> _post;
    private readonly TimingLog _timing;
    private readonly object _gate = new();
    private NetState? _last;

    public NetworkWatcher(INetworkSource source, Action<object> post, TimingLog timing)
    {
        _source = source;
        _post = post;
        _timing = timing;
        _source.Changed += OnChanged;
    }

    public static string Name(NetState s) => s switch
    {
        NetState.Internet => "internet",
        NetState.Local => "local",
        _ => "none",
    };

    /// <summary>Posts the state as it is now; the page asks for it when it has loaded.</summary>
    public void PostCurrent()
    {
        var state = _source.Read();
        lock (_gate)
            _last = state;
        _post(new { type = "net", state = Name(state) });
    }

    private void OnChanged()
    {
        var state = _source.Read();
        lock (_gate)
        {
            if (_last == state)
                return;
            _last = state;
        }
        _timing.Mark("net: " + Name(state));
        _post(new { type = "net", state = Name(state) });
    }

    public void Dispose() => _source.Changed -= OnChanged;
}
