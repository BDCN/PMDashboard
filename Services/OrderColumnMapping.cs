using System.Reflection;
using System.Text.Json;
using PMDashboard.Data.Models;

namespace PMDashboard.Services;

public enum OrderFieldKind { Text, Number, Integer, Date }

/// <summary>One Order property that an import can fill from a spreadsheet column.</summary>
public sealed record OrderImportField(string Key, string Label, OrderFieldKind Kind, bool Required = false)
{
    /// <summary>The Order property written to (Key = property name).</summary>
    internal PropertyInfo Property { get; } = typeof(Order).GetProperty(Key)
        ?? throw new InvalidOperationException($"Order has no property '{Key}'");
}

/// <summary>Where one field's value comes from: a row-1 heading, a column letter, or nothing.</summary>
public sealed class ColumnSource
{
    /// <summary>Match this heading in row 1 (case-insensitive, extra spaces ignored). Survives
    /// columns being added, removed or reordered in the export — the recommended way.</summary>
    public string? Header { get; set; }

    /// <summary>Fixed column letter ("I"). How the built-in defaults work.</summary>
    public string? Column { get; set; }

    public bool IsSkipped => string.IsNullOrWhiteSpace(Header) && string.IsNullOrWhiteSpace(Column);

    public static ColumnSource FromHeader(string h) => new() { Header = h.Trim() };
    public static ColumnSource FromColumn(string c) => new() { Column = c.Trim().ToUpperInvariant() };
    public static ColumnSource Skip() => new();

    public override string ToString() =>
        !string.IsNullOrWhiteSpace(Header) ? $"heading \"{Header}\""
        : !string.IsNullOrWhiteSpace(Column) ? $"column {Column}"
        : "not imported";
}

/// <summary>
/// Configurable spreadsheet → Order column mapping for the Orders import, one per source type
/// (VADATA / Tableau / Open Lines), stored in AppConfig "OrderColumnMap_{type}" so everyone on
/// the shared database imports the same way. The built-in defaults are exactly the fixed
/// column positions the three original parsers used, so nothing changes until someone edits it.
/// Edited in Configuration → Source Data → Edit Column Mapping (Dialogs/OrderColumnMappingDialog).
/// </summary>
public static class OrderColumnMapping
{
    public const string ConfigKeyPrefix = "OrderColumnMap_";
    public static readonly string[] SourceTypes = { "VADATA", "Tableau", "OpenLines" };

    public static string SourceLabel(string type) => type switch
    {
        "Tableau"   => "Tableau (Details.xlsx)",
        "OpenLines" => "Open Lines Report",
        _           => "VADATA",
    };

