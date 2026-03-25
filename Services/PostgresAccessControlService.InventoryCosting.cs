using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Accounting.Services;

public sealed partial class PostgresAccessControlService
{
    private const string InventoryValuationMethodAverage = "AVERAGE";
    private const string InventoryValuationMethodFifo = "FIFO";
    private const string InventoryValuationMethodLifo = "LIFO";

    private const string InventoryCostSourceStockIn = "STOCK_IN";
    private const string InventoryCostSourceStockOut = "STOCK_OUT";
    private const string InventoryCostSourceOpnamePlus = "OPNAME_PLUS";
    private const string InventoryCostSourceOpnameMinus = "OPNAME_MINUS";
}
