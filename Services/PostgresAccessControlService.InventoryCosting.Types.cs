using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    private sealed class InventoryCostFlowEvent
    {
        public string SourceType { get; init; } = string.Empty;

        public long SourceId { get; init; }

        public long SourceLineId { get; init; }

        public int LineNo { get; init; }

        public long LocationId { get; init; }

        public long ItemId { get; init; }

        public string ItemCode { get; init; } = string.Empty;

        public DateTime EventDate { get; init; }

        public decimal Qty { get; init; }

        public decimal UnitCost { get; init; }

        public string InventoryAccountCode { get; init; } = string.Empty;

        public string CogsAccountCode { get; init; } = string.Empty;

        public long? CogsJournalId { get; init; }
    }

    private sealed class InventoryValuationSnapshotRow
    {
        public long LocationId { get; init; }

        public string AccountCode { get; init; } = string.Empty;

        public decimal TotalValue { get; init; }
    }

    private sealed class InventoryCostConsumptionResult
    {
        public bool IsSuccess { get; init; }

        public string Message { get; init; } = string.Empty;

        public decimal UnitCost { get; init; }

        public decimal TotalCost { get; init; }

        public static InventoryCostConsumptionResult Success(decimal unitCost, decimal totalCost)
        {
            return new InventoryCostConsumptionResult
            {
                IsSuccess = true,
                Message = string.Empty,
                UnitCost = RoundCost(unitCost),
                TotalCost = RoundCost(totalCost)
            };
        }

        public static InventoryCostConsumptionResult Failure(string message)
        {
            return new InventoryCostConsumptionResult
            {
                IsSuccess = false,
                Message = message ?? "Perhitungan cost gagal.",
                UnitCost = 0,
                TotalCost = 0
            };
        }
    }

    private sealed class InventoryAutoJournalLine
    {
        public string AccountCode { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;

        public decimal Debit { get; set; }

        public decimal Credit { get; set; }
    }

    private sealed class InventoryPostedJournalResult
    {
        public bool IsSuccess { get; init; }

        public string Message { get; init; } = string.Empty;

        public long? JournalId { get; init; }

        public string JournalNo { get; init; } = string.Empty;

        public static InventoryPostedJournalResult SuccessNoJournal(string message)
        {
            return new InventoryPostedJournalResult
            {
                IsSuccess = true,
                Message = message ?? string.Empty,
                JournalId = null,
                JournalNo = string.Empty
            };
        }

        public static InventoryPostedJournalResult Failure(string message)
        {
            return new InventoryPostedJournalResult
            {
                IsSuccess = false,
                Message = message ?? "Gagal membuat jurnal otomatis inventory.",
                JournalId = null,
                JournalNo = string.Empty
            };
        }
    }

    private sealed class InventoryJournalCreateResult
    {
        public bool IsSuccess { get; init; }

        public string Message { get; init; } = string.Empty;

        public long? JournalId { get; init; }

        public string JournalNo { get; init; } = string.Empty;

        public static InventoryJournalCreateResult SuccessNoJournal()
        {
            return new InventoryJournalCreateResult
            {
                IsSuccess = true,
                Message = string.Empty,
                JournalId = null,
                JournalNo = string.Empty
            };
        }

        public static InventoryJournalCreateResult Failure(string message)
        {
            return new InventoryJournalCreateResult
            {
                IsSuccess = false,
                Message = message ?? "Gagal membuat jurnal COGS inventory.",
                JournalId = null,
                JournalNo = string.Empty
            };
        }
    }

    private sealed class InventoryAdjustmentJournalResult
    {
        public bool IsSuccess { get; init; }

        public string Message { get; init; } = string.Empty;

        public int JournalCount { get; init; }

        public IReadOnlyCollection<string> JournalNos { get; init; } = [];

        public static InventoryAdjustmentJournalResult Success(IReadOnlyCollection<string> journalNos)
        {
            var normalized = (journalNos ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new InventoryAdjustmentJournalResult
            {
                IsSuccess = true,
                Message = string.Empty,
                JournalCount = normalized.Length,
                JournalNos = normalized
            };
        }

        public static InventoryAdjustmentJournalResult Failure(string message)
        {
            return new InventoryAdjustmentJournalResult
            {
                IsSuccess = false,
                Message = message ?? "Gagal membuat jurnal penyesuaian valuasi inventory.",
                JournalCount = 0,
                JournalNos = []
            };
        }
    }

    private sealed class InventoryAdjustmentEventSaveResult
    {
        public bool IsSuccess { get; init; }

        public string Message { get; init; } = string.Empty;

        public int DocumentCount { get; init; }

        public IReadOnlyCollection<string> ReferenceNos { get; init; } = [];

        public static InventoryAdjustmentEventSaveResult Success(IReadOnlyCollection<string> referenceNos)
        {
            var normalized = (referenceNos ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new InventoryAdjustmentEventSaveResult
            {
                IsSuccess = true,
                Message = string.Empty,
                DocumentCount = normalized.Length,
                ReferenceNos = normalized
            };
        }

        public static InventoryAdjustmentEventSaveResult Failure(string message)
        {
            return new InventoryAdjustmentEventSaveResult
            {
                IsSuccess = false,
                Message = message ?? "Gagal menyimpan event penyesuaian valuasi inventory.",
                DocumentCount = 0,
                ReferenceNos = []
            };
        }
    }

    private readonly record struct InventoryCostKey(long LocationId, long ItemId);
}
