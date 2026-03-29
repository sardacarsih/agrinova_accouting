using System.Collections.ObjectModel;
using Accounting.Infrastructure;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed class JournalBrowseRowViewModel : ViewModelBase
{
    private bool _isDetailLoading;
    private bool _isDetailLoaded;
    private string _detailErrorMessage = string.Empty;

    public JournalBrowseRowViewModel(ManagedJournalSummary summary)
    {
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
        Lines = new ObservableCollection<ManagedJournalLine>();
    }

    public ManagedJournalSummary Summary { get; }

    public ObservableCollection<ManagedJournalLine> Lines { get; }

    public long Id => Summary.Id;

    public string JournalNo => Summary.JournalNo;

    public DateTime JournalDate => Summary.JournalDate;

    public string CreatedBy => Summary.CreatedBy;

    public string ReferenceNo => Summary.ReferenceNo;

    public string Description => Summary.Description;

    public string Status => Summary.Status;

    public decimal TotalDebit => Summary.TotalDebit;

    public decimal TotalCredit => Summary.TotalCredit;

    public bool IsDetailLoading
    {
        get => _isDetailLoading;
        set => SetProperty(ref _isDetailLoading, value);
    }

    public bool IsDetailLoaded
    {
        get => _isDetailLoaded;
        set => SetProperty(ref _isDetailLoaded, value);
    }

    public string DetailErrorMessage
    {
        get => _detailErrorMessage;
        set => SetProperty(ref _detailErrorMessage, value);
    }
}
