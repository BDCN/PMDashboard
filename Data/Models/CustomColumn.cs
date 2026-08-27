namespace PMDashboard.Data.Models;

/// <summary>
/// A user-defined column on the Custom Data grid, scoped to one mode ("Orders" | "SitesRomps")
/// since the two modes have unrelated row sets. Key is generated once at creation (col{Id})
/// and never changes, so renaming Name never orphans existing row values.
/// </summary>
public class CustomColumn
{
    public int    Id         { get; set; }
    public string Mode       { get; set; } = "Orders"; // "Orders" | "SitesRomps"
    public string Name       { get; set; } = "";
    public string Key        { get; set; } = "";
    public string ColumnType { get; set; } = "Text"; // "Text" | "Number" | "Date" | "Bool"
    public int    SortOrder  { get; set; }

    public DateTime LastModified { get; set; }
}
