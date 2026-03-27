using System.Collections.ObjectModel;
using Accounting.Infrastructure;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed class InventoryImportErrorPanelState : ViewModelBase
{
    private string _summary = string.Empty;

    public InventoryImportErrorPanelState(string title)
    {
        Title = title;
        Errors = new ObservableCollection<InventoryImportError>();
        Errors.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasErrors));
            OnPropertyChanged(nameof(ErrorCountText));
        };
        CloseCommand = new RelayCommand(Clear);
    }

    public string Title { get; }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public ObservableCollection<InventoryImportError> Errors { get; }

    public bool HasErrors => Errors.Count > 0;

    public string ErrorCountText => Errors.Count switch
    {
        0 => "Tidak ada error",
        1 => "1 error",
        _ => $"{Errors.Count} error"
    };

    public RelayCommand CloseCommand { get; }

    public void SetErrors(IEnumerable<InventoryImportError> errors, string summary)
    {
        Errors.Clear();
        foreach (var error in errors)
        {
            Errors.Add(error);
        }

        Summary = summary;
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(ErrorCountText));
    }

    public void Clear()
    {
        Errors.Clear();
        Summary = string.Empty;
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(ErrorCountText));
    }
}
