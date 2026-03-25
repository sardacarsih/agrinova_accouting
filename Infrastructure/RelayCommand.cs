using System.Windows.Input;

namespace Accounting.Infrastructure;

public sealed class RelayCommand : ICommand
{
    private readonly Action? _execute;
    private readonly Action<object?>? _executeWithParameter;
    private readonly Func<bool>? _canExecute;
    private readonly Func<object?, bool>? _canExecuteWithParameter;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _executeWithParameter = execute;
        _canExecuteWithParameter = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        if (_canExecuteWithParameter is not null)
        {
            return _canExecuteWithParameter(parameter);
        }

        return _canExecute?.Invoke() ?? true;
    }

    public void Execute(object? parameter)
    {
        if (_executeWithParameter is not null)
        {
            _executeWithParameter(parameter);
            return;
        }

        _execute?.Invoke();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

