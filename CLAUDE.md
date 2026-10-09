# Project PMDashboard

Windows desktop application for resource management — tracks Project Managers, client sites, ROMPs, and imported order/job data.

## Tech Stack

- **Framework:** WPF (.NET 8, `net8.0-windows`)
- **Language:** C#
- **Database:** SQLite (local/network) via Entity Framework Core 8 or MariaDB (cloud) via Pomelo EF Core 8
- **MVVM:** CommunityToolkit.Mvvm 8.3.2 (ObservableObject, ObservableProperty, RelayCommand)
- **Excel Import:** ClosedXML 0.102.3
- **Cloud/MySQL:** Pomelo.EntityFrameworkCore.MySql 8.0.2 + MySqlConnector 2.3.7
- **Local DB file:** `%LOCALAPPDATA%\PMDashboard\data.db`
- **DB config file:** `%APPDATA%\PMDashboard\dbconfig.json` (database mode, network path, cloud connection string)
- **Favicon:** `favicon.ico` in project root (from iem.co), built as `<Resource>`
- **IEM logo:** `Assets/iem-logo.png` in project root, built as `<Resource>` — displayed in app header

## Project Structure

```
PMDashboard/
├── App.xaml                        # Global styles, DataTemplates, FormPasswordBoxStyle
├── App.xaml.cs                     # DB EnsureCreated, schema migrations, seeds, AutoImportService, CloudSyncService
├── MainWindow.xaml                 # Shell: 64px white header (IEM logo) + collapsible sidebar + ContentControl
├── MainWindow.xaml.cs              # Nav click handlers, sidebar toggle, geo filter ComboBox
├── favicon.ico
├── Assets/
│   └── iem-logo.png                # IEM company logo shown in app header
├── Data/
│   ├── AppDbContext.cs             # EF Core context; ForceCloud param; SkipLastModifiedStamp; LocalSaved event; auto-stamps LastModified
│   └── Models/
│       ├── PM.cs                   # PM entity + LastModified
│       ├── Location.cs             # Grandparent entity (3-letter code, GeographicalLocation) + LastModified
│       ├── Campus.cs               # Cluster → Campus → Site level: FK → Location, FK? → PM (campus PM, cascades to sites), SiteCount/OverrideCount/CampusStatus/PmDisplay computed
│       ├── SubRegion.cs            # Team sub-region (e.g. "East 1") in a Region; clusters map to one via Location.SubRegionId
│       ├── TeamMember.cs           # Director (Region) / PE (SubRegionId) / PA (PmId) + TeamRoles constants; PMs join via PM.PeId
│       ├── Site.cs                 # FK → Location, FK? → Campus, FK? → PM, CampusManual, PmOverride, IsClosed (persisted), SiteStatus computed (folds in IsClosed only when Romps is empty), LastModified
│       ├── Romp.cs                 # FK → Site, Status, EquipmentTypes nav collection, LastModified
│       ├── RompEquipmentType.cs    # Junction: Romp ↔ EquipmentCategory (many-to-many) + LastModified
│       ├── RompStatus.cs           # Status config: IsComplete, CountsAsActive, ScoreWeight (double) + LastModified
│       ├── EquipmentCategory.cs    # Equipment scoring category: Weight (double), IsExcluded + LastModified
│       ├── EquipmentKeyword.cs     # Keyword matched in ItemDescription (FK → EquipmentCategory) + LastModified
│       ├── EquipmentWeight.cs      # Legacy flat weight table — kept for schema compat only
│       ├── Order.cs                # 51-field import row (VADATA + Tableau + OpenLines) + LastModified
│       ├── AppConfig.cs            # Key/Value config storage (PK = Key) + LastModified
│       ├── AssignmentRule.cs       # PM assignment rules (IsBuiltIn, Weight, SortOrder, RuleKey) + LastModified
│       ├── SavedReport.cs          # Report Builder preset: Mode, FilterJson, SelectedColumnsJson, chart config + LastModified
│       ├── CustomColumn.cs         # Custom Data grid's user-defined column: Name, Key (stable, "col{Id}"), ColumnType + LastModified
│       ├── CustomRow.cs            # Custom Data grid row: ValuesJson (dict keyed by CustomColumn.Key) + LastModified
│       ├── User.cs                 # Account: Email (username), PasswordHash, Role (SuperAdmin|User), IsActive, MustChangePassword + LastModified
│       └── AuditEntry.cs           # Audit log row: TimestampUtc, UserEmail/UserName, Action, EntityType, EntityId?, Details, MachineName + LastModified
├── ViewModels/
│   ├── MainViewModel.cs            # CurrentPage, GeoFilter, Navigate* methods incl. NavigateToPmDetail + NavigateToSiteRompsFromPm
│   ├── PmMaintenanceViewModel.cs   # Live-edit grid, geo filter, BulkAddPms, GetAllPms
│   ├── SitesViewModel.cs           # 3-level drill-down; NavigateDirectToSiteRomps; BackButtonLabel/NavigateBackAction
│   ├── OrdersViewModel.cs          # UpsertOrders + ImportWithLockAsync (import lock, local import, cloud push-and-wait), last-import summary properties
│   ├── WorkloadViewModel.cs        # New multiplicative scoring formula, mode-aware KPI counts (Overloaded/NearFull/HasCapacity)
│   ├── PmDetailViewModel.cs        # PM detail: BulkUnassignSites, NavigateToSiteRomps delegate, CurrentPmId
│   ├── AssignmentViewModel.cs      # Multi-select site ListBox, SetSelectedSites, ComputeCandidates uses SelectedLocation
│   ├── ConfigViewModel.cs          # All config: DB mode, workload params (double), IsSuperAdmin (gates the whole Configuration page), SyncEquipmentFromOrders, Delete Local Data
│   ├── ReportBuilderViewModel.cs   # Mode (Orders/SitesRomps) toggle, filters, column picker, saved-report CRUD, chart config
│   └── CustomDataViewModel.cs      # Custom Data: paginated real Orders/Sites&ROMPs rows (reuses ReportQueryService) + attached dynamic columns; CustomDataRow (Static/DynamicValues), row CRUD, column CRUD (shared with ManageColumnsDialog), Excel-style column filters, persistent column order
├── Views/
│   ├── PmMaintenancePage.xaml/.cs  # Live-edit DataGrid — includes Score Cap column
│   ├── SitesPage.xaml/.cs          # Drill-down: Locations → Sites → ROMPs; ← Back-to-PM button when navigated from PM Detail
│   ├── OrdersPage.xaml/.cs         # Import section only (no data grid) — Import Excel / Clear All, persistent "Last Import" results card, import-in-progress indicator
│   ├── WorkloadPage.xaml/.cs       # Bar chart; mode-aware KPI cards; Score mode: Overloaded/Near Full/Has Capacity
│   ├── PmDetailPage.xaml/.cs       # PM detail: checkbox bulk-unassign, Edit/Unassign per-site, clickable site name → ROMs
│   ├── AssignmentPage.xaml/.cs     # Multi-select ListBox (Ctrl+Click/Shift+Click); All/Unassigned/Clear buttons; geo badge
│   ├── ConfigPage.xaml/.cs         # Reordered cards; sliders on params/statuses/equipment; single admin lock; Danger Zone (Delete Local Data); Manage Users card (SuperAdmin-gated)
│   ├── ReportBuilderPage.xaml/.cs  # Filter panel + dynamic results grid + bar chart + saved-report strip; grid/picker columns built in code-behind, driven by VM PropertyChanged
│   └── CustomDataPage.xaml/.cs     # Mode toggle + date-range filter over real Orders/Sites&ROMPs data, paginated results, Excel-style column header filters, editable dynamic columns with accent styling, per-row History link; DataGrid.Columns built in code-behind, driven by VM PropertyChanged
├── Dialogs/
│   ├── AddEditPmDialog.xaml/.cs
│   ├── AddEditSiteDialog.xaml/.cs  # BulkRompStatus loaded from StatusConfig
│   ├── AddEditRompDialog.xaml/.cs  # Status dropdown + multi-select equipment type checkboxes
│   ├── AddEditLocationDialog.xaml/.cs
│   ├── AddEditRuleDialog.xaml/.cs
│   ├── AddEditStatusDialog.xaml/.cs
│   ├── AddEquipmentWeightDialog.xaml/.cs
│   ├── AssignPmDialog.xaml/.cs
│   ├── MoveSiteDialog.xaml/.cs     # Sites page "Move…": re-associate a site (and its ROMPs) to an existing or new campus in its cluster
│   ├── TeamMemberDialog.xaml/.cs   # Teams page: add/edit Director/PE/PA, or (PM mode) pick the PE a Platform Manager reports to
│   ├── SubRegionDialog.xaml/.cs    # Teams page: add/rename a sub-region (name + region)
│   ├── TextInputDialog.xaml/.cs    # Generic single-line text prompt (Report Builder Save As / Rename, Delete Local Data's type-DELETE-to-confirm)
│   ├── ManageColumnsDialog.xaml/.cs # Custom Data's column setup screen — add/rename/reorder/delete CustomColumns; shares the CustomDataViewModel instance passed in from CustomDataPage
│   ├── RowHistoryDialog.xaml/.cs   # Custom Data's per-row "History" link — read-only DataGrid of AuditService.ForEntity(entityType, entityId)
│   ├── LoginDialog.xaml/.cs        # Email + password sign-in; generic "incorrect" error (no user enumeration); forces ChangePasswordDialog when MustChangePassword
│   ├── ChangePasswordDialog.xaml/.cs # Current + new + confirm password; "Sign Out" instead of "Cancel" when the change is mandatory
│   ├── UserProfileDialog.xaml/.cs  # Signed-in user edits their own name/email, or changes their own password
│   ├── CreateAdminAccountDialog.xaml/.cs # First-run only (Users table empty): the first real person sets their own email + password directly — no placeholder password ever in source
│   ├── AddEditUserDialog.xaml/.cs  # Config → Manage Users → "+ Add User": creates an account via UserService.CreateUser (MustChangePassword defaults true)
│   └── ResetUserPasswordDialog.xaml/.cs # Config → Manage Users → "Reset Password": admin sets a new password directly via UserService.AdminResetPassword (MustChangePassword forced true)
└── Services/
    ├── ExcelImportService.cs       # ImportOrders (VADATA), ImportTableauOrders, ImportOpenLinesOrders
    ├── PmImportService.cs          # PM bulk template generate/parse
    ├── SiteAssignmentImportService.cs # Bulk-assigns PMs to already-existing Sites from Excel (template generate/parse); skips+reports conflicts rather than overwriting
    ├── SitesImportService.cs       # Bulk-creates/updates Sites+ROMPs (with Status) from Excel (template generate/parse); upserts — re-importing updates existing ROMPs' Status
    ├── ConfigService.cs            # Get/Set AppConfig rows; ConfigKeys constants
    ├── StatusConfig.cs             # App-lifetime cache: status lists + ScoreWeights (Dictionary<string,double>)
    ├── ScoreConfig.cs              # App-lifetime cache: CategoryRule (double Weight), WorkloadParams, EquipmentWeightSum
    ├── ScoringEngine.cs            # Pure stateless scoring logic (testable): RompScore, PmLoad, Availability, PmStatus
    ├── DatabaseConfig.cs           # Persistent DB mode config in %APPDATA%\PMDashboard\dbconfig.json
    ├── SyncService.cs              # Network travel sync (SQLite ATTACH bidirectional merge) + MariaDB DDL script
    ├── CloudSyncService.cs         # Background cloud sync: initial pull + periodic push/pull via LastModified; PushNowAndWaitAsync (import-lock flow); DataChanged event (cross-user notifications)
    ├── AutoImportService.cs        # Background timer, file polling, seen-file dedup, RunUpsert (shares OrderMerge with the manual import path); ImportCompleted event
    ├── ImportLockService.cs        # Cloud-mode-only best-effort cross-user import lock, stored as an AppConfig row on the cloud DB directly (ForceCloud)
    ├── AppVersion.cs               # Running build's version (<Version> in PMDashboard.csproj) + build time; Short "v1.2.3" / Full "v1.2.3 (built …)"
    ├── CampusService.cs            # Campus PM assign (cascades to non-override sites), ClearOverride, MoveSite, GetOrCreate
    ├── CampusArrangementService.cs # Places sites into campuses from forecast campus fields (az); idempotent; see Data Hierarchy
    ├── TeamRules.cs                # Team boundary for assignments (hard rule) + TeamRuleException; snapshot cached per DbContext
    ├── IdAllocator.cs              # Cloud mode: per-machine primary-key Id ranges (slot × 20M) so two machines never create the same Id; slot claimed via cloud AppConfig "IdSlot.N"
    ├── OrderMerge.cs                # HasChanged/ApplyChanges field-by-field diff+copy for the Order upsert, shared by OrdersViewModel.UpsertOrders and AutoImportService.RunUpsert
    ├── PasswordHasher.cs           # PBKDF2-SHA256, 100k iterations, per-password salt, constant-time compare; self-describing "pbkdf2$iter$salt$hash" format
    ├── CurrentUser.cs              # Static holder for the signed-in User; Email/DisplayName/IsSuperAdmin; session-only (closing the app signs out)
    ├── UserService.cs              # Account CRUD: FindByEmail, Authenticate, RecordLogin, UpdateProfile, ChangePassword, AllUsers, CreateUser, AdminResetPassword, SetActive
    ├── AuditService.cs             # Best-effort audit log: Log(action, entityType, entityId, details), Recent, ForEntity — never throws, failures are swallowed and reported via LastError
    ├── AirportLookup.cs            # Static IATA code → (Name, StateOrCountry, GeoRegion East|Central|West) — US split follows actual time zones; international entries stay on the coarser East/West split
    ├── ImportResult.cs             # Record: Added, Updated, Skipped, LocationsCreated, ...
    ├── NullToBoolConverter.cs      # IValueConverter: null → false
    ├── ReportFieldCatalog.cs       # ReportField/ReportFieldType, SiteRompFlatRow, ReportRow, curated Order/SiteRomp field catalogs + value getters
    ├── ReportFilterState.cs        # Serializable filter DTO (date range, multi-select IDs/statuses, text search)
    ├── ReportQueryService.cs       # Builds/executes the two mode-specific query pipelines (Orders / Sites+ROMPs); paginated (PagedResult<T>) overloads used by Custom Data
    ├── CoveragePlanner.cs          # Pure, stateless coverage/hiring model (no EF/WPF) — work packages, weekly load, overlaps, gap fits, hiring steps; unit-tested
    ├── CoverageDataService.cs      # Builds CoveragePlanner inputs from forecast rows + PMs + AppConfig settings
    ├── ReportExportService.cs      # ClosedXML export of report results, styled per PmImportService convention
    └── CustomCellValueConverter.cs # IValueConverter coercing a boxed Values[key] object to/from each ColumnType's editing control (stateless; ConverterParameter carries the type)

PMDashboard.Tests/                  # xUnit test project (42 tests); links ScoringEngine.cs + CoveragePlanner.cs directly
├── ScoringTests.cs
└── CoveragePlannerTests.cs
```

