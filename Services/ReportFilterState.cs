namespace PMDashboard.Services;

/// <summary>
/// Mode-agnostic filter bag for the Report Builder. Only the fields relevant to the
/// active mode are populated; this is what gets JSON-serialized into SavedReport.FilterJson.
/// </summary>
public class ReportFilterState
{
    // Orders mode: which date field the range applies to (defaults to OrderDate).
    public string?   DateField { get; set; }
    public DateTime? DateFrom  { get; set; }
    public DateTime? DateTo    { get; set; }

    // Sites/ROMP mode multi-selects
    public List<int>? LocationIds { get; set; }
    public List<int>? SiteIds     { get; set; }
    public List<int>? PmIds       { get; set; }

    public List<string>? RompStatuses  { get; set; }
    public List<string>? OrderStatuses { get; set; }

    public string? TextSearch { get; set; }
}
