namespace Accounting.Services;

public sealed class JournalLifecycleResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;

    public long EntityId { get; init; }

    public ManagedJournalBundle? Bundle { get; init; }
}

public sealed class JournalLifecycleWorkflow
{
    private readonly IAccessControlService _accessControlService;

    public JournalLifecycleWorkflow(IAccessControlService accessControlService)
    {
        _accessControlService = accessControlService;
    }

    public async Task<JournalLifecycleResult> SaveDraftAsync(
        ManagedJournalHeader header,
        IReadOnlyCollection<ManagedJournalLine> lines,
        string actorUsername)
    {
        var result = await _accessControlService.SaveJournalDraftAsync(header, lines, actorUsername);
        return new JournalLifecycleResult
        {
            IsSuccess = result.IsSuccess,
            Message = result.Message,
            EntityId = result.EntityId ?? 0
        };
    }

    public async Task<JournalLifecycleResult> SubmitAsync(
        long journalId,
        long companyId,
        long locationId,
        string actorUsername)
    {
        var result = await _accessControlService.SubmitJournalAsync(journalId, companyId, locationId, actorUsername);
        return new JournalLifecycleResult
        {
            IsSuccess = result.IsSuccess,
            Message = result.Message,
            EntityId = journalId
        };
    }

    public async Task<JournalLifecycleResult> ApproveAsync(
        long journalId,
        long companyId,
        long locationId,
        string actorUsername)
    {
        var result = await _accessControlService.ApproveJournalAsync(journalId, companyId, locationId, actorUsername);
        return new JournalLifecycleResult
        {
            IsSuccess = result.IsSuccess,
            Message = result.Message,
            EntityId = journalId
        };
    }

    public async Task<JournalLifecycleResult> PostAsync(
        long journalId,
        long companyId,
        long locationId,
        string actorUsername)
    {
        var result = await _accessControlService.PostJournalAsync(journalId, companyId, locationId, actorUsername);
        return new JournalLifecycleResult
        {
            IsSuccess = result.IsSuccess,
            Message = result.Message,
            EntityId = journalId
        };
    }

    public async Task<JournalLifecycleResult> OpenAsync(long journalId, long companyId, long locationId, string actorUsername)
    {
        var bundle = await _accessControlService.GetJournalBundleAsync(journalId, companyId, locationId, actorUsername);
        if (bundle is null)
        {
            return new JournalLifecycleResult
            {
                IsSuccess = false,
                Message = "Jurnal tidak ditemukan.",
                EntityId = journalId
            };
        }

        return new JournalLifecycleResult
        {
            IsSuccess = true,
            Message = "Jurnal dimuat ke tab Input.",
            EntityId = bundle.Header.Id,
            Bundle = bundle
        };
    }
}
