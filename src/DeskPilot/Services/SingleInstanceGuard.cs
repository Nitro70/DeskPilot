using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using DeskPilot.Core.Runtime;

namespace DeskPilot.Services;

/// <summary>
/// One DeskPilot per user: a named mutex marks the running instance and a named event lets a
/// second launch ask the first one to show its window before exiting.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly string _mutexName;
    private readonly string _eventName;
    private Mutex? _mutex;
    private EventWaitHandle? _activate;
    private RegisteredWaitHandle? _wait;
    private bool _owned;

    public SingleInstanceGuard(string baseName)
    {
        _mutexName = $@"Local\{baseName}.Instance";
        _eventName = $@"Local\{baseName}.Activate";
    }

    /// <summary>Raised on a thread-pool thread when another launch asks this instance to show itself.</summary>
    public event Action? ActivationRequested;

    public bool IsOwner => _owned;

    /// <summary>A per-user name (hashed user SID) so two accounts on one PC do not block each other.</summary>
    public static string DefaultBaseName()
    {
        string user;
        try { user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; }
        catch (Exception) { user = Environment.UserName; }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..16];
        return $"DeskPilot.{hash}";
    }

    /// <summary>True when this is the first instance (it then starts listening for activation requests).</summary>
    public bool TryAcquire()
    {
        if (_owned) return true;
        _mutex = new Mutex(initiallyOwned: true, _mutexName, out var createdNew);
        if (!createdNew)
        {
            // An abandoned mutex (the previous instance crashed) can still be taken over.
            try { createdNew = _mutex.WaitOne(0); }
            catch (AbandonedMutexException) { createdNew = true; }
        }
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        _owned = true;
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, _eventName);
        _wait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, timedOut) =>
        {
            if (timedOut) return;
            try { ActivationRequested?.Invoke(); }
            catch (Exception ex) { Log.Error("Activation handler failed", ex); }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
        return true;
    }

    /// <summary>Called by a second launch: asks the running instance to show its window.</summary>
    public bool SignalFirstInstance()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(_eventName, out var handle)) return false;
            using (handle) return handle.Set();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _wait = null;
        _activate?.Dispose();
        _activate = null;
        if (_mutex != null)
        {
            if (_owned)
            {
                try { _mutex.ReleaseMutex(); }
                catch (ApplicationException) { }
            }
            _mutex.Dispose();
            _mutex = null;
        }
        _owned = false;
    }
}
