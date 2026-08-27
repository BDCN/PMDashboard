namespace PMDashboard.Data.Models;

/// <summary>
/// A scoring category (e.g. PAX, LV, MV, PDU).
/// One category can match against multiple keywords — see EquipmentKeyword.
/// IsExcluded categories are checked first; matching order lines are skipped entirely.
/// </summary>
public class EquipmentCategory
{
    public int    Id         { get; set; }
    public string Name       { get; set; } = "";   // "PAX", "LV", "MV", "PDU", "FS Exclusion"
    public double Weight     { get; set; }          // Equipment weight added to a ROMP's equip_weight_sum when this category is present (0 for exclusions)
    public bool   IsExcluded { get; set; }          // True → skip the line, don't score it
    public bool   IsBuiltIn  { get; set; }          // Cannot be deleted
    public int    SortOrder  { get; set; }

    public DateTime LastModified { get; set; }
    public ICollection<EquipmentKeyword> Keywords { get; set; } = new List<EquipmentKeyword>();
}