## Layout

### App Header (MainWindow.xaml)
- **64px white header bar** spans the full window width above the sidebar and content area
- **Left:** IEM logo (`Assets/iem-logo.png`) — white background blends seamlessly with the white header
- **Right:** "SAT" + "Site Assignment Tracker" subtitle (app display name — renamed from "PM Dashboard" / "Resource Management"; window title, login/first-run dialogs all use "SAT" the same way. The underlying project/namespace/assembly is still `PMDashboard` — only user-visible text changed)
- Header background `White` with a `#E2E8F0` bottom border separates it from the sidebar/content

### Sidebar
Sidebar order (top → bottom): **Workload → Assignment → Sites → PM Maintenance → Teams → Org Chart → Report Builder → Custom Data → Orders (Import) → Forecast Planning → Forecast Timeline → Coverage Planner** then **CONFIG** section with **User Management** / **Configuration**.

Geographic Location filter ComboBox (All / East / Central / West) at the bottom of the sidebar — pushes filter to all relevant VMs via `MainViewModel.OnGeoFilterChanged`.

Sidebar is **collapsible** via `«` / `»` toggle button (44 px when collapsed). The branding text that was previously in the sidebar has moved to the top header.

App starts on the **Workload** page.

```
NavWorkload_Click    → NavigateToWorkload()       → _workloadVm.ApplyGeoFilter + Load
NavAssignment_Click  → NavigateToAssignment()     → _assignmentVm.Load
NavSites_Click       → NavigateToSites()          → _sitesVm.ApplyGeoFilter
NavPm_Click          → NavigateToPm()             → _pmVm.ApplyGeoFilter
NavReportBuilder_Click → NavigateToReportBuilder() → _reportBuilderVm.Load
NavCustomData_Click  → NavigateToCustomData()      → _customDataVm.Load
NavOrders_Click      → NavigateToOrders()         → _ordersVm.Load
NavConfig_Click      → NavigateToConfig()         → _configVm.Load
Row click in Workload → NavigateToPmDetail(pmId)  → _pmDetailVm.Load + CurrentPage = _pmDetailVm
Site name in PmDetail → NavigateToSiteRompsFromPm(siteId, pmId) → SitesPage at Romps level with ← Back button
```

## Pages

### Workload
- Horizontal bar chart — one row per active PM, sorted by utilisation ascending (Site Count Only mode: sorted by assigned-site count ascending instead)
- **Auto-scaling**: `ChartGrid.SizeChanged` → `WorkloadViewModel.ApplyScale(barWidth)`
- **Inside label**: when utilization > 80%, label appears inside bar in white; otherwise outside
- **Score mode label**: `Score: 11.77 ⚠  ·  65.4% util  ·  34.6% avail  ·  2 sites / 6 ROMPs`
- **Hover tooltip**: per-status ROMP counts + PM Load, Utilization %, Availability %, Status
- **Click any row** → navigates to PM Detail page
- **KPI cards (Sites mode)**: Over Capacity, On Target, Under Target, No Capacity Set, Avg Utilisation
- **KPI cards (Score mode)**: Overloaded (util > threshold), Near Full (≥ nearfull), Has Capacity, No Capacity Set, Avg Utilisation
- Cards switch via `IsScoreMode` DataTrigger
- **Site Count Only mode** (`PercentageCalculation == "Count"`, set on the SuperAdmin-only Configuration page — see below): the entire KPI panel is hidden (`IsCountMode` DataTrigger collapses the whole `Border`, not just individual cards); bars are scaled by raw assigned-site count instead of a utilization percentage (`ApplyScale`'s `countMode` branch — `maxCount = max site count among PMs`, `scale = availableWidth / maxCount`); the bar-fill fraction relative to `maxCount` is written into `PmWorkloadRow.UtilizationPct` purely to drive the existing >80%-inside-label heuristic, not shown as a number anywhere; label reads `"{N} sites · {M} ROMPs"` with no score/percentage; the 100%-reference red line and percentage axis ticks are replaced with plain integer count ticks, and `WorkloadPage.xaml.cs.RebuildBackground` skips drawing the reference line (`HundredPctX` is set to `-1` as an off-screen sentinel in this mode) since there's no "100%" concept without a capacity figure.
### PM Detail
- Navigated to by clicking a row in Workload; ← Back button returns to Workload
- **Header**: PM name, office, region; info strip: Data Name, Site Cap, Ideal %, utilization %, **PM Multiplier**, **Eff. Capacity**
- **KPI cards**: Active Sites, Completed Sites, Active ROMPs, Total ROMPs, PM Load (purple)
- **PM Load card** shows: `Score: 11.77 · Avail: 34.6% · Overloaded`
- **ROMP Status Breakdown**: bar + count + ×activity_multiplier + actual total score contribution per status group
- **Score formula footer**: `ROMP sum: X × site factor Y = PM Load: Z`
- **Assigned Sites table** (active/non-complete only): checkbox, Site Name (clickable), Location, Status (dot), ROMs, Score, Edit, Unassign
  - **Checkbox column**: tick individual rows; **Select All** checkbox + **Unassign Selected** (red) button at top
  - **Site Name link** → navigates to Sites page at Romps level for that site
  - **Edit button** → opens `AddEditSiteDialog`, saves + reloads
  - **Unassign button** → confirmation → removes PM, reloads
- **Site Count Only mode** (`PmDetailViewModel.IsCountMode`, set in `Load()` from `ConfigService.Get(PercentageCalculation)`): info strip collapses to just Data Name (Capacity/Ideal Utilization/Current Utilization/PM Multiplier/Eff. Capacity all hidden as one `StackPanel`); the PM Load KPI card is hidden; ROMP Status Breakdown keeps its bars and raw per-status ROMP counts (still the same 6 categories — Ordered/OFCI/Construction/CX/Complete/Close Out) but hides the `×weight` and per-status score columns, and the "ROMP sum × site factor = PM Load" footer is hidden; the Assigned Sites table's Score column is hidden (`ColSiteScore`, collapsed in the page constructor, same pattern as `PmMaintenancePage`'s Site Cap/Score Cap/Multiplier/Ideal Util columns). The breakdown's per-row visibility trigger binds through `RelativeSource AncestorType=UserControl` + `DataContext.IsCountMode` since the `ItemsControl.ItemTemplate`'s own `DataContext` is a `PmStatusRow`, not the page's `PmDetailViewModel`.