    /// <summary>Every field an import can fill, in display order.</summary>
    public static readonly IReadOnlyList<OrderImportField> Fields = new OrderImportField[]
    {
        new("JobName",                 "Job Name  (→ cluster / site / ROMP)", OrderFieldKind.Text, Required: true),
        new("CustomerOrder",           "Customer Order",                      OrderFieldKind.Text, Required: true),
        new("OrdLine",                 "Order Line",                          OrderFieldKind.Text, Required: true),
        new("CustomerPO",              "Customer PO",                         OrderFieldKind.Text),
        new("CustPOLine",              "Customer PO Line",                    OrderFieldKind.Text),
        new("CreditHold",              "Credit Hold",                         OrderFieldKind.Text),
        new("ShipHold",                "Ship Hold",                           OrderFieldKind.Text),
        new("FaceSite",                "Face Site",                           OrderFieldKind.Text),
        new("Slsman",                  "Salesperson",                         OrderFieldKind.Text),
        new("OrderStatus",             "Order Status",                        OrderFieldKind.Text),
        new("OrderLineStatus",         "Order Line Status",                   OrderFieldKind.Text),
        new("AssignSite",              "Assign Site",                         OrderFieldKind.Text),
        new("AssignWhse",              "Assign Warehouse",                    OrderFieldKind.Text),
        new("IPN",                     "IPN",                                 OrderFieldKind.Text),
        new("Item",                    "Item",                                OrderFieldKind.Text),
        new("ItemDescription",         "Item Description",                    OrderFieldKind.Text),
        new("ItemDesignation",         "Item Designation",                    OrderFieldKind.Text),
        new("ProductCode",             "Product Code",                        OrderFieldKind.Text),
        new("Price",                   "Price",                               OrderFieldKind.Number),
        new("ExtPrice",                "Ext Price",                           OrderFieldKind.Number),
        new("TotalCOPrice",            "Total CO Price",                      OrderFieldKind.Number),
        new("SectionCount",            "Section Count",                       OrderFieldKind.Integer),
        new("QtyOrd",                  "Qty Ordered",                         OrderFieldKind.Number),
        new("QtyShip",                 "Qty Shipped",                         OrderFieldKind.Number),
        new("QtyInvoiced",             "Qty Invoiced",                        OrderFieldKind.Number),
        new("OrderDate",               "Order Date",                          OrderFieldKind.Date),
        new("PromiseDate",             "Promise Date",                        OrderFieldKind.Date),
        new("ShipDate",                "Ship Date",                           OrderFieldKind.Date),
        new("LastShipDate",            "Last Ship Date",                      OrderFieldKind.Date),
        new("DeliveryDate",            "Delivery Date",                       OrderFieldKind.Date),
        new("WarrantyExpiration",      "Warranty Expiration",                 OrderFieldKind.Date),
        new("EstimatedDeliveryDate",   "Estimated Delivery Date",             OrderFieldKind.Date),
        new("EstimatedCompletionDate", "Estimated Completion Date",           OrderFieldKind.Date),
        new("ActualCompletionDate",    "Actual Completion Date",              OrderFieldKind.Date),
        new("RequestedCompletion",     "Requested Completion",                OrderFieldKind.Date),
        new("ProductionVariance",      "Production Variance",                 OrderFieldKind.Text),
        new("RequestedOSD",            "Requested OSD",                       OrderFieldKind.Text),
        new("DaysLate",                "Days Late",                           OrderFieldKind.Integer),
        new("PreviousEstCompletionDate","Previous Est. Completion Date",      OrderFieldKind.Date),
        new("EstCompletionDateChange", "Est. Completion Date Change",         OrderFieldKind.Date),
        new("EstCompletionDateChangeReason", "Est. Completion Change Reason", OrderFieldKind.Text),
        new("ScheduledStartDate",      "Scheduled Start Date",                OrderFieldKind.Date),
        new("ActualStartDate",         "Actual Start Date",                   OrderFieldKind.Date),
        new("InvoiceDate",             "Invoice Date",                        OrderFieldKind.Date),
        new("CustNum",                 "Customer Number",                     OrderFieldKind.Text),
        new("CustomerName",            "Customer Name",                       OrderFieldKind.Text),
        new("OrderShipTo",             "Order Ship To",                       OrderFieldKind.Text),
        new("LineItemShipTo",          "Line Item Ship To",                   OrderFieldKind.Text),
        new("SalesTax1",               "Sales Tax T",                         OrderFieldKind.Text),
        new("SalesTax2",               "Sales Tax T2",                        OrderFieldKind.Text),
        new("ProjectManagerDataName",  "Project Manager (Data Name)",         OrderFieldKind.Text),
    };

    public static OrderImportField Field(string key) => Fields.First(f => f.Key == key);

    /// <summary>A row is skipped when all of these fields are empty — the same rule each original
    /// parser used, expressed as fields so it follows the mapping.</summary>
    public static IReadOnlyList<string> RowKeyFields(string type) => type switch
    {
        "Tableau"   => new[] { "CustomerPO", "JobName" },
        "OpenLines" => new[] { "CustomerOrder", "JobName" },
        _           => new[] { "CustomerPO", "JobName", "Item" },
    };

