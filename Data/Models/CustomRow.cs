namespace PMDashboard.Data.Models;

/// <summary>
/// Dynamic-column values for one real row of the Custom Data grid (an Order or a flattened
/// ROMP, per Mode) — NOT a standalone record. EntityId is that Order's/Romp's Id. A row only
/// gets a CustomRow once the user enters at least one dynamic value for it; Orders/ROMPs with
/// no custom data simply have no matching CustomRow. Values are stored as a JSON object keyed
/// by CustomColumn.Key rather than as typed columns, since the column set is user-configurable.
/// </summary>
public class CustomRow
{
    public int    Id         { get; set; }
    public string Mode       { get; set; } = "Orders"; // "Orders" | "SitesRomps" — matches CustomColumn.Mode
    public int    EntityId   { get; set; }             // Order.Id or Romp.Id, depending on Mode
    public string ValuesJson { get; set; } = "{}";

    // Unused — kept only because installs from before this redesign already have this column
    // as NOT NULL with no default; removing it from the model makes EF's INSERT omit it and
    // violate that constraint. A fresh install's CREATE TABLE also defines it, DEFAULT 0.
    public int SortOrder { get; set; }

    public DateTime LastModified { get; set; }
}
