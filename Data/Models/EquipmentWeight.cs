namespace PMDashboard.Data.Models;

/// <summary>
/// Defines how order line items contribute to a ROMP's difficulty score.
/// Matched against ItemDescription of orders where Item starts with the configured prefix (default "SOLI").
/// IsExcluded entries (FSPS, FSWTY, FS Field Service) are checked first — matching orders are skipped entirely.
/// </summary>
public class EquipmentWeight
{
    public int    Id         { get; set; }
    public string Name       { get; set; } = "";    // Display label e.g. "PDU", "LV", "FSPS (Excluded)"
    public string Keyword    { get; set; } = "";    // Substring matched in ItemDescription (case-insensitive)
    public int    Weight     { get; set; }          // Score added per matching order line (0 for exclusions)
    public bool   IsExcluded { get; set; }          // True → matching lines are skipped, not scored
    public bool   IsBuiltIn  { get; set; }          // Cannot be deleted
    public int    SortOrder  { get; set; }
}
