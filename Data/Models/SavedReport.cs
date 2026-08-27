namespace PMDashboard.Data.Models;

/// <summary>
/// A saved Report Builder preset — mode, filters, and selected columns, serialized as JSON
/// so the report schema can evolve without a table-shape migration.
/// </summary>
public class SavedReport
{
    public int    Id     { get; set; }
    public string Name   { get; set; } = "";
    public string Mode   { get; set; } = "Orders";   // "Orders" | "SitesRomps"

    public string FilterJson          { get; set; } = "{}";
    public string SelectedColumnsJson { get; set; } = "[]";

    public string? ChartGroupByField    { get; set; }
    public string? ChartAggregateField  { get; set; }
    public string  ChartAggregateOp     { get; set; } = "Count"; // "Count" | "Sum"

    public DateTime LastModified { get; set; }
}
