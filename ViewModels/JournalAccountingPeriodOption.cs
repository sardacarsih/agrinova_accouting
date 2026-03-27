namespace Accounting.ViewModels;

public sealed class JournalAccountingPeriodOption
{
    public JournalAccountingPeriodOption(DateTime periodMonth, bool isOpen, bool isRegistered = true)
    {
        PeriodMonth = new DateTime(periodMonth.Year, periodMonth.Month, 1);
        IsOpen = isOpen;
        IsRegistered = isRegistered;
    }

    public DateTime PeriodMonth { get; }

    public bool IsOpen { get; }

    public bool IsRegistered { get; }

    public string DisplayText => IsRegistered
        ? $"{PeriodMonth:MM/yyyy} - {(IsOpen ? "OPEN" : "CLOSED")}"
        : $"{PeriodMonth:MM/yyyy} - TIDAK TERDAFTAR";

    public override string ToString()
    {
        return DisplayText;
    }
}
