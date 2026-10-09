namespace PMDashboard.Data.Models;

/// <summary>Team roles, top → bottom. Platform Managers ("PM") are the existing <see cref="PM"/> rows.</summary>
public static class TeamRoles
{
    public const string Director = "Director";
    public const string Pe       = "PE";
    public const string Pm       = "PM";
    public const string Pa       = "PA";
}

/// <summary>
/// A Director, Project Executive (PE) or Project Administrator (PA). Platform Managers stay in the
/// PMs table (everything assigns to them) and join the team through <see cref="PM.PeId"/>.
/// Director → controls a Region (East/Central/West); PE → controls a <see cref="SubRegion"/>;
/// PA → reports to one PM.
/// </summary>
public class TeamMember
{
    public int    Id        { get; set; }
    public string Role      { get; set; } = TeamRoles.Pe;
    public string FirstName { get; set; } = "";
    public string LastName  { get; set; } = "";
    public string Email     { get; set; } = "";
    public string Phone     { get; set; } = "";

    /// <summary>Director only: the region they control.</summary>
    public string Region { get; set; } = "";

    /// <summary>PE only: the sub-region they control.</summary>
    public int?       SubRegionId { get; set; }
    public SubRegion? SubRegion   { get; set; }

    /// <summary>PA only: the Platform Manager they support.</summary>
    public int? PmId { get; set; }
    public PM?  Pm   { get; set; }

    /// <summary>A "TBD" placeholder (e.g. a PA still to be hired). "Fill" turns it into the real person.</summary>
    public bool     IsPlaceholder { get; set; }

    public bool     IsActive     { get; set; } = true;
    public DateTime LastModified { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();
}
