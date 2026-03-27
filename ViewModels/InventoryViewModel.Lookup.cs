using System.Collections.ObjectModel;
using Accounting.Services;

namespace Accounting.ViewModels;

public sealed partial class InventoryViewModel
{
    private const int InventoryLookupSuggestionLimit = 25;

    public bool CommitItemLookup(StockTransactionLineEditor? line)
    {
        if (line is null)
        {
            return false;
        }

        var rawCode = (line.ItemLookupText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            line.ClearItemLookupState();
            return true;
        }

        var exactMatch = FindExactItemByCode(rawCode);
        if (exactMatch is not null)
        {
            line.ApplyResolvedItem(exactMatch);
            line.ClearItemLookupState();
            return true;
        }

        PopulateItemLookupSuggestions(line, rawCode);
        StatusMessage = $"Kode item '{rawCode}' tidak valid. Pilih item dari daftar.";
        return false;
    }

    public bool CommitExpenseAccountLookup(StockTransactionLineEditor? line)
    {
        if (line is null)
        {
            return false;
        }

        var rawCode = (line.ExpenseAccountLookupText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            line.ClearExpenseAccountLookupState();
            return true;
        }

        var exactMatch = FindExactExpenseAccountByCode(rawCode);
        if (exactMatch is not null)
        {
            line.ApplyResolvedExpenseAccount(exactMatch);
            line.ClearExpenseAccountLookupState();
            return true;
        }

        PopulateExpenseAccountLookupSuggestions(line, rawCode);
        StatusMessage = $"Kode akun beban '{rawCode}' tidak valid. Pilih akun dari daftar.";
        return false;
    }

    public bool CommitWarehouseLookup(StockTransactionLineEditor? line)
    {
        if (line is null)
        {
            return false;
        }

        var rawText = (line.WarehouseLookupText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(rawText))
        {
            line.ClearWarehouseLookupState();
            return true;
        }

        var exactMatch = FindExactWarehouse(rawText);
        if (exactMatch is not null)
        {
            line.ApplyResolvedWarehouse(exactMatch);
            line.ClearWarehouseLookupState();
            return true;
        }

        PopulateWarehouseLookupSuggestions(line, rawText, destination: false);
        StatusMessage = $"Gudang '{rawText}' tidak valid. Pilih gudang dari daftar.";
        return false;
    }

    public bool CommitDestinationWarehouseLookup(StockTransactionLineEditor? line)
    {
        if (line is null)
        {
            return false;
        }

        var rawText = (line.DestinationWarehouseLookupText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(rawText))
        {
            line.ClearDestinationWarehouseLookupState();
            return true;
        }

        var exactMatch = FindExactWarehouse(rawText);
        if (exactMatch is not null)
        {
            line.ApplyResolvedDestinationWarehouse(exactMatch);
            line.ClearDestinationWarehouseLookupState();
            return true;
        }

        PopulateWarehouseLookupSuggestions(line, rawText, destination: true);
        StatusMessage = $"Gudang tujuan '{rawText}' tidak valid. Pilih gudang dari daftar.";
        return false;
    }

    public bool ApplySelectedItemLookupOption(StockTransactionLineEditor? line, InventoryLookupOption? option)
    {
        if (line is null || option?.Value is not ManagedInventoryItem item)
        {
            return false;
        }

        line.ApplyResolvedItem(item);
        line.ClearItemLookupState();
        return true;
    }

    public bool ApplySelectedExpenseAccountLookupOption(StockTransactionLineEditor? line, InventoryLookupOption? option)
    {
        if (line is null || option?.Value is not ManagedAccount account)
        {
            return false;
        }

        line.ApplyResolvedExpenseAccount(account);
        line.ClearExpenseAccountLookupState();
        SyncStockOutExpenseAccountName(line);
        return true;
    }

    public bool ApplySelectedWarehouseLookupOption(StockTransactionLineEditor? line, InventoryLookupOption? option)
    {
        if (line is null || option?.Value is not ManagedWarehouse warehouse)
        {
            return false;
        }

        line.ApplyResolvedWarehouse(warehouse);
        line.ClearWarehouseLookupState();
        return true;
    }

    public bool ApplySelectedDestinationWarehouseLookupOption(StockTransactionLineEditor? line, InventoryLookupOption? option)
    {
        if (line is null || option?.Value is not ManagedWarehouse warehouse)
        {
            return false;
        }

        line.ApplyResolvedDestinationWarehouse(warehouse);
        line.ClearDestinationWarehouseLookupState();
        return true;
    }

