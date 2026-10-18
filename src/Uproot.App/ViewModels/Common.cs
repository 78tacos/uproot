using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Uproot.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        Raise(propertyName);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public static RelayCommand Create(Action action, Func<bool>? canExecute = null) =>
        new(_ => action(), canExecute is null ? null : _ => canExecute());
}

/// <summary>
/// An async command that blocks re-entry while it is running, so a slow scan or install cannot
/// be launched twice by an impatient double-click.
/// </summary>
public sealed class AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (_running) return;

        _running = true;
        CommandManager.InvalidateRequerySuggested();

        try
        {
            await execute(parameter);
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public static AsyncRelayCommand Create(Func<Task> action, Func<bool>? canExecute = null) =>
        new(_ => action(), canExecute is null ? null : _ => canExecute());
}
