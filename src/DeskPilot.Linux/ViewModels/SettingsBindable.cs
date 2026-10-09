using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace DeskPilot.Linux.ViewModels;

// Small MVVM helpers for the settings and welcome windows. Names carry a "Settings" prefix so they
// never clash with helpers another part of the app defines in the same namespace.

public abstract class SettingsBindableBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    /// <summary>Write-through setter for a value that lives on a settings POCO.</summary>
    protected bool Through<T>(T current, T value, Action<T> assign, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        assign(value);
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void Raise(params string[] names)
    {
        foreach (var n in names) Raise(n);
    }
}

public sealed class SettingsCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public SettingsCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute())
    {
    }

    public SettingsCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _execute(parameter);
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Runs an async operation; disabled while it runs so a double click cannot start it twice.</summary>
public sealed class SettingsAsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _running;

    public SettingsAsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning => _running;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        await ExecuteAsync();
    }

    public async Task ExecuteAsync()
    {
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            // Every operation behind these commands reports its own errors; this only keeps an
            // unexpected one from taking the dialog down.
            DeskPilot.Core.Runtime.Log.Error("Settings command failed", ex);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Inline validation messages keyed by field name. XAML binds to <c>Errors[FieldName]</c>; a missing
/// key yields "" so the error line hides itself.
/// </summary>
public sealed class SettingsErrorBag : INotifyPropertyChanged
{
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => _errors.TryGetValue(key, out var v) ? v : "";

    public bool HasErrors => _errors.Count > 0;

    public int Count => _errors.Count;

    /// <summary>Field names in the order the errors were added.</summary>
    public IReadOnlyList<string> Keys => _order;

    public bool Contains(string key) => _errors.ContainsKey(key);

    public void Set(string key, string message)
    {
        if (!_errors.ContainsKey(key)) _order.Add(key);
        _errors[key] = message;
        Changed();
    }

    public void Remove(string key)
    {
        if (_errors.Remove(key))
        {
            _order.Remove(key);
            Changed();
        }
    }

    public void Clear()
    {
        if (_errors.Count == 0) return;
        _errors.Clear();
        _order.Clear();
        Changed();
    }

    // An empty property name tells every binding on this object (the indexer included, whatever name a
    // binding engine gives it) to read again.
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>A value with a friendly label, for combo boxes. ToString is the label, so no item template is needed.</summary>
public sealed record SettingsOption<T>(T Value, string Label, string Description = "")
{
    public override string ToString() => Label;
}
