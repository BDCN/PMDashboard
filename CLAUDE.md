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
- **Admin config file:** `%APPDATA%\PMDashboard\adminconfig.json` (admin-gate password) — added 2026-08-27 when this project was first pushed to GitHub (`AdminConfig.cs`, mirrors `DatabaseConfig`'s pattern). `PasswordDialog.xaml.cs` used to have the real password as a hardcoded `const string` — that would have shipped straight into git history, so it was moved out of source into this per-machine file. **A fresh machine/clone has no admin password configured until this file is created there** (no default ships in source, and `TryConfirm()` deliberately never matches on an empty/unconfigured password) — there's currently no in-app "set admin password" UI, so seed it manually: write `{"Password": "<value>"}` to that path, or ask whoever set the original value.
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
│       ├── Site.cs                 # FK → Location, FK? → PM, SiteStatus computed, LastModified
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
│       └── CustomRow.cs            # Custom Data grid row: ValuesJson (dict keyed by CustomColumn.Key) + LastModified
├── ViewModels/
│   ├── MainViewModel.cs            # CurrentPage, GeoFilter, Navigate* methods incl. NavigateToPmDetail + NavigateToSiteRompsFromPm
│   ├── PmMaintenanceViewModel.cs   # Live-edit grid, geo filter, BulkAddPms, GetAllPms
│   ├── SitesViewModel.cs           # 3-level drill-down; NavigateDirectToSiteRomps; BackButtonLabel/NavigateBackAction
│   ├── OrdersViewModel.cs          # UpsertOrders, site/ROMP auto-creation, LastUploadDate
│   ├── WorkloadViewModel.cs        # New multiplicative scoring formula, mode-aware KPI counts (Overloaded/NearFull/HasCapacity)
│   ├── PmDetailViewModel.cs        # PM detail: BulkUnassignSites, NavigateToSiteRomps delegate, CurrentPmId
│   ├── AssignmentViewModel.cs      # Multi-select site ListBox, SetSelectedSites, ComputeCandidates uses SelectedLocation
│   ├── ConfigViewModel.cs          # All config: DB mode, workload params (double), IsAdminUnlocked, SyncEquipmentFromOrders
│   ├── ReportBuilderViewModel.cs   # Mode (Orders/SitesRomps) toggle, filters, column picker, saved-report CRUD, chart config
│   └── CustomDataViewModel.cs      # Custom Data: real Orders/Sites&ROMPs rows (reuses ReportQueryService) + attached dynamic columns; CustomDataRow (Static/DynamicValues), row CRUD, column CRUD (shared with ManageColumnsDialog)
├── Views/
│   ├── PmMaintenancePage.xaml/.cs  # Live-edit DataGrid — includes Score Cap column
│   ├── SitesPage.xaml/.cs          # Drill-down: Locations → Sites → ROMPs; ← Back-to-PM button when navigated from PM Detail
│   ├── OrdersPage.xaml/.cs         # Import routing by SourceDataType, last-upload stat
│   ├── WorkloadPage.xaml/.cs       # Bar chart; mode-aware KPI cards; Score mode: Overloaded/Near Full/Has Capacity
│   ├── PmDetailPage.xaml/.cs       # PM detail: checkbox bulk-unassign, Edit/Unassign per-site, clickable site name → ROMs
│   ├── AssignmentPage.xaml/.cs     # Multi-select ListBox (Ctrl+Click/Shift+Click); All/Unassigned/Clear buttons; geo badge
│   ├── ConfigPage.xaml/.cs         # Reordered cards; sliders on params/statuses/equipment; single admin lock
│   ├── ReportBuilderPage.xaml/.cs  # Filter panel + dynamic results grid + bar chart + saved-report strip; grid/picker columns built in code-behind, driven by VM PropertyChanged
│   └── CustomDataPage.xaml/.cs     # Mode toggle + date-range filter over real Orders/Sites&ROMPs data, plus attached editable dynamic columns; DataGrid.Columns built in code-behind, driven by VM PropertyChanged
├── Dialogs/
│   ├── AddEditPmDialog.xaml/.cs
│   ├── AddEditSiteDialog.xaml/.cs  # BulkRompStatus loaded from StatusConfig
│   ├── AddEditRompDialog.xaml/.cs  # Status dropdown + multi-select equipment type checkboxes
│   ├── AddEditLocationDialog.xaml/.cs
│   ├── AddEditRuleDialog.xaml/.cs
│   ├── AddEditStatusDialog.xaml/.cs
│   ├── AddEquipmentWeightDialog.xaml/.cs
│   ├── AssignPmDialog.xaml/.cs
│   ├── PasswordDialog.xaml/.cs     # Admin password gate (password = RowanU)
│   ├── TextInputDialog.xaml/.cs    # Generic single-line text prompt (Report Builder Save As / Rename)
│   └── ManageColumnsDialog.xaml/.cs # Custom Data's column setup screen — add/rename/reorder/delete CustomColumns; shares the CustomDataViewModel instance passed in from CustomDataPage
└── Services/
    ├── ExcelImportService.cs       # ImportOrders (VADATA), ImportTableauOrders, ImportOpenLinesOrders
    ├── PmImportService.cs          # PM bulk template generate/parse
    ├── ConfigService.cs            # Get/Set AppConfig rows; ConfigKeys constants
    ├── StatusConfig.cs             # App-lifetime cache: status lists + ScoreWeights (Dictionary<string,double>)
    ├── ScoreConfig.cs              # App-lifetime cache: CategoryRule (double Weight), WorkloadParams, EquipmentWeightSum
    ├── ScoringEngine.cs            # Pure stateless scoring logic (testable): RompScore, PmLoad, Availability, PmStatus
    ├── DatabaseConfig.cs           # Persistent DB mode config in %APPDATA%\PMDashboard\dbconfig.json
    ├── SyncService.cs              # Network travel sync (SQLite ATTACH bidirectional merge) + MariaDB DDL script
    ├── CloudSyncService.cs         # Background cloud sync: initial pull + periodic push/pull via LastModified
    ├── AutoImportService.cs        # Background timer, file polling, seen-file dedup, RunUpsert
    ├── AirportLookup.cs            # Static IATA code → (Name, StateOrCountry, GeoRegion)
    ├── ImportResult.cs             # Record: Added, Updated, Skipped, LocationsCreated, ...
    ├── NullToBoolConverter.cs      # IValueConverter: null → false
    ├── ReportFieldCatalog.cs       # ReportField/ReportFieldType, SiteRompFlatRow, ReportRow, curated Order/SiteRomp field catalogs + value getters
    ├── ReportFilterState.cs        # Serializable filter DTO (date range, multi-select IDs/statuses, text search)
    ├── ReportQueryService.cs       # Builds/executes the two mode-specific query pipelines (Orders / Sites+ROMPs)
    ├── ReportExportService.cs      # ClosedXML export of report results, styled per PmImportService convention
    └── CustomCellValueConverter.cs # IValueConverter coercing a boxed Values[key] object to/from each ColumnType's editing control (stateless; ConverterParameter carries the type)

PMDashboard.Tests/                  # xUnit test project (26 tests); links ScoringEngine.cs directly
└── ScoringTests.cs
```

## Layout

### App Header (MainWindow.xaml)
- **64px white header bar** spans the full window width above the sidebar and content area
- **Left:** IEM logo (`Assets/iem-logo.png`) — white background blends seamlessly with the white header
- **Right:** "PM Dashboard" + "Resource Management" subtitle
- Header background `White` with a `#E2E8F0` bottom border separates it from the sidebar/content

### Sidebar
Sidebar order (top → bottom): **Workload → Assignment → Sites → PM Maintenance → Report Builder → Custom Data → Orders (Import)** then **CONFIG** section with **Configuration**.

Geographic Location filter ComboBox (All / East / West) at the bottom of the sidebar — pushes filter to all relevant VMs via `MainViewModel.OnGeoFilterChanged`.

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
- Horizontal bar chart — one row per active PM, sorted by utilisation ascending
- **Auto-scaling**: `ChartGrid.SizeChanged` → `WorkloadViewModel.ApplyScale(barWidth)`
- **Inside label**: when utilization > 80%, label appears inside bar in white; otherwise outside
- **Score mode label**: `Score: 11.77 ⚠  ·  65.4% util  ·  34.6% avail  ·  2 sites / 6 ROMPs`
- **Hover tooltip**: per-status ROMP counts + PM Load, Utilization %, Availability %, Status
- **Click any row** → navigates to PM Detail page
- **KPI cards (Sites mode)**: Over Capacity, On Target, Under Target, No Capacity Set, Avg Utilisation
- **KPI cards (Score mode)**: Overloaded (util > threshold), Near Full (≥ nearfull), Has Capacity, No Capacity Set, Avg Utilisation
- Cards switch via `IsScoreMode` DataTrigger

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

### Assignment
- Left panel: Location (ComboBox + geo badge East/West) → multi-select site ListBox → optional ROMP
- **Site ListBox**: `SelectionMode=Extended` (Ctrl+Click / Shift+Click); quick-select buttons **All / Unassigned / None**; selection counter below; ROMP section only visible when exactly 1 site selected
- Right panel: ranked PM candidates via `AssignmentViewModel.ComputeCandidates()` using `SelectedLocation` region
- Assign → assigns PM to ALL selected sites; confirmation dialog for 2+ sites
- `RestoreListBoxSelection()` re-selects the same sites after assignment

### Sites (3-level drill-down)
- `SiteNavLevel` enum: Locations | Sites | Romps
- **← Back to [PM Name]** button appears when navigated from PM Detail via `HasBackButton` / `BackButtonLabel` properties
- ROMP status dropdowns driven by `StatusConfig`

### PM Maintenance
- **Live-edit DataGrid**: click a cell to edit inline; `RowEditEnding` → `UpdatePm(pm)` saves automatically
- Columns: Active, First Name, Last Name, Home Office, Region, Data Name, **Site Cap**, **Score Cap**, **Multiplier**, Ideal Util (%)

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
Shows **every real Order or Site/ROMP already in the database** (mode toggle, same Orders/SitesRomps split and date-range filter as Report Builder — reuses `ReportFieldCatalog`/`ReportQueryService`/`ReportFilterState` directly rather than duplicating that logic), with a "Manage Columns" **setup screen** that lets any user layer their own extra columns (Text/Number/Date/Bool) on top and fill in values per row — shared across all users via the normal sync path, not per-user local state. Static columns (the real Order/ROMP fields) are read-only; only the dynamic columns are editable. This deliberately does **not** work like a blank standalone spreadsheet — every row is tied to a real `Order`/ROMP `Id`, not a freestanding record.
- **Schema**: `CustomColumn` (`Mode`, `Name`, `Key`, `ColumnType`, `SortOrder`) + `CustomRow` (`Mode`, `EntityId`, `ValuesJson`). A `CustomRow` only exists once a user enters at least one dynamic value for that Order/ROMP — most rows have none. `EntityId` is that `Order.Id` or the flattened ROMP's `RompId`, **not** an independent identity; `SaveRow()` looks up by `(Mode, EntityId)` and inserts only if no row exists yet (no DB-level unique constraint — relies on this app's single-writer desktop usage pattern, same as elsewhere in the codebase). Both `CustomColumn` and `CustomRow` are scoped by `Mode` since Orders and Sites/ROMPs are unrelated row sets — one "Notes" column added while in Sites & ROMPs mode never appears in Orders mode.
- **`Key` is generated once at creation** (`col{Id}`, set in a second `SaveChanges()` after the row gets its auto-increment `Id`) and never changes — renaming a column's display `Name` never orphans existing values. **`ColumnType` is immutable after creation** — changing it would make existing JSON values (stored per the old type) fail to parse; delete and re-add the column instead.
- **`CustomRow.SortOrder` is vestigial** — kept in both the model and every `CREATE TABLE`/DDL statement (`DEFAULT 0`) purely because installs that had this table before the "attach to a real entity" redesign already have that column as `NOT NULL` with no default; dropping it from the C# model made EF's `INSERT` omit it and violate that constraint (`SQLite Error 19`), crashing on every first dynamic-cell edit — caught by actually editing a cell, not by the build or test suite.
- **Typed round-trip**: `CustomDataViewModel.BuildRow()` deserializes each `CustomRow.ValuesJson` into `Dictionary<string, JsonElement>` first, then coerces each value to the column's native CLR type (`double`/`DateTime`/`bool`/`string`) before exposing it as `CustomDataRow.DynamicValues[key]` (kept separate from the read-only `StaticValues[key]` dict sourced from `ReportFieldCatalog`) — binding a `DataGrid` cell straight to a raw `JsonElement` wouldn't work with `DatePicker`/`CheckBox` controls.
- **`CustomCellValueConverter`** (stateless `IValueConverter`, `ConverterParameter` = `ColumnType`) is what makes a single dictionary-indexer binding (`DynamicValues[key]`) work across `DataGridTextColumn` (Text/Number), `DataGridCheckBoxColumn` (Bool), and a `DataGridTemplateColumn` wrapping a `DatePicker` (Date) — same "columns built in code-behind, driven by VM `PropertyChanged`" pattern as Report Builder (now firing off both `StaticFields` and `DynamicColumns` changing), but the dynamic half is editable (`Mode=TwoWay`) while the static half is read-only.
- **Setup screen** (`ManageColumnsDialog`) is a modal `Window` that takes the *same* `CustomDataViewModel` instance the page uses (passed via constructor, not a separate ViewModel) — column CRUD lives on the shared VM (`AddColumn`/`RenameColumn`/`DeleteColumn`/`MoveColumnUp`/`MoveColumnDown`, mirroring `ConfigViewModel`'s `RompStatus`/`EquipmentCategory` reorder pattern), scoped to whichever `Mode` is currently active, so both surfaces always agree.
- **Column CRUD calls `RefreshDynamicColumns()`, not a full `RunQuery()`** — it reloads `DynamicColumns` and resyncs already-loaded `Rows`' `DynamicValues` from the DB, without re-running the (unchanged, and for Orders mode potentially 100k+-row) static Orders/ROMPs query.
- **Row edits commit via `DataGrid.RowEditEnding`** → `SaveRow()`, same as `PmMaintenancePage`'s inline-edit convention — but unlike `PmMaintenanceViewModel.UpdatePm()`, `SaveRow()` does **not** reload the grid afterward, so editing one cell doesn't reset scroll position/selection.

### Orders (Import)
- Routes to three parsers based on `ConfigService.Get(SourceDataType)`
- Upsert by `(CustomerOrder, OrdLine)` key; auto-creates Locations/Sites/ROMPs from JobName

### Configuration
Cards in order (top → bottom):
1. **Upload Method** — Manual or Semi-Automatic; import folder + poll interval when Semi-Auto
2. **Assignment Rules** — enable/disable, weight, reorder, + Add Custom Rule
3. **Percentage Calculation** — Sites (default) or Score mode radio buttons
4. **Workload Scoring Parameters** — sliders + textboxes for SiteOverhead, TargetCapacity, NearFullThreshold, OverloadThreshold; "Save Parameters" + "Sync Equipment from Orders"
5. **Site/ROMP Statuses** — status list with inline **score weight slider** (0–2.4; CX default 1.2 = 50%)
6. **Equipment Score Categories** — categories DataGrid with inline **score weight slider** (0–3.2; PAX default 1.6 = 50%)
7. **Source Data** — VADATA / Tableau / Open Lines (admin-locked, see below)
8. **Database** — Local / Network / Cloud modes (admin-locked, see below)

#### Admin Lock (Source Data + Database)
A single amber/green banner above the Source Data card controls both sections:
- **Locked**: both cards are 40% opacity, disabled, single **Unlock** button visible
- **Unlocked**: `ConfigViewModel.IsAdminUnlocked = true` → both cards fully interactive, banner turns green
- One `PasswordDialog` (password = RowanU) unlocks both for the current session
- `ConfigViewModel.IsAdminUnlocked` replaces the old `IsSourceDataUnlocked` / `IsDatabaseUnlocked` booleans
- XAML still uses `IsSourceDataUnlocked` and `IsDatabaseUnlocked` as computed aliases → both return `IsAdminUnlocked`

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

## Data Hierarchy

```
Location (3-letter airport code, GeographicalLocation East|West)
  └── Site (Name, AssignedPmId? FK → PM, SiteStatus computed, LastModified)
        └── Romp (Name, Status, LastModified)
              └── RompEquipmentType (junction → EquipmentCategory)
```

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
| GeographicalLocation | string | East, West |
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
- `new AppDbContext { ForceCloud = true }` — uses MariaDB; used exclusively by `CloudSyncService`
- **Never** use `ForceCloud = true` in ViewModels or other app code

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

### LastModified Column
All 11 tables have `LastModified TEXT NOT NULL DEFAULT '2000-01-01 00:00:00'`. Migration via `ALTER TABLE ... ADD COLUMN` (try/catch). **Must run before any EF Core query** — placed immediately after other ALTER TABLE blocks in `App.OnStartup`.

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
**Every method creates its own `using var db = new AppDbContext()`** — no shared long-lived context. In Cloud mode, regular contexts use local SQLite. Only `CloudSyncService` uses `ForceCloud = true`.

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

### Admin Lock Pattern
`ConfigViewModel.IsAdminUnlocked` (single bool) controls both Source Data and Database sections. `IsSourceDataUnlocked` and `IsDatabaseUnlocked` are computed aliases. One `PasswordDialog` call sets all. Lock resets on app restart.

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