    /// <summary>The original fixed positions, as column letters. Fields absent here default to "not imported".</summary>
    public static Dictionary<string, ColumnSource> Defaults(string type)
    {
        var letters = type switch
        {
            "Tableau" => new (string, string)[]
            {
                ("CustomerPO","A"), ("CustPOLine","B"), ("CreditHold","C"), ("ShipHold","D"), ("FaceSite","E"),
                ("CustomerOrder","F"), ("Slsman","G"), ("OrdLine","H"), ("OrderStatus","I"), ("AssignSite","J"),
                ("AssignWhse","K"), ("JobName","L"), ("IPN","M"), ("Item","N"), ("ItemDescription","O"),
                ("ItemDesignation","P"), ("ProductCode","Q"), ("Price","R"), ("ExtPrice","S"), ("SectionCount","T"),
                ("QtyOrd","U"), ("QtyShip","V"), ("ShipDate","W"), ("DeliveryDate","X"), ("WarrantyExpiration","Y"),
                ("EstimatedDeliveryDate","Z"), ("EstimatedCompletionDate","AA"), ("ActualCompletionDate","AB"),
                ("RequestedCompletion","AC"), ("ProductionVariance","AD"), ("RequestedOSD","AE"), ("DaysLate","AF"),
                ("PreviousEstCompletionDate","AG"), ("EstCompletionDateChange","AH"), ("EstCompletionDateChangeReason","AI"),
                ("ScheduledStartDate","AJ"), ("ActualStartDate","AK"), ("InvoiceDate","AL"), ("CustNum","AM"),
                ("OrderShipTo","AN"), ("LineItemShipTo","AO"),
            },
            "OpenLines" => new (string, string)[]
            {
                ("FaceSite","A"), ("AssignSite","B"), ("CustomerOrder","C"), ("OrderDate","D"), ("OrdLine","E"),
                ("OrderStatus","F"), ("OrderLineStatus","G"), ("PromiseDate","H"), ("LastShipDate","I"), ("Item","J"),
                ("ItemDescription","K"), ("QtyOrd","L"), ("Price","M"), ("ExtPrice","N"), ("QtyInvoiced","O"),
                ("QtyShip","P"), ("JobName","Q"), ("SalesTax1","S"), ("SalesTax2","T"), ("TotalCOPrice","U"),
                ("ProjectManagerDataName","V"), ("CustNum","W"), ("CustomerName","X"), ("Slsman","Y"),
            },
            _ => new (string, string)[]
            {
                ("CustomerPO","A"), ("CustPOLine","B"), ("ShipHold","C"), ("FaceSite","D"), ("CustomerOrder","E"),
                ("OrdLine","F"), ("AssignSite","G"), ("AssignWhse","H"), ("JobName","I"), ("IPN","J"), ("Item","K"),
                ("ItemDescription","L"), ("ItemDesignation","M"), ("QtyOrd","N"), ("QtyShip","O"), ("ShipDate","P"),
                ("DeliveryDate","Q"), ("WarrantyExpiration","R"), ("EstimatedDeliveryDate","S"),
                ("EstimatedCompletionDate","T"), ("ActualCompletionDate","U"), ("RequestedCompletion","V"),
                ("ProductionVariance","W"), ("RequestedOSD","X"), ("DaysLate","Y"), ("PreviousEstCompletionDate","Z"),
                ("EstCompletionDateChange","AA"), ("ScheduledStartDate","AB"), ("ActualStartDate","AC"),
                ("InvoiceDate","AD"), ("CustNum","AE"), ("OrderShipTo","AF"), ("LineItemShipTo","AG"),
            },
        };
        var map = Fields.ToDictionary(f => f.Key, _ => ColumnSource.Skip());
        foreach (var (key, letter) in letters) map[key] = ColumnSource.FromColumn(letter);
        return map;
    }

    /// <summary>The saved mapping for <paramref name="type"/>, laid over the defaults (so a field
    /// added in a later version starts at its default; a saved "not imported" stays skipped).</summary>
    public static Dictionary<string, ColumnSource> Load(string type)
    {
        var map  = Defaults(type);
        var json = new ConfigService().Get(ConfigKeyPrefix + type);
        if (string.IsNullOrWhiteSpace(json)) return map;
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, ColumnSource>>(json);
            if (saved != null)
                foreach (var (key, src) in saved)
                    if (map.ContainsKey(key)) map[key] = src ?? ColumnSource.Skip();
        }
        catch { /* unreadable → defaults; the dialog shows them and saving rewrites it */ }
        return map;
    }

    public static bool IsCustomised(string type) =>
        !string.IsNullOrWhiteSpace(new ConfigService().Get(ConfigKeyPrefix + type));

    public static void Save(string type, Dictionary<string, ColumnSource> map) =>
        new ConfigService().Set(ConfigKeyPrefix + type, JsonSerializer.Serialize(map));

    /// <summary>Back to the built-in positions (removes the saved mapping's content).</summary>
    public static void Reset(string type) => new ConfigService().Set(ConfigKeyPrefix + type, "");

    // ── Column letters ────────────────────────────────────────────────────────

    public static bool IsColumnLetter(string s) =>
        s.Length is >= 1 and <= 3 && s.All(c => c is >= 'A' and <= 'Z') && ColumnIndex(s) <= 16384;

    public static int ColumnIndex(string letters)
    {
        int n = 0;
        foreach (var c in letters.Trim().ToUpperInvariant()) n = n * 26 + (c - 'A' + 1);
        return n;
    }

    public static string ColumnLetter(int index)
    {
        var s = "";
        for (; index > 0; index = (index - 1) / 26) s = (char)('A' + (index - 1) % 26) + s;
        return s;
    }

    /// <summary>Heading comparison key: case-insensitive, whitespace collapsed.</summary>
    public static string NormalizeHeading(string? s) =>
        string.Join(' ', (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
