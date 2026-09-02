namespace PMDashboard.Services;

/// <param name="DuplicatesSkipped">
/// Rows whose (CustomerOrder, OrdLine) key repeated — either already duplicated in the
/// database or repeated within the imported file. Defaulted so existing call sites and
/// any persisted results keep compiling.
/// </param>
public record ImportResult(
    int Added, int Updated, int Skipped,
    int LocationsCreated, int SitesCreated, int RompsCreated,
    int DuplicatesSkipped = 0);
