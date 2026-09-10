namespace PMDashboard.Data.Models;

/// <summary>
/// One recorded user interaction. Append-only: rows are never updated or deleted by the app,
/// so an entry is a permanent statement about what happened.
///
/// UserEmail is stored as text rather than a FK to Users, so the trail survives an account
/// being renamed or deactivated — an audit row that changes retroactively is not an audit row.
/// </summary>
public class AuditEntry
{
    public int      Id           { get; set; }
    public DateTime TimestampUtc { get; set; }

    public string UserEmail { get; set; } = "";
    public string UserName  { get; set; } = "";

    /// <summary>Verb: "Login", "Create", "Update", "Delete", "Import", "Export", "Navigate", ...</summary>
    public string Action { get; set; } = "";

    /// <summary>What was acted on: "PM", "Site", "Romp", "Order", "CustomColumn", "Config", ...</summary>
    public string EntityType { get; set; } = "";

    /// <summary>Id of the affected record, when there is a single one.</summary>
    public int? EntityId { get; set; }

    /// <summary>Human-readable specifics, e.g. "Capacity 8 -> 10".</summary>
    public string Details { get; set; } = "";

    public string MachineName { get; set; } = "";

    public DateTime LastModified { get; set; }
}