    private ManagedInventoryItem? FindExactItemByCode(string code)
    {
        return EnumerateItemLookupCandidates()
            .FirstOrDefault(item => string.Equals(item.Code?.Trim(), code.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private ManagedAccount? FindExactExpenseAccountByCode(string code)
    {
        return EnumerateExpenseAccountLookupCandidates()
            .FirstOrDefault(account => string.Equals(account.Code?.Trim(), code.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private ManagedWarehouse? FindExactWarehouse(string text)
    {
        return EnumerateWarehouseLookupCandidates()
            .FirstOrDefault(warehouse =>
                string.Equals(warehouse.Code?.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(warehouse.Name?.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private void PopulateItemLookupSuggestions(StockTransactionLineEditor line, string query)
    {
        var rankedMatches = EnumerateItemLookupCandidates()
            .Select(item => new
            {
                Item = item,
                Rank = GetLookupSortRank(item.Code, item.Name, query)
            })
            .Where(x => x.Rank < 5)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Item.Code, StringComparer.OrdinalIgnoreCase)
            .Take(InventoryLookupSuggestionLimit)
            .Select(x => x.Item)
            .ToList();

        if (rankedMatches.Count == 0)
        {
            rankedMatches = EnumerateItemLookupCandidates()
                .OrderBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
                .Take(InventoryLookupSuggestionLimit)
                .ToList();
        }

        ReplaceLookupSuggestions(
            line.ItemLookupSuggestions,
            rankedMatches
                .Select(item => new InventoryLookupOption
                {
                    Code = item.Code,
                    Name = item.Name,
                    Value = item
                }));

        line.SelectedItemLookupSuggestion = line.ItemLookupSuggestions.FirstOrDefault();
        line.IsItemLookupPopupOpen = line.ItemLookupSuggestions.Count > 0;
    }

    private void PopulateExpenseAccountLookupSuggestions(StockTransactionLineEditor line, string query)
    {
        var rankedMatches = EnumerateExpenseAccountLookupCandidates()
            .Select(account => new
            {
                Account = account,
                Rank = GetLookupSortRank(account.Code, account.Name, query)
            })
            .Where(x => x.Rank < 5)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Account.Code, StringComparer.OrdinalIgnoreCase)
            .Take(InventoryLookupSuggestionLimit)
            .Select(x => x.Account)
            .ToList();

        if (rankedMatches.Count == 0)
        {
            rankedMatches = EnumerateExpenseAccountLookupCandidates()
                .OrderBy(account => account.Code, StringComparer.OrdinalIgnoreCase)
                .Take(InventoryLookupSuggestionLimit)
                .ToList();
        }

        ReplaceLookupSuggestions(
            line.ExpenseAccountLookupSuggestions,
            rankedMatches
                .Select(account => new InventoryLookupOption
                {
                    Code = account.Code,
                    Name = account.Name,
                    Value = account
                }));

        line.SelectedExpenseAccountLookupSuggestion = line.ExpenseAccountLookupSuggestions.FirstOrDefault();
        line.IsExpenseAccountLookupPopupOpen = line.ExpenseAccountLookupSuggestions.Count > 0;
    }

    private void PopulateWarehouseLookupSuggestions(StockTransactionLineEditor line, string query, bool destination)
    {
        var rankedMatches = EnumerateWarehouseLookupCandidates()
            .Select(warehouse => new
            {
                Warehouse = warehouse,
                Rank = GetLookupSortRank(warehouse.Code, warehouse.Name, query)
            })
            .Where(x => x.Rank < 5)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Warehouse.Code, StringComparer.OrdinalIgnoreCase)
            .Take(InventoryLookupSuggestionLimit)
            .Select(x => x.Warehouse)
            .ToList();

        if (rankedMatches.Count == 0)
        {
            rankedMatches = EnumerateWarehouseLookupCandidates()
                .OrderBy(warehouse => warehouse.Code, StringComparer.OrdinalIgnoreCase)
                .Take(InventoryLookupSuggestionLimit)
                .ToList();
        }

        var target = destination ? line.DestinationWarehouseLookupSuggestions : line.WarehouseLookupSuggestions;
        ReplaceLookupSuggestions(
            target,
            rankedMatches.Select(warehouse => new InventoryLookupOption
            {
                Code = warehouse.Code,
                Name = warehouse.Name,
                Value = warehouse
            }));

        if (destination)
        {
            line.SelectedDestinationWarehouseLookupSuggestion = line.DestinationWarehouseLookupSuggestions.FirstOrDefault();
            line.IsDestinationWarehouseLookupPopupOpen = line.DestinationWarehouseLookupSuggestions.Count > 0;
            return;
        }

        line.SelectedWarehouseLookupSuggestion = line.WarehouseLookupSuggestions.FirstOrDefault();
        line.IsWarehouseLookupPopupOpen = line.WarehouseLookupSuggestions.Count > 0;
    }

    private IEnumerable<ManagedInventoryItem> EnumerateItemLookupCandidates()
    {
        return StockItemLookupOptions
            .Concat(Items)
            .Where(item => item.IsActive && item.Id > 0)
            .GroupBy(item => item.Id)
            .Select(group => group.First());
    }

    private IEnumerable<ManagedAccount> EnumerateExpenseAccountLookupCandidates()
    {
        return StockOutExpenseAccountOptions
            .Concat(Accounts.Where(account => account.IsActive &&
                                              account.IsPosting &&
                                              string.Equals(account.AccountType, "EXPENSE", StringComparison.OrdinalIgnoreCase)))
            .Where(account => account.Id > 0 || !string.IsNullOrWhiteSpace(account.Code))
            .GroupBy(account => account.Code?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First());
    }

    private IEnumerable<ManagedWarehouse> EnumerateWarehouseLookupCandidates()
    {
        return Warehouses
            .Where(warehouse => warehouse.IsActive && warehouse.Id > 0)
            .GroupBy(warehouse => warehouse.Id)
            .Select(group => group.First());
    }

    private static int GetLookupSortRank(string? code, string? name, string query)
    {
        var normalizedQuery = query.Trim();
        var normalizedCode = (code ?? string.Empty).Trim();
        var normalizedName = (name ?? string.Empty).Trim();

        if (normalizedCode.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (normalizedCode.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (normalizedName.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (normalizedCode.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (normalizedName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        return 5;
    }

    private static void ReplaceLookupSuggestions(
        ObservableCollection<InventoryLookupOption> target,
        IEnumerable<InventoryLookupOption> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}