### Assignment
- **Assigns per campus** (see Data Hierarchy). Left panel: Cluster (ComboBox + geo badge) → multi-select **campus** ListBox (name · site count · `Campus.PmDisplay`) → with exactly one campus selected, an optional **Site** picker (for a site override) → optional **ROMP** picker (ROMP override, as before)
- **Campus ListBox**: `SelectionMode=Extended`; quick-select **All / Unassigned / None** (Unassigned = campus has no PM); "+ New" creates a campus by exact name (`CampusService.GetOrCreate`)
- Right panel: ranked PM candidates via `AssignmentViewModel.ComputeCandidates()` using `SelectedLocation` region (counts in the grid are sites)
- Assign → `CampusService.AssignCampus` for every selected campus (cascades to each site without its own PM); confirmation for 2+ campuses
- **Site override panel** (a site picked): "Assign to This Site Only" → `AssignSiteOverride` (`AssignmentHistoryService.RecordChange`, which sets `PmOverride` when the PM differs from the campus's); "Use Campus PM" → `CampusService.ClearOverride`
- Every assignment reloads the campus list and raises `SelectionNeedsRestore` so the page re-selects the same campuses (the ListBox multi-selection isn't bindable); the VM then re-picks the same site/ROMP by Id — reloading `Campuses` clears the ListBox selection, which nulls `SelectedCampus`/`SelectedSite` before the restore, so Ids must be captured first
- **Site Count Only mode**: the `CapacityAvailability` rule's contribution is replaced with `1.0 / (1.0 + activeSiteCount)` — no `pm.Capacity`/`Utilization`/`EffectiveCapacity` call anywhere in the ranking — so candidates rank purely by fewest currently-assigned active sites (RegionMatch/LocationFamiliarity rules still apply normally, since neither uses capacity data). The "Util" column in the candidates grid is hidden (`ColUtil.Visibility`, set in the `AssignmentPage` constructor) since it would just show "—" for everyone.
- **Site Assignments bulk import** (`SiteAssignmentImportService`, header buttons "Download Assignments Template" / "Import Assignments") — records assignments that are already in place, at scale, without visiting every site one at a time. **Does not create Sites** — a row's Location + Site Name must both match an existing Site exactly (Location is required specifically so a typo'd Site name under the wrong Location is caught as an error rather than silently matched to the wrong record); PM is matched by **Data Name**. If a Site in the sheet is already assigned to a *different* PM than the sheet says, that row is skipped and reported as a conflict rather than overwritten — reassignment has to be deliberate, not a side effect of a stale export. "Download Template" pre-populates every Site with its current PM Data Name (blank if unassigned), so the natural workflow is export → edit → re-import. `AssignmentViewModel.GetAllSitesForExport()`/`RefreshAfterImport()` support this from the page.
- **PM Data Name dropdown**: the PM Data Name column gets an in-cell Excel dropdown of active PMs (`vm.AllPms`, already `IsActive`-filtered), built as a **range-reference validation against a hidden "Lists" helper sheet** — not the inline comma-string `.List("\"A,B,C\"")` technique used for Home Office/Region elsewhere in the app. Excel's inline dropdown list is capped at ~255 characters, which a real PM roster's Data Names concatenated together would likely exceed; a range reference has no such limit. The dropdown is a convenience only — free-text entry (an inactive or nonexistent PM's Data Name) still works and is still validated/reported at import time, same as before.

### Sites (4-level drill-down: Clusters › Campuses › Sites › ROMPs)
- `SiteNavLevel` enum: Locations | Campuses | Sites | Romps; breadcrumb crumbs jump straight to their level
- **Campuses level**: Campus link, Assigned PM (`PmDisplay`, e.g. "Name (+2 sites with own PM)"), Sites, ROMPs, Status; Assign PM (`CampusService.AssignCampus`, warns about overrides) / Rename / Delete (only when empty); "+ Add Campus"
- **Sites level** (inside a campus): Edit / Assign (asks before creating a site override) / **Move…** (`MoveSiteDialog` — pick a campus in the same cluster or type a new name; `CampusService.MoveSite` sets `CampusManual` so forecast re-arrangement never moves it back; ROMPs move with the site; the site takes the new campus's PM unless it has its own) / Delete. "+ Add Site" inside a campus puts the site there (manual) with the campus's PM
- **← Back to [PM Name]** button appears when navigated from PM Detail via `HasBackButton` / `BackButtonLabel` properties
- ROMP status dropdowns driven by `StatusConfig`
- **Sites/ROMPs bulk import** (`SitesImportService`, header buttons "Download Sites Template" / "Import Sites") — a direct setup/seeding tool for Sites+ROMPs with their actual current status, independent of Orders import's JobName-derived auto-creation. Template columns: Location, Region, Site Name, ROMP Name, Status (one row per ROMP). **Upsert semantics**: re-importing an already-existing (Site, ROMP) pair **updates its Status** rather than skipping it — the sheet is meant to be re-downloaded, edited, and re-uploaded to reflect status changes over time. Region is required only when the Location doesn't exist yet (used to create it); if the Location already exists, Region in the sheet is ignored, never used to update an existing Location's region. A Site that already exists under a *different* Location than the sheet states is an error (typo guard), not silently refiled. Status must match one of the **live** `StatusConfig.All` values — the template's Status dropdown is built from that live list at generation time, not hardcoded, since `RompStatus` is an admin-configurable table. `SitesViewModel.GetAllRompsForExport()`/`RefreshAfterImport()` (the latter just calls `LoadLocations()` — safer than trying to preserve the current drill-down level after an import that can touch many unrelated locations at once) support this from the page.
- **Close & hide (complete-and-remove)**, all three drill-down levels — `AddEditRompDialog` has a "Mark Complete & Remove from List" checkbox; `AddEditSiteDialog` (edit only) has "Close All ROMPs & This Site"; `AddEditLocationDialog` (edit only) has "Close All Sites & This Location". Each level bulk-sets every ROMP underneath it to the complete status, cascading down through the existing "Set All ROMPs to Status" mechanism (`SitesViewModel.UpdateSite`'s `bulkRompStatus` param) one level further via `UpdateLocation`'s own `bulkRompStatus` param (looks up every Site under the Location, then every ROMP under those Sites). All three require a Yes/No confirmation (`MessageBox`) before saving; declining leaves the dialog open with nothing changed. None of this deletes anything — records stay in the database for reporting/scoring history, exactly like any other Complete-status row.
  - **Hidden by default, not just filtered**: completed Locations/Sites/ROMPs now drop out of `FilteredLocations`/`FilteredSites`/`FilteredRomps` even when the (Site/ROMP) Status filter is set to "All" — a real behavior change from before this feature (previously "All" meant literally everything; Locations never had a status concept at all before this). `SitesViewModel.ShowCompletedLocations`/`ShowCompletedSites`/`ShowCompletedRomps` (new bools, default `false`) are separate "Show Completed" checkboxes on each list's filter bar that bring them back into view. For Sites/ROMPs, picking an explicit Status filter value (including "Complete" itself) always overrides the hide-by-default rule — the two controls don't fight each other, an explicit choice wins. Locations have no per-row status filter dropdown, so no such override exists there — just the one toggle.
  - **`Location.IsFullyClosed`** (new computed property, EF-ignored like `Site.SiteStatus`) — `Sites.Any() && Sites.All(s => s.SiteStatus == "Complete")`. Depends on `Sites` (and each Site's `Romps`) being eager-loaded by the caller; defaults to `false` (don't hide it) when they aren't — `SitesViewModel.LoadLocations()` already does `.Include(l => l.Sites).ThenInclude(s => s.Romps)`, so this is accurate wherever the Sites page itself reads it.
  - **`Site.IsClosed`** (new persisted column, `bool`, default `false`) — a **zero-ROMP site can never become "Complete" through ROMP data alone** (there's nothing to bulk-set), so `SiteStatus`'s zero-ROMP branch became `IsClosed ? "Complete" : "Ordered"` instead of an unconditional `"Ordered"`. Deliberately scoped to *only* that branch — once a site has real ROMPs, their actual statuses govern `SiteStatus` as before, so a later active ROMP added to a previously-closed-while-empty site (e.g. a fresh Orders import) makes the site reappear automatically rather than staying silently hidden behind a stale flag. `AddEditSiteDialog` pre-checks its checkbox from `existing.IsClosed` and treats it as a real toggle: checking it when not already closed asks for confirmation and bulk-completes ROMPs; unchecking an already-closed site reopens it (no confirmation, and doesn't touch ROMP statuses — the site only reappears via this flag when it truly has none). `AddEditLocationDialog`'s "Close All Sites & This Location" cascades `IsClosed = true` onto every Site under it the same way (`UpdateLocation`'s new `closeAllSites` param), so a Location made entirely of empty sites can actually be closed now too. Migration: `ALTER TABLE Sites ADD COLUMN IsClosed` in both `App.xaml.cs` (SQLite) and `SyncService.MariaDbDdlScript` (MariaDB) — a plain new scalar column on an already-synced entity, so no `CloudSyncService` changes were needed (its batch sync already copies every scalar property).

### PM Maintenance
- **Live-edit DataGrid**: click a cell to edit inline; `RowEditEnding` → `UpdatePm(pm)` saves automatically
- Columns: Active, First Name, Last Name, Home Office, Region, **Team (PE)**, Data Name, **Site Cap**, **Score Cap**, **Multiplier**, Ideal Util (%)
- **Sub-regions** column: "East1 (+East2)" (`PM.TeamLabel`, EF-ignored) + an Edit button (Super Admins/Directors, `CanEditTeams`) opening `PmSubRegionsDialog` (default + "also covers" checkboxes) → `SetSubRegions` → `PmTeamService`. `UpdatePm` never changes `PeId`/`SubRegionId`/`OtherSubRegionIds` from a grid row save (the row's copy may be stale).
- **Site Count Only mode**: the last four columns (Site Cap/Score Cap/Multiplier/Ideal Util) are hidden — `ColSiteCap`/`ColScoreCap`/`ColMultiplier`/`ColIdealUtil` `x:Name`s on the `DataGridColumn`s, collapsed in the `PmMaintenancePage` constructor. Same fields are hidden in `AddEditPmDialog` (`CapacityRow`/`PmMultiplierPanel`) — the underlying `PM.Capacity`/`IdealUtilization`/`PmMultiplier`/`ScoreCapacity` values are untouched either way (controls are hidden, not removed, so a new PM still saves its default 0/80/blank values and an existing PM keeps whatever it already had).

### Teams
`Views/TeamsPage` + `ViewModels/TeamsViewModel`. Hierarchy **Director (region) › Project Executive / PE (sub-region) › Platform Manager / PM › Project Administrator / PA**; any number of people per role.
- **Data**: `TeamMember` (Role `Director`|`PE`|`PA`; Director → `Region`, PE → `SubRegionId`, PA → `PmId`), `SubRegion` (Name, Region), **PM team = `PM.SubRegionId` (default) + `PM.OtherSubRegionIds` (comma-separated Ids — on the PM row rather than a link table so two machines can't create duplicate rows; sync has no deletes)**; `PM.PeId` is kept = first active PE of the default sub-region (`PmTeamService.SetSubRegions`, the one place a PM's team changes) and is the fallback default for older rows with `SubRegionId` null. A PM's PAs go wherever the PM goes; the PM reports to the default sub-region's PE(s). `Location.SubRegionId` (cluster → sub-region), `Campus.SubRegionId` (campus override). PMs stay in `PMs` — everything assigns to them. Synced like other tables (all four `CloudSyncService` methods + `EnsureCloudSchema`, `SyncService` table list/DDL).
- **Tabs**: Org Chart (read-only tree per region), People (all roles incl. PMs; PM "Edit" = choose PE), Sub-regions & Clusters (sub-region CRUD; per-cluster sub-region dropdown + multi-select "Set selected to"), Conflicts (existing assignments that break the rule).
- **Who can edit**: `CurrentUser.IsSuperAdmin || IsDirector` (Director *account* role); everyone else views.
- **Split clusters**: a campus can have its own sub-region (`Campus.SubRegionId`, any region), overriding its cluster's — set per campus on Sub-regions & Clusters (select a cluster → "Campuses in …" panel; "same as cluster" = null). The governing area for a site is **campus's own sub-region → else cluster's sub-region → else cluster's region** (`TeamRules.AreaOf`/`EffectiveArea`), so **every rule check must pass the campus** (`Allows/Reason/Ensure(pm, locationId, campusId)`); whole-cluster or multi-campus actions use `EligibleForAll` (intersection — empty for a split cluster spanning two teams, by design). Sites page campus grid shows `Campus.SubRegionDisplay`; the Assignment page's `TeamScopeInfo`/`EligiblePms` follow the selected campuses. Coverage packages take the campus's area (forecast sites not in the system via `CampusArrangementService.BuildSiteCampusMap` → campus name).
- **Splits & exceptions (Oct 2026, user decision — replaced the original hard block)**: a cluster **and a campus** can be split across sub-regions/regions — `Campus.SubRegionId` and **`Site.SubRegionId`** (site's own → campus's own → cluster's → cluster's region; `TeamRules.AreaOf(locationId, campusId, siteId)` — pass the site wherever there is one). Setting a campus/site to a sub-region other than its parent's area asks for an acknowledged warning (`AcknowledgeDialog.ConfirmSplit`, via `TeamsViewModel.ConfirmSplit`; declining reverts). Sites panel: Teams → Sub-regions & Clusters → select cluster → select campus. Assigning a PM **outside their team** is allowed after an acknowledged warning: every UI assignment goes through `AcknowledgeDialog.RunAssignment(owner, action)`, which catches `TeamRuleException`, shows `ConfirmOutsideTeam`, and re-runs the action inside `using (TeamRules.AllowOutsideTeam())` — `Ensure` then lets it through and writes one `OutsideTeam` audit entry per distinct exception. Pickers list the team first, others marked "outside team" (`PM.TeamLabel`; Assignment page `OverridePms` + `PmPickTemplate`, `AssignPmDialog` Team column); candidate rankings stay team-only. Site Assignments import holds back out-of-team rows (`OutsideTeam` count) and offers them as one acknowledged batch. Campus/cluster PM cascades skip a split-off site the PM doesn't cover (it becomes `PmOverride`, keeping its own PM) unless acknowledged. These show on Teams → Conflicts.
- **The rule** (`Services/TeamRules.cs`; enforced by throwing, overridable only by acknowledgement as above): a cluster/campus with a sub-region → only PMs whose PE controls that sub-region; a cluster with no sub-region → any PM in the same region (PM's region = their sub-region's region if on a team, else `PM.GeographicalLocation`); no region either → unrestricted. A PM with no team can only take unmapped clusters.
- **Enforcement**: `AssignmentHistoryService.RecordChange`/`RecordRompChange`, `CampusService.AssignCampus`, `SitesViewModel.AssignPmToLocation`, `AssignmentViewModel.AssignPmToCluster` call `TeamRules.For(db).Ensure(...)`, which throws `TeamRuleException` **before** anything changes. `App.OnDispatcherUnhandledException` shows a `TeamRuleException` as a warning (no crash/shutdown), so single UI actions need no try/catch. Bulk paths check per row instead: Site Assignments import (skips + reports), PM deactivation `ReassignAll` (all-or-nothing, names the blocked clusters; also moves the PM's campuses so sites don't become overrides). Pickers only list eligible PMs: Assignment page (`EligiblePms`, candidates, `TeamScopeInfo`), Sites page (`GetPms(locationId)`), Forecast Timeline Suggest PM, Coverage Planner gap fill. Sync, campus arrangement and startup never go through the rule.
- Changing a cluster's sub-region or a PM's PE never moves existing assignments — the ones now outside the team show on Conflicts (and status text). On the real data (Oct 2026) 121 site assignments already crossed regions before any teams existed (APA/Gina Davis, EFD/Kaylee Smith, JAN/Cindy Noi, SBN/Dabrielle Smith, SHV/Chace Adkins).

### Org Chart
`Views/OrgChartPage` + `ViewModels/OrgChartViewModel` — read-only tree of the Teams data: one block per region (Director card) → a column per sub-region (PE card, its clusters incl. split-off campuses/sites) → PM cards on a bracket line (sites count, home office, **campuses they currently cover** — from their open assigned sites, first 6 + "+N more", full list in the tooltip) with each PM's **PA card(s) to the right, directly across** (row Grid: bracket | PM card 220px | connector + PA cards; columns widen to fit; the whole row accepts a PA drop) → plus a "No team yet" column per region for PMs not under a PE. Active people only; PEs without a sub-region / PAs without an active PM are listed as notes underneath. Connector lines are plain `Border`s driven by `OrgChartColumn.IsFirst/IsLast` and `OrgChartPm.IsLast` (no drawing code). Search outlines matching cards; Clusters/PAs toggles; zoom slider + Ctrl+wheel + Fit (a `LayoutTransform` on `ChartRoot`); **Export PNG** renders `ChartRoot` through a `VisualBrush`, dividing out the zoom so the file is always full size (×1.5, capped at 9000px). Sidebar geo filter applies. Clicking a PM opens PM Detail with "← Back to Org Chart" (`NavigateToPmDetail(pmId, back, backLabel)`; `PmDetailViewModel.BackLabel`); "Edit Teams" goes to the Teams page.

#### Org Chart edit mode
"✎ Edit Org Chart" (Super Admins/Directors) → `OrgChartViewModel` keeps a working copy (`_teams` PM→(Default, Others), `_paPm` PA→PM) vs. the saved one; nothing is written until **Save Changes** (`SaveEdits` → `PmTeamService` + `TeamMember.PmId`, audited "(Org Chart)"); Cancel/leaving the page asks. Drag a PM card (code-behind `DoDragDrop`, data = the `OrgChartPm`) onto a sub-region column: plain drop = `MovePm` — removes the column it was dragged from, target becomes the default, other sub-regions kept (old default becomes an "other"); **Ctrl+drop** = add as another sub-region; drop on "No team yet" (always shown in edit mode) = off every team. Drag a PA chip onto another PM card (`MovePa`). Card buttons: ★ Make default (faded cards), ✕ Remove (from that column's sub-region; removing the default promotes the next). A PM appears in **every** sub-region column they're on — full card under the default (tag "also …"), faded card elsewhere ("default: …"); changed cards get a purple border. Background-sync refresh is skipped while there are unsaved edits (`RefreshFromSync`). Adding people stays on PM Maintenance (PMs) and Teams (Directors/PEs/PAs).

### Report Builder
Two independent report modes — not a unified cross-entity query builder, since `Orders` isn't FK'd to `Sites`/`Romps`/`PMs` (only loosely linked by `JobName`/`ProjectManagerDataName` text):
- **Orders mode** — base table `Orders`, curated ~20-field catalog (`ReportFieldCatalog.OrderFields`): dates, price/status, PM/customer identifiers, job/item text.
- **Sites & ROMPs mode** — base table `Romps` flattened with Site/Location/AssignedPm into `SiteRompFlatRow` (one row per ROMP), 9-field catalog. `Site.SiteStatus` is computed from a separate `db.Sites.Include(s => s.Romps)` lookup rather than extending the ROMP query's own `Include` chain (see the Include-cycle note under DbContext Pattern).
- **Filters**: date range (mode-specific date field picker), multi-select Location/Site/PM (`ListBox SelectionMode="Extended"`, same pattern as `AssignmentPage`), ROMP Status / Order Status multi-select, free-text search — composed into `IQueryable` conditionally in `ReportQueryService`.
- **Column picker + results grid are built in code-behind**, not XAML, since the column set is user-chosen at runtime: `ReportBuilderPage` subscribes to the VM's `PropertyChanged` and rebuilds both whenever `AvailableFields`/`SelectedColumnKeys` change (rather than only from button click handlers) — cheaper than re-deriving which code path should remember to call the rebuild, and it fires automatically for saved-report loads and mode switches alike. Grid cells bind via `Values[FieldKey]` dictionary-indexer syntax against each row's `ReportRow`.
- **Chart** — grouped bar chart (group-by field + Count-of-rows or Sum-of-field), reusing `WorkloadPage`'s hand-rolled `ItemsControl`/`Rectangle` + `SizeChanged → ApplyScale` pattern; no charting package added.
- **Saved reports** — `SavedReport` row per preset (Mode + `FilterJson` + `SelectedColumnsJson`, `System.Text.Json`-serialized); Save As / Update / Rename / Delete via `TextInputDialog` prompts.
- **Export to Excel** via `ReportExportService` (ClosedXML, same styled-header convention as `PmImportService`).
- New `SavedReports` table needs its own entry in **all four** `CloudSyncService` sync methods (`DoInitialPullAsync`/`DoPush`/`DoPull`/`DoForcePush`) to sync in Cloud mode — easy to add the `DbSet` and forget one of the four call sites.

### Custom Data
Shows **every real Order or Site/ROMP already in the database**, paginated (mode toggle, same Orders/SitesRomps split and date-range filter as Report Builder — reuses `ReportFieldCatalog`/`ReportQueryService`/`ReportFilterState` directly rather than duplicating that logic), with a "Manage Columns" **setup screen** that lets any user layer their own extra columns (Text/Number/Date/Bool) on top and fill in values per row — shared across all users via the normal sync path, not per-user local state. Static columns (the real Order/ROMP fields) are read-only; only the dynamic columns are editable. This deliberately does **not** work like a blank standalone spreadsheet — every row is tied to a real `Order`/ROMP `Id`, not a freestanding record.
- **Schema**: `CustomColumn` (`Mode`, `Name`, `Key`, `ColumnType`, `SortOrder`) + `CustomRow` (`Mode`, `EntityId`, `ValuesJson`). A `CustomRow` only exists once a user enters at least one dynamic value for that Order/ROMP — most rows have none. `EntityId` is that `Order.Id` or the flattened ROMP's `RompId`, **not** an independent identity; `SaveRow()` looks up by `(Mode, EntityId)` and inserts only if no row exists yet (no DB-level unique constraint — relies on this app's single-writer desktop usage pattern, same as elsewhere in the codebase). Both `CustomColumn` and `CustomRow` are scoped by `Mode` since Orders and Sites/ROMPs are unrelated row sets — one "Notes" column added while in Sites & ROMPs mode never appears in Orders mode.
- **`Key` is generated once at creation** (`col{Id}`, set in a second `SaveChanges()` after the row gets its auto-increment `Id`) and never changes — renaming a column's display `Name` never orphans existing values. **`ColumnType` is immutable after creation** — changing it would make existing JSON values (stored per the old type) fail to parse; delete and re-add the column instead.
- **`CustomRow.SortOrder` is vestigial** — kept in both the model and every `CREATE TABLE`/DDL statement (`DEFAULT 0`) purely because installs that had this table before the "attach to a real entity" redesign already have that column as `NOT NULL` with no default; dropping it from the C# model made EF's `INSERT` omit it and violate that constraint (`SQLite Error 19`), crashing on every first dynamic-cell edit — caught by actually editing a cell, not by the build or test suite.
- **Typed round-trip**: `CustomDataViewModel.BuildRow()` deserializes each `CustomRow.ValuesJson` into `Dictionary<string, JsonElement>` first, then coerces each value to the column's native CLR type (`double`/`DateTime`/`bool`/`string`) before exposing it as `CustomDataRow.DynamicValues[key]` (kept separate from the read-only `StaticValues[key]` dict sourced from `ReportFieldCatalog`) — binding a `DataGrid` cell straight to a raw `JsonElement` wouldn't work with `DatePicker`/`CheckBox` controls.
- **`CustomCellValueConverter`** (stateless `IValueConverter`, `ConverterParameter` = `ColumnType`) is what makes a single dictionary-indexer binding (`DynamicValues[key]`) work across `DataGridTextColumn` (Text/Number), `DataGridCheckBoxColumn` (Bool), and a `DataGridTemplateColumn` wrapping a `DatePicker` (Date) — same "columns built in code-behind, driven by VM `PropertyChanged`" pattern as Report Builder (now firing off both `StaticFields` and `DynamicColumns` changing), but the dynamic half is editable (`Mode=TwoWay`) while the static half is read-only.
- **Setup screen** (`ManageColumnsDialog`) is a modal `Window` that takes the *same* `CustomDataViewModel` instance the page uses (passed via constructor, not a separate ViewModel) — column CRUD lives on the shared VM (`AddColumn`/`RenameColumn`/`DeleteColumn`/`MoveColumnUp`/`MoveColumnDown`, mirroring `ConfigViewModel`'s `RompStatus`/`EquipmentCategory` reorder pattern), scoped to whichever `Mode` is currently active, so both surfaces always agree.
- **Column CRUD calls `RefreshDynamicColumns()`, not a full `RunQuery()`** — it reloads `DynamicColumns` and resyncs already-loaded `Rows`' `DynamicValues` from the DB, without re-running the (unchanged, and for Orders mode potentially 100k+-row) static Orders/ROMPs query.
- **Row edits commit via `DataGrid.RowEditEnding`** → `SaveRow()`, same as `PmMaintenancePage`'s inline-edit convention — but unlike `PmMaintenanceViewModel.UpdatePm()`, `SaveRow()` does **not** reload the grid afterward, so editing one cell doesn't reset scroll position/selection. `SaveRow()` also calls `TouchParentEntity` (bumps the underlying `Order`/`Romp`'s `LastModified` so sync/notifications pick up the change) and `AuditService.Log`.
- **Pagination**: `ReportQueryService.RunOrdersQuery`/`RunSiteRompQuery` gained page-parameter overloads returning `PagedResult<T>` (`Rows`, `TotalCount`, `PageIndex`, `PageSize`, `TotalPages`), built with SQL `Skip`/`Take` and a deterministic `OrderBy...ThenBy(Id)`. Only one page of rows is ever materialized, so there is no "set a filter first" guard even on the 130k+-row Orders table.
- **Excel-style column header filters**: each header carries a small filter-glyph `Button` (not the whole header, so `CanUserReorderColumns`'s drag gesture still works) opening a shared `Popup` — search box, select-all/none, a checkbox `ListBox` of that column's distinct values (capped at `CustomDataViewModel.MaxFilterValues`, with a "narrow the date range" hint past the cap). `_columnFilters: Dictionary<string, HashSet<string>>` is applied client-side over the already-paged rows via `FilteredRows`/`ApplyColumnFilters`.
- **Persistent column order**: dragging a header (`DataGridCustom_ColumnReordered`) saves the new left-to-right key order into `AppConfig` as JSON (`ConfigKeys.CustomDataColumnOrderPrefix + Mode`); `RebuildColumnsCore` re-sequences newly-built columns against `vm.GetSavedColumnOrder()` (stable sort — a key absent from the saved order, e.g. a brand-new column, keeps its default position at the end).
- **Editable-column styling**: dynamic (user-added) columns get a red-accent header and a 2px left/right cell border (`DataGridEditableCellStyle` in `App.xaml`, applied via `ApplyEditableStyling`) — the cell background itself is left alone since the grid already alternates row colours, so a tint would just read as normal banding.
- **History column**: every row has a trailing "History" link opening `RowHistoryDialog`, which lists `AuditService.ForEntity(entityType, entityId)` — every change (bulk imports are logged once for the run, not per row, so they won't show up as row-level history entries).

### Orders (Import)
Import-only page — no data grid or search bar (removed deliberately; see next paragraph). Header shows record count + last-uploaded stat; buttons are Clear All and Import Excel.
- Import routes to one of three parsers based on `ConfigService.Get(SourceDataType)`; upsert by `(CustomerOrder, OrdLine)` key; auto-creates Locations/Sites/ROMPs from JobName.
- **The grid was removed on purpose**: reviewing 100k+ raw order rows in-app added no value over reviewing the source spreadsheet, and rendering it was the single biggest cost on this page. In its place, a persistent **"Last Import" results card** (added/updated/unchanged counts, locations/sites/ROMPs created, timestamp, who ran it) stays visible until the next import — replacing the old one-shot `MessageBox` that disappeared as soon as you dismissed it. `OrdersViewModel.SetLastImportResult()` populates it; `HasLastImport` gates the empty state.
- **Import lock** (`ImportLockService`, Cloud mode only — Local is single-machine and Network's manual Sync Before/After Travel already serializes access by nature): `OrdersViewModel.ImportWithLockAsync()` acquires a lock row (`AppConfig` key `ImportLock`, written directly to the cloud via `ForceCloud`) before importing, so two people can't import over each other. A lock older than 30 minutes is treated as abandoned and silently reclaimed, so a crashed import can't wedge the button forever. Local/Network mode: `TryAcquire`/`Release` are no-ops that always succeed.
- **Local-first-then-background-push, unchanged import architecture**: the lock wraps around the *existing* fast local SQLite import (`UpsertOrders`, unchanged) — it does not route writes to the cloud directly. After the local import, `CloudSyncService.PushNowAndWaitAsync()` forces an immediate push and awaits it (bounded by a timeout, after which it just proceeds — the next regular sync tick picks up anything not yet pushed) before the lock is released. `OrdersViewModel.IsImporting`/`ImportingStatus` drive a small in-progress indicator and disable the Import button for the duration.
- **`OrderMerge`** (`Services/OrderMerge.cs`) holds the field-by-field `HasChanged`/`ApplyChanges` comparison, shared by both `OrdersViewModel.UpsertOrders` (manual import) and `AutoImportService.RunUpsert` (semi-automatic import) — previously `AutoImportService` had its own "simplified" change check (`found.UploadDate != uploadTime`) that matched on every re-import but never actually copied the new field values onto the existing row, silently discarding every field change made in the source spreadsheet on a semi-automatic re-import. Both paths now use the same real diff+copy.
- **`AutoImportService.ImportCompleted`** event (carries the `ImportResult`) lets `OrdersViewModel` refresh the Last Import card even when a semi-automatic import lands while the user is on a different page — subscribed idempotently (`-=`/`+=`) in `Load()`, same pattern as `ConfigViewModel`'s `CloudSyncService.StatusChanged` subscription.

### Forecast Timeline — sort
Filter bar **Sort: NBD / PFHO** buttons (`ForecastTimelineViewModel.Sort`, `TimelineSort` enum). First click on a date = earliest → latest, next click flips it; switching buttons starts that date ascending; ✕ (visible only while sorted) returns to Default = load order (cluster → campus → room type). NBD sorts on the bar's `StartDate` (earliest NBD of its rows), PFHO on `EndDate` (latest PFHO); ties → the other date, then cluster/campus/room. Sorting is applied in `ApplyScale` after `FilterRows`, so it composes with every filter; Clear Filters deliberately leaves the sort alone.

### Coverage Planner
`Views/CoveragePlannerPage` + `ViewModels/CoveragePlannerViewModel`, logic in `Services/CoveragePlanner.cs` (pure, unit-tested in `CoveragePlannerTests`), inputs from `Services/CoverageDataService.cs`. Answers: who is assigned to what, what's unassigned, where PMs are overloaded, which unassigned campuses fit into a PM's breaks, and when/how many PMs to hire.
- **Work package** = one forecast campus + room type, window = earliest NBD → latest PFHO of its forecast rows (same grouping/skip rules as the Forecast Timeline). Fallback NBD: when a row has no global "NBD", the first of its forecast type's own NBD-named Date fields is used (the LV sheet's "NBD _EDD" — without it ~half the forecast was invisible). EDD is never used as a start date. Forecast campuses not in the system are included as unassigned (region from `AirportLookup`). A package's owner = its campus's PM, else the cluster's direct PM (`Location.AssignedPmId`); a package owned by an inactive/missing PM counts as unassigned.
- **Load** (per day, averaged per Monday-start week): `CoverageBuildWeight` during the build phase (NBD → CX start) + `CoverageCxWeight` during the CX window (last `CxWindowDays` before PFHO — shared with the Timeline's conflict rule). A PM is **over capacity** in a week when their packages' load > `CoverageCapacityPerPm`.
- **Team boundary**: packages carry their cluster's sub-region and PMs their team's (`WorkPackage.SubRegion`, `PlannerPm.SubRegion`, filled by `CoverageDataService` from `TeamRules`); `CoveragePlanner.CanTake` is the same rule as `TeamRules`. Hiring is per **area**: each sub-region (its packages vs its team's PMs), plus "{Region} (no sub-region)" for unmapped clusters vs PMs not on a team — exactly the old per-region view while no teams exist. `HiringStep`/`RegionMonth.ParentRegion` drives the region filter; `CoveragePlan.PmAreas` maps PMs to areas.
- **Gap fit**: an unassigned package fits a PM (same team area only — `CanTake`) if adding it never pushes them over capacity in any week of its window. The Gap Fill tab's Assign button assigns the **whole campus** via `AssignmentHistoryService.RecordChange` + audit, then recalculates.
- **Hiring**: per region per week, PMs needed = ⌈total load ÷ capacity⌉ (a lower bound — perfect packing); each time the shortfall vs. the region's active PM count grows, a hiring step is emitted with Hire-by = that week − `CoverageHireLeadDays` (shown as overdue when past). "Unassigned campuses to cover" = unassigned packages active that week. The month grid shows peak need per region per month.
- Recalculates on every visit, on Recalculate, and on background-sync `DataChanged` while open (`MainViewModel`). Region filter = the sidebar geo filter.
- Defaults were calibrated on real data (Oct 2026): capacity 3.0 ≈ today's East/Central headcount just covering their average load; peaks still show shortfalls. Capacity is the main lever — lowering it raises the hiring numbers.
- **Settings**: Configuration → **Coverage Planning Model** card (`x:Name="CoverageCard"`) edits all six variables — capacity, build weight, CX weight, CX window (the same `CxWindowDays` setting as the Forecast Schedule Conflicts card, so editing either changes both), hire lead, horizon. **Reset to defaults** restores `CoverageDefaults` (in `CoverageDataService.cs` — the single source also used for first-run seeding and unreadable-value fallbacks) and saves. The planner page's **Model settings** button (Super Admin only, since Configuration is) opens Configuration scrolled to that card via `ConfigViewModel.PendingScrollTarget`.
- `MainWindow` keeps the sidebar highlight in sync with `MainViewModel.CurrentPage` (type → RadioButton map), so pages reached from another page (not the sidebar) still highlight correctly; add new pages to that map.

### Configuration
**The entire page requires `CurrentUser.IsSuperAdmin`** (see Access Gate below) — anyone else sees a plain "restricted" message instead of any of the cards. Cards in order (top → bottom):
1. **Upload Method** — Manual or Semi-Automatic; import folder + poll interval when Semi-Auto
2. **Assignment Rules** — enable/disable, weight, reorder, + Add Custom Rule
3. **Workload Scoring Parameters** — sliders + textboxes for SiteOverhead, TargetCapacity, NearFullThreshold, OverloadThreshold; "Save Parameters" + "Sync Equipment from Orders"
4. **Site/ROMP Statuses** — status list with inline **score weight slider** (0–2.4; CX default 1.2 = 50%)
5. **Equipment Score Categories** — categories DataGrid with inline **score weight slider** (0–3.2; PAX default 1.6 = 50%)
6. **Percentage Calculation** — Sites / Score / **Site Count Only** radio buttons
7. **Source Data** — VADATA / Tableau / Open Lines
8. **Database** — Local / Network / Cloud modes; includes a **Danger Zone** ("Delete Local Database Data")
9. **Manage Users** — see Accounts & Authentication

#### Percentage Calculation — Site Count Only mode
Third `PercentageCalculation` value (`"Sites"` | `"Score"` | `"Count"`), set via `ConfigViewModel.SavePercentageCalculation("Count")` / `IsCountMode`. Unlike the other two, this mode strips capacity/utilization out of the app rather than just changing how it's computed:
- **Workload** — KPI panel hidden entirely; PM bars scaled by assigned-site count, labelled `"{N} sites · {M} ROMPs"`; sorted by site count ascending. See the Workload page section above for the `ApplyScale` mechanics.
- **Add/Edit PM** (`AddEditPmDialog`) and **PM Maintenance**'s grid — Capacity, Ideal Utilization, Score Cap, and PM Multiplier are hidden (not removed; see the PM Maintenance section above).
- **Assignment** ranking — the `CapacityAvailability` rule ranks by fewest active assigned sites instead of any capacity/utilization figure; see the Assignment section above.
- Every one of these checks `new ConfigService().Get(ConfigKeys.PercentageCalculation, "Sites") == "Count"` independently (in `WorkloadViewModel.Load()`, `AssignmentViewModel.ComputeCandidates()`, and the constructors of `AddEditPmDialog`/`PmMaintenancePage`/`AssignmentPage`) — there's no single `CurrentMode` static, so a new surface that should also respect Count mode has to add its own check the same way.
- **PM Detail is NOT adapted** — deliberately out of scope for the original request; it still shows the full Score/Capacity breakdown in every mode.

#### Danger Zone — Delete Local Database Data
`ConfigViewModel.GetLocalDataCounts()`/`DeleteLocalData()` empty `RompEquipmentTypes/Romps/Sites/Locations/Orders/CustomRows/CustomColumns/SavedReports/EquipmentWeights` from **this machine's local SQLite only**, via raw `DELETE FROM` (not EF `RemoveRange`, so 100k+ Orders rows never get tracked) plus `DELETE FROM sqlite_sequence` to reset autoincrement counters. PMs and all configuration tables (`AppConfigs`, `RompStatuses`, `EquipmentCategories`, `EquipmentKeywords`, `AssignmentRules`, `Users`) are deliberately preserved. Since raw `DELETE` never runs through `SaveChanges()`, no cloud push is triggered — in Cloud mode the next sync simply pulls the cloud's copy back down, making the cloud authoritative for this machine; in Local mode the data is genuinely gone. Two confirmations: a `MessageBox` showing the per-table breakdown, then a `TextInputDialog` requiring the literal text `DELETE` to be typed.

#### Danger Zone — Clear All Assignments
`ConfigViewModel.ClearAllAssignments()` unassigns every PM from every site (and clears `PmOverride`), campus, ROMP override and cluster (`Location.AssignedPmId`), and closes all open `AssignmentHistory` rows (history kept). Unlike Delete Local Data it goes through `SaveChanges()` — rows are stamped and **pushed to the cloud**, so every machine loses the assignments. Super Admin only; MessageBox with counts (`GetAssignmentCounts`) + type `CLEAR` to confirm; suggests downloading the Assignments template first as a backup. Audited as `Unassign`/`AllAssignments`.

#### Manage Users
`ConfigViewModel.Users` (`ObservableCollection<User>`, loaded via `UserService.AllUsers()`) bound to a `DataGrid` — Name, Email, Role, Last Login, Active. Actions per row: **Reset Password** (`ResetUserPasswordDialog` → `UserService.AdminResetPassword`) and **Activate/Deactivate** (`UserService.SetActive` — no hard delete, keeps audit history intact). **+ Add User** opens `AddEditUserDialog` → `UserService.CreateUser`. Every action logs via `AuditService.Log`. This card no longer carries its own visibility check — the whole page already requires `IsSuperAdmin`, so the card is simply always shown once you're on the page at all; the `Border.Style`/`DataTrigger` gating it individually is harmless leftover redundancy, not a second real gate.

#### Access Gate — whole page requires Super Admin
The entire Configuration page (`Views/ConfigPage.xaml`) is gated on `ConfigViewModel.IsSuperAdmin` (`CurrentUser.IsSuperAdmin`) — **not** a shared password. This replaced an earlier two-tier scheme: a per-person `IsSuperAdmin` check on just the Manage Users card, plus a separate shared "admin-gate password" (`AdminConfig`/`PasswordDialog`, stored per-machine in `%APPDATA%\PMDashboard\adminconfig.json`) that covered only Percentage Calculation/Source Data/Database and left the other six cards open to any signed-in user. Both `Dialogs/PasswordDialog.xaml(.cs)` and `Services/AdminConfig.cs` have been **deleted** — there is no more shared admin password anywhere in the app.
- **Structure**: the page's outer `<Grid>` holds two overlapping siblings toggled by opposite `IsSuperAdmin` DataTriggers — a centered "Configuration is restricted to Super Admins" message, and the real content `Grid` (all 9 cards). Non-SuperAdmins never see any card, not even read-only.
- `Source Data`'s per-click confirmation (`ConfigPage.xaml.cs`'s `TryChangeSourceType`) used to pop a fresh `PasswordDialog` on every VADATA/Tableau/OpenLines click, independent of the old Unlock banner — that's gone too; changing it now just saves directly, since getting onto the page at all already proved the SuperAdmin role.
- `ConfigViewModel.IsAdminUnlocked`/`IsAdminLocked`/`UnlockAdmin()`/`LockAdmin()`/`IsSourceDataUnlocked`/`IsDatabaseUnlocked` are all gone — there's nothing left to unlock once the whole page is the gate.

#### Workload Scoring Parameter Sliders
Each parameter row: `Label (160px) | Slider (220px fixed) | Textbox (90px) | Hint (*)`.
Default values sit at **50%** of the slider maximum:

| Parameter | Min | Default (50%) | Max |
|---|---|---|---|
| SiteOverhead | 0 | 0.05 | 0.10 |
| TargetCapacity | 0 | **259.2** | 518.4 |
| NearFullThreshold | 0 | 0.80 | 1.60 |
| OverloadThreshold | 0 | 1.00 | 2.00 |
| SkillBase | 0 | 0.40 | 0.80 |
| SkillPerPoint | 0 | 0.06 | 0.12 |

Note: `TargetCapacity` default was updated from 18 → **259.2** based on actual PM load calculations.

Workload param ViewModel properties are now `double` (not string). `SaveWorkloadParams()` formats them to invariant-culture strings for AppConfig storage.

## Accounts & Authentication

Real per-person logins — the only access-control mechanism in the app now (the earlier separate shared admin-gate password has been removed; see Configuration's Access Gate). `CurrentUser` (static, session-only — closing the app signs out) holds who is signed in; `CurrentUser.IsSuperAdmin` gates the entire Configuration page.

- **First run** (`Users` table empty): `App.xaml.cs` shows `CreateAdminAccountDialog` instead of `LoginDialog` — the first real person sets their own email + password directly (`UserService.CreateUser(..., "SuperAdmin", ..., requireChangeOnFirstLogin: false)`). No placeholder/seed password ever exists in source or git history — the same class of mistake the old shared admin-gate password used to have (fixed for that password earlier this session, before removing it entirely).
- **Every other run**: `LoginDialog` (email + password). Deliberately generic "Email address or password is incorrect" error on failure — doesn't reveal whether the email exists, avoiding user enumeration. A successful sign-in with `MustChangePassword = true` (set on account creation and on any admin password reset) forces `ChangePasswordDialog` before the app continues; its Cancel button reads "Sign Out" in that mandatory case instead of "Cancel".
- **`App.xaml.cs` startup sequencing**: `ShutdownMode="OnExplicitShutdown"` (not the default `StartupUri`-driven `OnMainWindowClose`) plus explicit `MainWindow` creation at the end of `OnStartup`, because the account/login dialogs run *before* `MainWindow` exists — without the explicit shutdown mode, closing `LoginDialog` (e.g. via the X button) before a successful sign-in would tear the whole app down with no window ever having been the "main" one.
- **`PasswordHasher`**: PBKDF2-SHA256, 100k iterations, per-password random salt, constant-time comparison; hash format is self-describing (`pbkdf2$iterations$salt$hash`) so the work factor could be bumped later without invalidating existing hashes.
- **`UserService`**: `CreateUser`/`AdminResetPassword` both set `MustChangePassword = true` except first-run self-creation — an admin-typed (or admin-reset) password must never stay live. `SetActive` deactivates rather than deletes, so `AuditEntry.UserEmail` (a plain string, not a `User` FK) keeps meaning after the account is gone.
- **`AuditService.Log(action, entityType, entityId, details)`** is called from every mutating action across the app (user CRUD, Custom Data row/column CRUD, Orders import/clear, local-data delete) — best-effort: a logging failure is caught and never breaks the action being logged. `AuditService.ForEntity(entityType, entityId)` powers Custom Data's per-row History dialog; `AuditService.Recent()` is the general activity feed.
- **`Users`/`AuditEntries` sync like every other table** — both are in all four `CloudSyncService` sync methods and `SyncService._intPkTables`/`MariaDbDdlScript`, and both got `CREATE TABLE IF NOT EXISTS` blocks in `App.xaml.cs`. There is no separate "auth server" — accounts and the audit log are just more rows that flow through the same local-first sync path as Orders or Sites.
- **Non-distracting cross-user notifications** (`CloudSyncService.DataChanged` event): raised from a periodic (never the initial startup) pull that brought in rows someone else changed, built from the newly-added `LastModifiedBy` column on `Order`/`Site`/`Romp` (excludes this machine's own changes, and excludes `Users`/`AuditEntries` — those aren't "news" in the same sense). `MainWindow` subscribes once at construction and shows a small auto-dismissing (5s) toast (`ShowToast`) — non-modal, doesn't steal focus.
- **Shared "last updated" indicator**: `MainWindow.SetLastUpdated()` is driven off `CloudSyncService.StatusChanged`, but only actually updates the label when `LastSyncTime` is set — that happens solely on `SyncTick`'s and `PushNowAndWaitAsync`'s success paths, not on the per-save "Pushed N record(s)" notice or on error/connecting messages, so the label reflects genuine sync completions (including this machine's own import push) rather than every intermediate status string.

## Data Hierarchy

```
Location = Cluster (3-letter airport code, e.g. ATL; GeographicalLocation East|Central|West)
  └── Campus (Name e.g. ATL089, AssignedPmId? — the campus PM)
        └── Site (Name e.g. ATL085, CampusId?, AssignedPmId? FK → PM, PmOverride, CampusManual, SiteStatus computed)
              └── Romp (Name e.g. ATL085ROMP06, Status, AssignedPmId? ROMP override)
                    └── RompEquipmentType (junction → EquipmentCategory)
```
`Site.LocationId` is kept alongside `CampusId` (a campus belongs to one cluster; moving a site to a campus also sets its `LocationId`).

**Terminology**: before Oct 2026 the UI called a `Site` row a "campus"; the real Campus level was added later. User-visible text now says Site for `Site` rows; many identifiers still say Campus for sites (e.g. `PmCardRow.CampusCount`, `CampusName` on Workload/Coverage/Timeline rows, `AddForecastCampusesDialog`) — read those as *site*. The real campus is `Campus`/`CampusId`/`RealCampusCount`.

**Campus PM is materialised onto sites**: `Campus.AssignedPmId` is copied to every site in the campus that isn't `PmOverride`, so all workload/report/planner code keeps reading `Site.AssignedPmId` unchanged. `AssignmentHistoryService.RecordChange` is the single place a site's PM changes and recomputes `PmOverride` (= campus has a PM and the site's PM differs). A user's move or override always wins over the forecast.

**Campus arrangement** (`CampusArrangementService.Arrange()`, idempotent, locked): builds a site → campus map from forecast rows using fields flagged `ForecastField.IsCampusField` (Type O "az", Type L's type-specific "Cluster"; Forecast Planning → Fields → "Campus (az)" checkbox — toggling it re-arranges), majority value per site, names kept exactly as written (IAD12 ≠ IAD012). Then: merges duplicate campuses (same cluster + name), skips `CampusManual` sites, places each site in its forecast campus, else keeps its current campus, else a campus of its own name; a new campus takes its sites' majority PM. Runs at startup (Local/Network), after the cloud initial pull, after forecast uploads, and after Orders/auto/Sites imports and Forecast Timeline "Add New Sites" when sites were created.

**SiteStatus** (computed, EF-ignored):
- `"Active"` (yellow) — any ROMP in a `CountsAsActive` status
- `"Complete"` (grey) — all ROMPs in `IsComplete` status
- `"Ordered"` (green) — default

**Workload counts**: complete sites and complete ROMPs are excluded from counts and scoring.

## Database Models

### PM
| Field | Type | Notes |
|---|---|---|
| Id | int | PK |
| FirstName, LastName | string | |
| HomeOffice | string | FRE, VAN, JAX, Remote |
| GeographicalLocation | string | East, Central, West |
| DataName | string | |
| Capacity | double | Site Cap |
| IdealUtilization | double | Target % |
| ScoreCapacity | double | Legacy field (kept for compat) |
| PmMultiplier | int? | Experience multiplier 1–10; null → treated as 1.0 factor |
| IsActive | bool | |
| LastModified | DateTime | Auto-stamped by SaveChanges override |

### RompStatus
| Field | Type | Notes |
|---|---|---|
| Id | int | PK |
| Name | string | "Ordered", "OFCI", "Construction", "CX", "Complete" |
| IsComplete | bool | Excluded from workload counts |
| CountsAsActive | bool | Site with any ROMP here → "Active" |
| ScoreWeight | **double** | **Activity multiplier** (Complete=0.1, Ordered=0.4, OFCI=0.6, Construction=1.0, CX=1.2) |
| SortOrder | int | |
| IsBuiltIn | bool | Cannot be deleted |
| LastModified | DateTime | |

### EquipmentCategory
| Field | Type | Notes |
|---|---|---|
| Id | int | PK |
| Name | string | "PAX", "LV", "MV", "PDC", "FS Exclusion" |
| Weight | **double** | Equipment weight (PAX=1.6, LV=1.3, MV=1.0, PDC=0.6; 0 for exclusions) |
| IsExcluded | bool | True → category is an exclusion filter |
| IsBuiltIn | bool | Cannot be deleted |
| SortOrder | int | |
| LastModified | DateTime | |

### RompEquipmentType (junction table)
| Field | Type | Notes |
|---|---|---|
| Id | int | PK |
| RompId | int | FK → Romps (cascade delete) |
| EquipmentCategoryId | int | FK → EquipmentCategories (cascade delete) |
| LastModified | DateTime | |
| (RompId, EquipmentCategoryId) | unique index | |

Equipment types assigned directly to ROMPs via `AddEditRompDialog` checkboxes. `MigrateEquipmentTypesFromOrders()` auto-populates on first launch from order data.

### User
| Field | Type | Notes |
|---|---|---|
| Id | int | PK |
| Email | string | Username; compared case-insensitively; unique |
| FirstName, LastName | string | |
| PasswordHash | string | `PasswordHasher` format; never the plaintext |
| Role | string | `"SuperAdmin"` \| `"User"` — SuperAdmin can manage other accounts |
| IsActive | bool | Deactivated, not deleted, on removal |
| MustChangePassword | bool | Set on creation/admin-reset; login flow forces a change before continuing |
| LastLoginUtc | DateTime? | |
| LastModified | DateTime | |

### AuditEntry
| Field | Type | Notes |
|---|---|---|
| Id | int | PK |
| TimestampUtc | DateTime | |
| UserEmail, UserName | string | Plain strings, not a `User` FK — stays meaningful after the account is deactivated |
| Action | string | e.g. "Create", "Import", "ResetPassword", "Delete" |
| EntityType | string | e.g. "Order", "Romp", "User", "LocalDatabase" |
| EntityId | int? | Null for actions with no single row (e.g. bulk import, local-data delete) |
| Details | string | Free-text summary |
| MachineName | string | |
| LastModified | DateTime | |

### AppConfig (known keys)
| Key | Default | Notes |
|---|---|---|
| UploadMethod | "Manual" | "Manual" \| "SemiAutomatic" |
| ImportFolder | "" | |
| PollTimeSeconds | "60" | |
| SourceDataType | "VADATA" | "VADATA" \| "Tableau" \| "OpenLines" |
| ItemFilterPrefix | "SOLI" | Legacy (equipment now stored directly on ROMPs) |
| PercentageCalculation | "Sites" | "Sites" \| "Score" |
| SiteOverhead | "0.05" | site_factor overhead per distinct site |
| TargetCapacity | **"259.2"** | PM load denominator (updated from 18) |
| NearFullThreshold | "0.80" | Utilization ratio → "Near Full" |
| OverloadThreshold | "1.00" | Utilization ratio → "Overloaded" |
| SkillBase | "0.40" | skill_factor intercept (multiplier=10 → factor=1.0) |
| SkillPerPoint | "0.06" | skill_factor slope per multiplier point |
| CloudSyncInterval | "30" | Background cloud sync interval in seconds |
| CoverageCapacityPerPm | "3.0" | Coverage Planner: load one PM can carry at once |
| CoverageBuildWeight | "0.25" | Coverage Planner: load per day during a campus's build phase |
| CoverageCxWeight | "1.0" | Coverage Planner: load per day during the CX window |
| CoverageHireLeadDays | "90" | Coverage Planner: hire this many days before a shortfall |
| CoverageHorizonWeeks | "104" | Coverage Planner: planning horizon |
| ClientVersion.{Machine} / IdSlot.{N} | (none) | Per-machine version reports / Id-range claims — written straight to the cloud, see Versioning and IdAllocator |
| CustomDataColumnOrder_{Mode} | (none) | JSON array of Custom Data column keys, per mode — saved column drag order |
| ImportLock | (none) | Cloud-mode-only, JSON `{HeldBy, Machine, AcquiredUtc}` — written directly to the cloud DB, not synced like other rows |

## Difficulty Scoring (Score mode)

### Multiplicative Formula
```
equip_weight_sum  = Σ weight[t]  for each equipment type t on the ROMP
romp_score        = activity_weight × equip_weight_sum
site_factor       = 1 + SiteOverhead × distinct_active_sites
pm_load           = site_factor × Σ romp_score  (active ROMPs only)
skill_factor      = SkillBase + SkillPerPoint × PmMultiplier  (null PmMultiplier → 1.0)
eff_capacity      = TargetCapacity × skill_factor
utilization       = pm_load / eff_capacity
availability      = max(0, 1 − utilization)
```

Activity weights (RompStatus.ScoreWeight, double): Complete=0.1, Ordered=0.4, OFCI=0.6, Construction=1.0, CX=1.2

Equipment weights (EquipmentCategory.Weight, double): PAX=1.6, LV=1.3, MV=1.0, PDC=0.6

Default skill params: SkillBase=0.40, SkillPerPoint=0.06 → multiplier 10 gives factor 1.0 (full capacity); multiplier 1 gives factor 0.46.

PM status thresholds: utilization > OverloadThreshold → "Overloaded"; ≥ NearFullThreshold → "Near Full"; else "Has Capacity"

### Sites mode (unchanged)
`UtilPct = activeSiteCount / pm.Capacity × 100`

### Pure Scoring Logic
`ScoringEngine.cs` — static class, no DB/WPF dependencies, fully unit-tested in `PMDashboard.Tests`.

### Key Implementation Notes
- `StatusConfig.Reload()` and `ScoreConfig.Reload()` called on startup and after any config change
- `AppDbContext.SaveChanges()` override auto-stamps `LastModified = DateTime.UtcNow`
- `SkipLastModifiedStamp = true` on a context prevents stamping during sync
- Equipment types stored directly on `Romp.EquipmentTypes`

## Database Configuration

### Three Modes (stored in `%APPDATA%\PMDashboard\dbconfig.json`)

| Mode | Storage | Use case |
|---|---|---|
| **Local** | `%LOCALAPPDATA%\PMDashboard\data.db` | Single machine |
| **Network** | UNC path e.g. `\\server\share\PMDashboard\data.db` | Small office team |
| **Cloud** | MariaDB (via Pomelo) | Multi-user, always synced |

### AppDbContext Connection Logic
- `new AppDbContext()` — ALWAYS uses local SQLite, even in Cloud mode (speed)
- `new AppDbContext(forceCloud: true)` — uses MariaDB; used only by `CloudSyncService`, `ImportLockService` and `IdAllocator`
- **Never** use `forceCloud: true` in ViewModels or other app code
- **`ForceCloud` is a constructor argument, not an initializer, on purpose.** The constructor touches `ChangeTracker` (IdAllocator hook), which initializes the context and runs `OnConfiguring` immediately — an object-initializer `{ ForceCloud = true }` runs *after* that, so every "cloud" context silently connected to local SQLite and all cloud sync stopped with no error (2026-10-05, v1.0.0). Anything `OnConfiguring` reads must be set before the constructor touches `ChangeTracker`/`Database`/`Model`.

### Network Travel Sync (`SyncService`)
- **Sync Before Travel**: copy network DB → local, enable travel mode
- **Sync After Travel**: bidirectional LastModified merge via SQLite `ATTACH DATABASE`
- FK constraints suspended during merge (`PRAGMA foreign_keys = OFF`)
- **Limitation**: record deletions made offline are not propagated

### Cloud Background Sync (`CloudSyncService`)
- Started in `App.OnStartup` when Cloud mode active (`App.CloudSync`)
- On startup: full pull from cloud → local SQLite — each table pulled independently; failure in one table reported as warning, others continue
- Background timer (default 30 s): push local-newer rows to cloud, pull cloud-newer rows to local
- **First push after startup always sends ALL local records** (`_firstPushDone` flag) so records with the migration-default `LastModified = '2000-01-01'` are not filtered out by the timestamp gate
- `AppDbContext.LocalSaved` event triggers immediate push after any local `SaveChanges()`; errors are reported (not swallowed)
- Sync status displays push/pull counts: `"Synced HH:mm:ss (↑N ↓M)"`; `SaveChanges()` skipped when count is 0
- **Batch queries**: `BatchPush` loads matching destination records in a single `WHERE Id IN (...)` query instead of one `Find()` per record (avoids N+1 round-trips to MariaDB)
- **`RompEquipmentTypes` initial pull** uses natural key `(RompId, EquipmentCategoryId)` to avoid unique-constraint conflicts when both sides have the same pair under different auto-increment IDs
- **`TriggerForcePush()`** — public method; overwrites every cloud record with local data unconditionally (bypasses `LastModified` check); exposed as "Force Push All Data to Cloud" button in Config → Database; use after schema changes or when sync is stuck
- **`PushNowAndWaitAsync(TimeSpan? timeout = null)`** — public, awaitable; forces an immediate push and waits for it (default 60s timeout, after which it just returns — the next regular tick picks up anything unpushed), used by the Orders import-lock flow so a lock isn't released until changes actually reached the cloud. No-op when sync isn't running or the initial pull hasn't completed (Local/Network mode, or Cloud mode still starting up).
- **`DataChanged` event** — raised from a periodic (not the initial) `DoPull` whenever it pulled **any** rows. The summary is built by re-querying `Orders`/`Sites`/`Romps` for `LastModified > since`, grouped by `LastModifiedBy` (excluding this machine's own email) into e.g. `"3 orders updated by Sarah Chen"`; when nothing is attributable (same account on both machines, or a table without `LastModifiedBy` like `Locations`/`AssignmentHistories`) it falls back to `"N changes synced from another computer"` — it used to stay silent then, which also meant pages never refreshed. Subscribers: `MainWindow` (toast), `WorkloadViewModel` (reload), `MainViewModel` (reloads the current page if it's Sites/Assignment/PM Detail via their `RefreshFromSync()`/`Load()`; edit-heavy pages are deliberately skipped).
- **Two clocks, two cursors** (each rule below was a real lost-update bug): `_lastPushUtc` is on **this machine's** clock (local rows are stamped locally); `_lastPullServerUtc` is on the **cloud server's** clock (`CloudNowUtc()` = `SELECT UTC_TIMESTAMP(6)`). Every push **restamps** the rows it writes with the server's time (`BatchPush`/`BatchPushConfigs` `stamp`) — the last-writer-wins comparison still uses the original *edit* time, only the stored value changes — and `RestampLocal` copies that stamp back onto the local row unless it was edited again meanwhile. So a cloud row's `LastModified` means "arrived at", on one clock for all machines: an edit made long before its upload (a long full push, an offline laptop) or on a PC with a skewed clock can no longer slip behind other machines' pull cursors. (Before this, 2 campus unassignments made during a 5-minute full push were skipped forever.) Pulls still re-read `PullOverlap` (2 min) to cover in-flight commits. `PushNowAndWaitAsync` advances neither cursor; the initial pull sets both to when it *started*. `AppDbContext.StampLastModified` never moves a row's timestamp backwards (server-stamped values can be slightly ahead of local time). `RompEquipmentTypes` (natural-key sync) is not restamped. Direct cloud writes that others should pull (`ClientVersion.*`) use `CloudNowUtc()` too.
- **Startup pull re-applies equal-timestamp rows** (`BatchPush(..., applyOnEqual: true)`, `InitialPullTable` only): an older build that predates a column pulls a row, copies `LastModified`, and drops the unknown column — after which "newer only" skips it forever (happened to `Locations.AssignedPmId` and `RompStatuses.Color`). EF only UPDATEs rows whose values really differ, so this is free for normal rows. Corollary: **startup seeding of a newly added column must not stamp `LastModified`** (`SkipLastModifiedStamp`), or the seed looks newer than the cloud and gets pushed over real values.
- **Per-machine Id ranges** (`IdAllocator`): local SQLite autoincrement gives every in-sync machine the same "max + 1", and sync matches rows by `Id`, so concurrent creates on two machines collided (one silently overwrote/skipped the other). In Cloud mode each machine claims slot N once (atomic insert of cloud `AppConfig` key `IdSlot.N`, value starting with the machine name; saved as `IdSlot`/`IdSlotCloud` in `dbconfig.json`). A saved slot is only used after `EnsureSlotClaimed` confirms the cloud row names this machine — otherwise it re-claims (preferring the saved number) and, if it lost that number, `RenumberUnsyncedRows` moves its local-only rows from the old range to the new one, FKs included and new rows get Ids in `[N×20M, (N+1)×20M)`, assigned in `AppDbContext`'s `ChangeTracker.Tracked` hook (before navigation fix-up, so child FKs follow). Rows with an explicit Id (pulled copies) keep it. Before a slot is claimed, Cloud mode allocates from the legacy range `< 20M` (re-read every time, since older builds still write there). Local/Network modes keep plain autoincrement — Network shares one SQLite file, where an in-memory counter would itself collide. `RompEquipmentType` is excluded (synced by natural key).

### LastModified Column
16 tables (`PMs`, `Locations`, `Sites`, `Romps`, `RompEquipmentTypes`, `Orders`, `RompStatuses`, `EquipmentCategories`, `EquipmentKeywords`, `AssignmentRules`, `AppConfigs`, `SavedReports`, `CustomColumns`, `CustomRows`, `Users`, `AuditEntries`) have `LastModified TEXT NOT NULL DEFAULT '2000-01-01 00:00:00'`. Migration via `ALTER TABLE ... ADD COLUMN` (try/catch). **Must run before any EF Core query** — placed immediately after other ALTER TABLE blocks in `App.OnStartup`. `Order`/`Site`/`Romp` additionally have a `LastModifiedBy TEXT` column (same migration pattern), auto-stamped from `CurrentUser.Email` by `AppDbContext.StampLastModified()` whenever the entity has that property — powers both Report Builder's "Last Changed By" field and the cross-user `DataChanged` notification summary above.

### MariaDB Setup
Run `SyncService.MariaDbDdlScript` (Config → Database → phpMyAdmin Setup Script → Copy Script) in phpMyAdmin.

The DDL script includes `PmMultiplier INT NULL` on the `PMs` table and `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` guards for columns added after initial setup. If a column is missing from an existing MariaDB table and `IF NOT EXISTS` isn't supported, run the ALTER manually in phpMyAdmin (e.g. `ALTER TABLE \`PMs\` ADD COLUMN \`PmMultiplier\` INT NULL;`).

## Services

### StatusConfig
Static cache. `ScoreWeights` is `Dictionary<string, double>` (activity multipliers).

### ScoreConfig
Static cache. `CategoryRule.Weight` is `double`. Exposes `EquipmentWeightSum(IEnumerable<string> categoryNames)` and `WorkloadParams`.

### ScoringEngine
Pure stateless class. Methods: `EquipWeightSum`, `RompScore`, `SiteFactor`, `PmLoad`, `SkillFactor`, `EffectiveCapacity`, `Utilization`, `Availability`, `PmStatus`. Linked directly into `PMDashboard.Tests`.

`WorkloadParams` is a `readonly record struct` with positional constructor `(SiteOverhead, TargetCapacity, NearFullThreshold, OverloadThreshold)` plus `{ get; init; }` properties `SkillBase` and `SkillPerPoint` (backward-compatible — existing `new WorkloadParams(a,b,c,d)` calls still compile).

`Utilization` has two overloads: `Utilization(pmLoad, effCapacity)` (primary) and `Utilization(pmLoad, WorkloadParams)` (backward-compat, divides by `TargetCapacity` directly — used by existing tests).

### AutoImportService
Background timer; polls configured folder; routes to correct parser; deduplicates via `_seen` HashSet.

## Coding Conventions

### DbContext Pattern
**Every method creates its own `using var db = new AppDbContext()`** — no shared long-lived context. In Cloud mode, regular contexts use local SQLite. Only the sync/lock/Id-slot services use `forceCloud: true`.

**A no-tracking query with an `Include` path that loops back to the root entity type throws `InvalidOperationException: ... results in a cycle`, not a silent wrong result.** E.g. querying `Romps` with `.Include(r => r.Site).ThenInclude(s => s.Romps)` fails, since `Site.Romps` re-enters the `Romp` type already at the root of the query. When a related aggregate (like `Site.SiteStatus`, which reads `Site.Romps`) is needed alongside a query rooted on one of that aggregate's children, compute it via a second, separately-rooted query instead of extending the `Include` chain.

### Schema Changes
Use `EnsureCreated()` — no migrations. New columns via `ALTER TABLE ... ADD COLUMN` wrapped in try/catch. **LastModified migrations must run before any EF Core query.** New tables via `CREATE TABLE IF NOT EXISTS`.

**`ExecuteSqlRaw(sql)` treats `sql` as a composite format string even with zero parameters.** A literal `{` or `}` anywhere in the raw SQL (e.g. `DEFAULT '{}'` for a JSON-default column) throws `FormatException: ... Expected an ASCII digit` at startup, before the window even renders. Escape literal braces as `{{`/`}}` in any `CREATE TABLE`/`ALTER TABLE` string passed to `ExecuteSqlRaw`.

Reseed: delete `%LOCALAPPDATA%\PMDashboard\data.db`. Also delete `%APPDATA%\PMDashboard\dbconfig.json` to reset DB mode to Local.

### Static Config Caches
`StatusConfig` and `ScoreConfig` are app-lifetime statics. Call `.Reload()` after any DB change to the respective tables.

### ConfigViewModel Workload Params
Workload params (`SiteOverhead`, `TargetCapacity`, `NearFullThreshold`, `OverloadThreshold`, `SkillBase`, `SkillPerPoint`) are stored as `double` properties — not strings. Sliders bind directly to these. `SaveWorkloadParams()` serialises them with InvariantCulture.

### PasswordBox Persistence
`PasswordBox` controls do not support WPF data binding. The cloud DB password is saved as part of the full connection string in `dbconfig.json`. On `ConfigPage` load, `ConfigViewModel.LoadDatabaseSettings()` parses the password back from the connection string, and the `Loaded` event in `ConfigPage.xaml.cs` pushes `vm.CloudPassword` to `TxtCloudPassword.Password` explicitly.

### Configuration Access Pattern
The whole Configuration page is gated on `ConfigViewModel.IsSuperAdmin` (`CurrentUser.IsSuperAdmin`) — see Configuration's Access Gate section. There is no separate per-card lock or shared password anymore; a role check at the page level is the only gate.

### ViewModel Navigation
`MainViewModel` holds all page VMs as private fields. `NavigateToXxx()` applies geo filter then sets `CurrentPage`. `NavigateToPmDetail(int pmId)` sets `NavigateBack` AND `NavigateToSiteRomps` delegates. `NavigateToSiteRompsFromPm(siteId, pmId)` pre-drills the Sites page and sets a ← back button.

### SitesViewModel Back Button
`HasBackButton`, `BackButtonLabel`, `NavigateBackAction` — set by `MainViewModel` when navigating from PM Detail. Cleared by `ApplyGeoFilter()` (normal sidebar navigation).

### Dynamic Status Dropdowns
All ROMP status dropdowns read from `StatusConfig.All` at runtime.

### AddEditRompDialog
Includes multi-select equipment type checkboxes (non-excluded categories). Returns `SelectedEquipmentCategoryIds: IReadOnlyList<int>`. Callers persist to `RompEquipmentTypes` table.

### Null-safe View Accessors
All code-behind uses `private XxxViewModel? Vm => DataContext as XxxViewModel` with `if (Vm is not { } vm) return` guards.

### Slider Pattern in DataGrids
Status score weight: `DataGridTemplateColumn`, slider `Minimum=0 Maximum=2.4`, saves via `Thumb.DragCompleted="StatusScoreSlider_DragCompleted"`.
Equipment score weight: same pattern with `Maximum=3.2`. Save handlers in `ConfigPage.xaml.cs`.

### Adding New Pages
1. Create `ViewModels/XxxViewModel.cs` (extend `ObservableObject`)
2. Create `Views/XxxPage.xaml` (UserControl) + `.cs`
3. Add `<DataTemplate DataType="{x:Type vm:XxxViewModel}"><views:XxxPage/></DataTemplate>` in `App.xaml`
4. Add `private readonly XxxViewModel _xxxVm = new();` in `MainViewModel`
5. Add `NavigateToXxx()` method in `MainViewModel`
6. Add `RadioButton` in `MainWindow.xaml` sidebar + handler

## Versioning & Publishing

The app version is `<Version>` in `PMDashboard.csproj` (plus a `BuildDateUtc` assembly-metadata stamp). **Every build that gets installed anywhere must have its own number** — publish with the script, which bumps it first:

```
.\publish.ps1                 # patch: bug fixes      1.0.0 -> 1.0.1
.\publish.ps1 -Bump minor     # new features          1.0.1 -> 1.1.0
.\publish.ps1 -Bump major     # big/breaking changes  1.1.0 -> 2.0.0
.\publish.ps1 -Bump none      # rebuild same version
```
(wraps `dotnet publish -c Release -r win-x64 --self-contained true`; output in `bin\Release\net8.0-windows\win-x64\publish`. If PowerShell blocks scripts: `powershell -ExecutionPolicy Bypass -File .\publish.ps1`.) Commit the bump afterwards so source matches what's installed.

Where the version shows up: header (under "Site Assignment Tracker", tooltip = build time), window title, login dialog, `sync-debug.log` "Connecting to cloud" line, every `crash.log` entry, and Login/AutoLogin audit details. In Cloud mode each machine writes `AppConfig` `ClientVersion.{MachineName}` (JSON: Version, BuildDateUtc, User, LastSeenUtc) straight to the cloud at startup; those rows sync to everyone, feeding Configuration → Database → **Versions in use** and an amber header banner on any machine whose version is older than another machine's (`CloudSyncService.NewerVersionInUse`, rechecked every ~10 min). An outdated build silently drops columns it doesn't know about when it syncs, so the banner matters.

## Running / Building

```
cd C:\Users\IMVRT\Documents\Projects\PMDashboard
dotnet build
dotnet run
```

Tests:
```
cd C:\Users\IMVRT\Documents\Projects\PMDashboard.Tests
dotnet test
```

Or open `PMDashboard.csproj` in Visual Studio 2022+.

To reset all data: delete `%LOCALAPPDATA%\PMDashboard\data.db` (and `-shm`/`-wal`). Also delete `%APPDATA%\PMDashboard\dbconfig.json` to reset to Local mode.
