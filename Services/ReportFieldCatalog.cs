using PMDashboard.Data.Models;

namespace PMDashboard.Services;

public enum ReportFieldType { Text, Number, Date, Bool }

/// <summary>Describes one reportable field: its key (stable, used in SelectedColumnsJson), display name, and type (drives filter operator UI).</summary>
public class ReportField
{
    public string          Key         { get; init; } = "";
    public string          DisplayName { get; init; } = "";
    public ReportFieldType FieldType   { get; init; }
}

/// <summary>One ROMP row with its Site/Location/PM ancestry flattened in — the natural grain of the Sites/ROMP report.</summary>
public class SiteRompFlatRow
{
    public int      RompId               { get; init; }
    public string   LocationName         { get; init; } = "";
    public string   GeographicalLocation { get; init; } = "";
    public string   SiteName             { get; init; } = "";
    public string   SiteStatus           { get; init; } = "";
    public string   AssignedPmName       { get; init; } = "";
    public string   RompName             { get; init; } = "";
    public string   RompStatus           { get; init; } = "";
    public string   EquipmentTypes       { get; init; } = "";
    public DateTime LastModified         { get; init; }
}

/// <summary>A single result row, keyed by field Key so the results DataGrid can bind dynamically chosen columns.</summary>
public class ReportRow
{
    public Dictionary<string, object?> Values { get; } = new();
}

public static class ReportFieldCatalog
{
    public static readonly IReadOnlyList<ReportField> OrderFields = new List<ReportField>
    {
        new() { Key = "JobName",                DisplayName = "Job Name",           FieldType = ReportFieldType.Text },
        new() { Key = "CustomerName",            DisplayName = "Customer",           FieldType = ReportFieldType.Text },
        new() { Key = "CustomerOrder",           DisplayName = "Customer Order",     FieldType = ReportFieldType.Text },
        new() { Key = "CustNum",                 DisplayName = "Customer #",         FieldType = ReportFieldType.Text },
        new() { Key = "ProjectManagerDataName",  DisplayName = "PM Data Name",       FieldType = ReportFieldType.Text },
        new() { Key = "Slsman",                  DisplayName = "Salesperson",        FieldType = ReportFieldType.Text },
        new() { Key = "OrderStatus",              DisplayName = "Order Status",       FieldType = ReportFieldType.Text },
        new() { Key = "OrderLineStatus",          DisplayName = "Order Line Status",  FieldType = ReportFieldType.Text },
        new() { Key = "Item",                    DisplayName = "Item",               FieldType = ReportFieldType.Text },
        new() { Key = "ItemDescription",          DisplayName = "Item Description",   FieldType = ReportFieldType.Text },
        new() { Key = "QtyOrd",                  DisplayName = "Qty Ordered",        FieldType = ReportFieldType.Number },
        new() { Key = "QtyShip",                 DisplayName = "Qty Shipped",        FieldType = ReportFieldType.Number },
        new() { Key = "Price",                   DisplayName = "Price",              FieldType = ReportFieldType.Number },
        new() { Key = "ExtPrice",                DisplayName = "Ext. Price",         FieldType = ReportFieldType.Number },
        new() { Key = "TotalCOPrice",             DisplayName = "Total CO Price",     FieldType = ReportFieldType.Number },
        new() { Key = "DaysLate",                DisplayName = "Days Late",          FieldType = ReportFieldType.Number },
        new() { Key = "OrderDate",               DisplayName = "Order Date",         FieldType = ReportFieldType.Date },
        new() { Key = "ShipDate",                DisplayName = "Ship Date",          FieldType = ReportFieldType.Date },
        new() { Key = "DeliveryDate",             DisplayName = "Delivery Date",      FieldType = ReportFieldType.Date },
        new() { Key = "PromiseDate",              DisplayName = "Promise Date",       FieldType = ReportFieldType.Date },
        new() { Key = "RequestedCompletion",      DisplayName = "Requested Completion", FieldType = ReportFieldType.Date },
    };

    /// <summary>Date-typed Order fields eligible as the date-range filter target.</summary>
    public static IEnumerable<ReportField> OrderDateFields =>
        OrderFields.Where(f => f.FieldType == ReportFieldType.Date);

    public static readonly IReadOnlyList<ReportField> SiteRompFields = new List<ReportField>
    {
        new() { Key = "LocationName",         DisplayName = "Location",          FieldType = ReportFieldType.Text },
        new() { Key = "GeographicalLocation", DisplayName = "Region",            FieldType = ReportFieldType.Text },
        new() { Key = "SiteName",             DisplayName = "Site",              FieldType = ReportFieldType.Text },
        new() { Key = "SiteStatus",           DisplayName = "Site Status",       FieldType = ReportFieldType.Text },
        new() { Key = "AssignedPmName",       DisplayName = "Assigned PM",       FieldType = ReportFieldType.Text },
        new() { Key = "RompName",             DisplayName = "ROMP",              FieldType = ReportFieldType.Text },
        new() { Key = "RompStatus",           DisplayName = "ROMP Status",       FieldType = ReportFieldType.Text },
        new() { Key = "EquipmentTypes",       DisplayName = "Equipment Types",   FieldType = ReportFieldType.Text },
        new() { Key = "LastModified",         DisplayName = "Last Modified",     FieldType = ReportFieldType.Date },
    };

    public static object? GetOrderValue(Order o, string key) => key switch
    {
        "JobName"               => o.JobName,
        "CustomerName"           => o.CustomerName,
        "CustomerOrder"          => o.CustomerOrder,
        "CustNum"                => o.CustNum,
        "ProjectManagerDataName" => o.ProjectManagerDataName,
        "Slsman"                 => o.Slsman,
        "OrderStatus"            => o.OrderStatus,
        "OrderLineStatus"        => o.OrderLineStatus,
        "Item"                   => o.Item,
        "ItemDescription"        => o.ItemDescription,
        "QtyOrd"                 => o.QtyOrd,
        "QtyShip"                => o.QtyShip,
        "Price"                  => o.Price,
        "ExtPrice"               => o.ExtPrice,
        "TotalCOPrice"           => o.TotalCOPrice,
        "DaysLate"               => o.DaysLate,
        "OrderDate"              => o.OrderDate,
        "ShipDate"               => o.ShipDate,
        "DeliveryDate"           => o.DeliveryDate,
        "PromiseDate"            => o.PromiseDate,
        "RequestedCompletion"    => o.RequestedCompletion,
        _                        => null,
    };

    public static object? GetSiteRompValue(SiteRompFlatRow r, string key) => key switch
    {
        "LocationName"         => r.LocationName,
        "GeographicalLocation" => r.GeographicalLocation,
        "SiteName"             => r.SiteName,
        "SiteStatus"           => r.SiteStatus,
        "AssignedPmName"       => r.AssignedPmName,
        "RompName"             => r.RompName,
        "RompStatus"           => r.RompStatus,
        "EquipmentTypes"       => r.EquipmentTypes,
        "LastModified"         => r.LastModified,
        _                      => null,
    };

    public static ReportRow BuildOrderRow(Order o)
    {
        var row = new ReportRow();
        foreach (var f in OrderFields) row.Values[f.Key] = GetOrderValue(o, f.Key);
        return row;
    }

    public static ReportRow BuildSiteRompRow(SiteRompFlatRow r)
    {
        var row = new ReportRow();
        foreach (var f in SiteRompFields) row.Values[f.Key] = GetSiteRompValue(r, f.Key);
        return row;
    }
}
