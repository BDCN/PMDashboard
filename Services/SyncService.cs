using System.IO;
using Microsoft.Data.Sqlite;

namespace PMDashboard.Services;

public record SyncResult(bool Success, string Message, int Pushed = 0, int Pulled = 0);

/// <summary>
/// Handles syncing between the local SQLite database and a network (shared) SQLite database.
///
/// Strategy — LastModified-based bidirectional merge:
///   SyncBeforeTravel  — copy network DB → local; enable travel mode.
///   SyncAfterTravel   — bidirectional merge using LastModified timestamps.
///                       For each row in each table:
///                         • exists only in local    → INSERT into network
///                         • exists only in network  → INSERT into local
///                         • exists in both, local newer   → push local version to network
///                         • exists in both, network newer → pull network version to local
///                         • equal timestamps              → no action (already in sync)
///
/// Limitation: record DELETIONS are not propagated.  If a row is deleted on one side
/// while the other is offline, the deletion will not be reflected after sync.
/// A tombstone table would be required to propagate deletes.
/// </summary>
public static class SyncService
{
    // Integer-PK tables in FK-safe order (parents before children)
    private static readonly string[] _intPkTables =
    [
        "PMs",
        "Locations",
        "EquipmentCategories",
        "EquipmentKeywords",
        "RompStatuses",
        "AssignmentRules",
        "Sites",
        "Romps",
        "RompEquipmentTypes",
        "Orders",
        "SavedReports",
        "CustomColumns",
        "CustomRows",
        "Users",
        "AuditEntries",
    ];

    // ── Before travel ─────────────────────────────────────────────────────────

    /// <summary>
    /// Download: copy the network DB to the local path and switch the app into travel mode.
    /// </summary>
    public static SyncResult SyncBeforeTravel(string networkPath)
    {
        if (!File.Exists(networkPath))
            return new SyncResult(false, $"Network database not found:\n{networkPath}");

        try
        {
            var localPath = DatabaseConfig.LocalDbPath;
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

            File.Copy(networkPath, localPath, overwrite: true);
            CopyWalFiles(networkPath, localPath);

            DatabaseConfig.SetTravelMode(true);
            return new SyncResult(true,
                "Network database copied to local.\n" +
                "You are now in Travel Mode — the app uses your local copy.\n\n" +
                "When you return to the office, click \"Sync After Travel\" to merge your changes back.");
        }
        catch (Exception ex)
        {
            return new SyncResult(false, $"Sync failed:\n{ex.Message}");
        }
    }

    // ── After travel ──────────────────────────────────────────────────────────

    /// <summary>
    /// Bidirectional merge using LastModified timestamps (most recent wins).
    /// Both databases are updated in a single ATTACH session.
    /// Foreign-key checks are suspended for the duration to avoid cascade side-effects
    /// from INSERT OR REPLACE's internal delete-then-insert behaviour.
    /// </summary>
    public static SyncResult SyncAfterTravel(string networkPath)
    {
        var localPath = DatabaseConfig.LocalDbPath;

        if (!File.Exists(localPath))
            return new SyncResult(false, "Local database not found.");
        if (!File.Exists(networkPath))
            return new SyncResult(false, $"Network database not found:\n{networkPath}");

        try
        {
            int pushed = 0, pulled = 0;

            var cs = new SqliteConnectionStringBuilder { DataSource = localPath }.ToString();
            using (var conn = new SqliteConnection(cs))
            {
                conn.Open();

                Execute(conn, $"ATTACH DATABASE '{Escape(networkPath)}' AS net");
                Execute(conn, "PRAGMA foreign_keys = OFF");   // prevent cascade during REPLACE

                // ── Integer-PK tables ──────────────────────────────────────────
                foreach (var table in _intPkTables)
                {
                    // Push: local rows that are newer (or absent) on network
                    pushed += ExecCount(conn, $"""
                        INSERT OR REPLACE INTO net.{table}
                        SELECT l.* FROM main.{table} l
                        WHERE l.Id NOT IN (SELECT Id FROM net.{table})
                           OR l.LastModified > (
                               SELECT r.LastModified FROM net.{table} r WHERE r.Id = l.Id)
                        """);

                    // Pull: network rows that are newer (or absent) on local
                    pulled += ExecCount(conn, $"""
                        INSERT OR REPLACE INTO main.{table}
                        SELECT r.* FROM net.{table} r
                        WHERE r.Id NOT IN (SELECT Id FROM main.{table})
                           OR r.LastModified > (
                               SELECT l.LastModified FROM main.{table} l WHERE l.Id = r.Id)
                        """);
                }

                // ── AppConfigs (text PK) ───────────────────────────────────────
                pushed += ExecCount(conn, """
                    INSERT OR REPLACE INTO net.AppConfigs
                    SELECT l.* FROM main.AppConfigs l
                    WHERE l.[Key] NOT IN (SELECT [Key] FROM net.AppConfigs)
                       OR l.LastModified > (
                           SELECT r.LastModified FROM net.AppConfigs r WHERE r.[Key] = l.[Key])
                    """);

                pulled += ExecCount(conn, """
                    INSERT OR REPLACE INTO main.AppConfigs
                    SELECT r.* FROM net.AppConfigs r
                    WHERE r.[Key] NOT IN (SELECT [Key] FROM main.AppConfigs)
                       OR r.LastModified > (
                           SELECT l.LastModified FROM main.AppConfigs l WHERE l.[Key] = r.[Key])
                    """);

                Execute(conn, "PRAGMA foreign_keys = ON");
                Execute(conn, "DETACH DATABASE net");
            }

            DatabaseConfig.SetTravelMode(false);
            return new SyncResult(true,
                $"Sync complete.\n" +
                $"  Pushed to network : {pushed} row(s)\n" +
                $"  Pulled from network: {pulled} row(s)\n\n" +
                "Both databases are now in sync. Travel mode is OFF.",
                Pushed: pushed, Pulled: pulled);
        }
        catch (Exception ex)
        {
            return new SyncResult(false, $"Sync failed:\n{ex.Message}");
        }
    }

    // ── Network DB setup ─────────────────────────────────────────────────────

    /// <summary>
    /// Create the network database by copying the current local DB to the chosen path.
    /// </summary>
    public static SyncResult CreateNetworkDatabase(string networkPath)
    {
        var localPath = DatabaseConfig.LocalDbPath;
        if (!File.Exists(localPath))
            return new SyncResult(false, "Local database not found — launch the app at least once first.");

        try
        {
            var dir = Path.GetDirectoryName(networkPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (File.Exists(networkPath))
                return new SyncResult(false,
                    $"A database already exists at:\n{networkPath}\n\nDelete it first if you want to overwrite.");

            File.Copy(localPath, networkPath);
            return new SyncResult(true, $"Network database created at:\n{networkPath}");
        }
        catch (Exception ex)
        {
            return new SyncResult(false, $"Could not create network database:\n{ex.Message}");
        }
    }

    // ── Cloud connection test ─────────────────────────────────────────────────

    public static async Task<SyncResult> TestCloudConnectionAsync(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return new SyncResult(false, "Connection string is empty.");
        try
        {
            using var conn = new MySqlConnector.MySqlConnection(connectionString);
            await conn.OpenAsync();
            return new SyncResult(true, $"Connection successful.\nServer: {conn.ServerVersion}");
        }
        catch (Exception ex)
        {
            return new SyncResult(false, $"Connection failed:\n{ex.Message}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void CopyWalFiles(string src, string dst)
    {
        foreach (var ext in new[] { "-wal", "-shm" })
        {
            var s = src + ext;
            var d = dst + ext;
            if (File.Exists(s)) File.Copy(s, d, overwrite: true);
        }
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static int ExecCount(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    private static string Escape(string s) => s.Replace("'", "''");

    // ── MariaDB DDL script ────────────────────────────────────────────────────

    public static string MariaDbDdlScript => """
        -- ================================================================
        -- PMDashboard — MariaDB / MySQL Setup Script
        -- Run this entire script in phpMyAdmin → SQL tab on your server.
        -- After running, enter your connection details in the app and click
        -- "Test Connection".  The app will seed built-in data on first launch.
        -- ================================================================

        CREATE DATABASE IF NOT EXISTS `pmdashboard`
            CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
        USE `pmdashboard`;

        -- AppConfig (key/value store) ────────────────────────────────────
        CREATE TABLE IF NOT EXISTS `AppConfigs` (
            `Key`          VARCHAR(255) NOT NULL,
            `Value`        LONGTEXT     NOT NULL DEFAULT '',
            `LastModified` DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                           ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Key`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Project Managers ───────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS `PMs` (
            `Id`                   INT          NOT NULL AUTO_INCREMENT,
            `FirstName`            LONGTEXT     NOT NULL,
            `LastName`             LONGTEXT     NOT NULL,
            `HomeOffice`           LONGTEXT     NOT NULL,
            `GeographicalLocation` LONGTEXT     NOT NULL,
            `DataName`             LONGTEXT     NOT NULL,
            `Capacity`             DOUBLE       NOT NULL DEFAULT 0,
            `IdealUtilization`     DOUBLE       NOT NULL DEFAULT 0,
            `ScoreCapacity`        DOUBLE       NOT NULL DEFAULT 0,
            `PmMultiplier`         INT          NULL,
            `IsActive`             TINYINT(1)   NOT NULL DEFAULT 1,
            `LastModified`         DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                                   ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Add PmMultiplier to existing PMs table (safe to run on already-migrated DBs)
        ALTER TABLE `PMs` ADD COLUMN IF NOT EXISTS `PmMultiplier` INT NULL;

        -- Locations (airport codes) ──────────────────────────────────────
        CREATE TABLE IF NOT EXISTS `Locations` (
            `Id`                   INT         NOT NULL AUTO_INCREMENT,
            `Name`                 LONGTEXT    NOT NULL,
            `GeographicalLocation` LONGTEXT    NOT NULL,
            `LastModified`         DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                                   ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Sites ──────────────────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS `Sites` (
            `Id`           INT         NOT NULL AUTO_INCREMENT,
            `Name`         LONGTEXT    NOT NULL,
            `LocationId`   INT         NOT NULL,
            `AssignedPmId` INT         NULL,
            `LastModified` DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                           ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`),
            CONSTRAINT `FK_Sites_Locations`
                FOREIGN KEY (`LocationId`) REFERENCES `Locations`(`Id`) ON DELETE CASCADE,
            CONSTRAINT `FK_Sites_PMs`
                FOREIGN KEY (`AssignedPmId`) REFERENCES `PMs`(`Id`) ON DELETE SET NULL
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- ROMP statuses (configurable) ───────────────────────────────────
        CREATE TABLE IF NOT EXISTS `RompStatuses` (
            `Id`             INT         NOT NULL AUTO_INCREMENT,
            `Name`           LONGTEXT    NOT NULL,
            `IsComplete`     TINYINT(1)  NOT NULL DEFAULT 0,
            `CountsAsActive` TINYINT(1)  NOT NULL DEFAULT 0,
            `ScoreWeight`    DOUBLE      NOT NULL DEFAULT 0,
            `SortOrder`      INT         NOT NULL DEFAULT 0,
            `IsBuiltIn`      TINYINT(1)  NOT NULL DEFAULT 0,
            `LastModified`   DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                             ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- ROMPs ──────────────────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS `Romps` (
            `Id`           INT         NOT NULL AUTO_INCREMENT,
            `Name`         LONGTEXT    NOT NULL,
            `SiteId`       INT         NOT NULL,
            `Status`       LONGTEXT    NOT NULL,
            `LastModified` DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                           ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`),
            CONSTRAINT `FK_Romps_Sites`
                FOREIGN KEY (`SiteId`) REFERENCES `Sites`(`Id`) ON DELETE CASCADE
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Equipment categories ───────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS `EquipmentCategories` (
            `Id`           INT         NOT NULL AUTO_INCREMENT,
            `Name`         LONGTEXT    NOT NULL,
            `Weight`       DOUBLE      NOT NULL DEFAULT 0,
            `IsExcluded`   TINYINT(1)  NOT NULL DEFAULT 0,
            `IsBuiltIn`    TINYINT(1)  NOT NULL DEFAULT 0,
            `SortOrder`    INT         NOT NULL DEFAULT 0,
            `LastModified` DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                           ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Equipment keywords ─────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS `EquipmentKeywords` (
            `Id`           INT         NOT NULL AUTO_INCREMENT,
            `CategoryId`   INT         NOT NULL,
            `Keyword`      LONGTEXT    NOT NULL,
            `LastModified` DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                           ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`),
            CONSTRAINT `FK_EquipmentKeywords_Categories`
                FOREIGN KEY (`CategoryId`) REFERENCES `EquipmentCategories`(`Id`) ON DELETE CASCADE
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Equipment weights (legacy, kept for schema compat) ─────────────
        CREATE TABLE IF NOT EXISTS `EquipmentWeights` (
            `Id`        INT        NOT NULL AUTO_INCREMENT,
            `Name`      LONGTEXT   NOT NULL,
            `Keyword`   LONGTEXT   NOT NULL,
            `Weight`    INT        NOT NULL DEFAULT 0,
            `IsExcluded` TINYINT(1) NOT NULL DEFAULT 0,
            `IsBuiltIn`  TINYINT(1) NOT NULL DEFAULT 0,
            `SortOrder`  INT        NOT NULL DEFAULT 0,
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- ROMP ↔ Equipment junction ───────────────────────────────────────
        CREATE TABLE IF NOT EXISTS `RompEquipmentTypes` (
            `Id`                  INT         NOT NULL AUTO_INCREMENT,
            `RompId`              INT         NOT NULL,
            `EquipmentCategoryId` INT         NOT NULL,
            `LastModified`        DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                                  ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`),
            UNIQUE KEY `UQ_RompEquipmentType` (`RompId`, `EquipmentCategoryId`),
            CONSTRAINT `FK_RompEquipmentTypes_Romps`
                FOREIGN KEY (`RompId`) REFERENCES `Romps`(`Id`) ON DELETE CASCADE,
            CONSTRAINT `FK_RompEquipmentTypes_Categories`
                FOREIGN KEY (`EquipmentCategoryId`) REFERENCES `EquipmentCategories`(`Id`) ON DELETE CASCADE
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Assignment rules ───────────────────────────────────────────────
        CREATE TABLE IF NOT EXISTS `AssignmentRules` (
            `Id`           INT         NOT NULL AUTO_INCREMENT,
            `Name`         LONGTEXT    NOT NULL,
            `Description`  LONGTEXT    NOT NULL,
            `RuleKey`      LONGTEXT    NOT NULL,
            `Weight`       INT         NOT NULL DEFAULT 1,
            `IsActive`     TINYINT(1)  NOT NULL DEFAULT 1,
            `SortOrder`    INT         NOT NULL DEFAULT 0,
            `IsBuiltIn`    TINYINT(1)  NOT NULL DEFAULT 0,
            `LastModified` DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                           ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Orders (imported from VADATA / Tableau / Open Lines) ───────────
        CREATE TABLE IF NOT EXISTS `Orders` (
            `Id`                            INT          NOT NULL AUTO_INCREMENT,
            `CustomerPO`                    VARCHAR(255) NULL,
            `CustPOLine`                    VARCHAR(255) NULL,
            `ShipHold`                      VARCHAR(255) NULL,
            `FaceSite`                      VARCHAR(255) NULL,
            `CustomerOrder`                 VARCHAR(255) NULL,
            `OrdLine`                       VARCHAR(255) NULL,
            `AssignSite`                    VARCHAR(255) NULL,
            `AssignWhse`                    VARCHAR(255) NULL,
            `JobName`                       VARCHAR(500) NULL,
            `IPN`                           VARCHAR(255) NULL,
            `Item`                          VARCHAR(255) NULL,
            `ItemDescription`               TEXT         NULL,
            `ItemDesignation`               VARCHAR(255) NULL,
            `QtyOrd`                        DOUBLE       NULL,
            `QtyShip`                       DOUBLE       NULL,
            `ShipDate`                      DATETIME     NULL,
            `DeliveryDate`                  DATETIME     NULL,
            `WarrantyExpiration`            DATETIME     NULL,
            `EstimatedDeliveryDate`         DATETIME     NULL,
            `EstimatedCompletionDate`       DATETIME     NULL,
            `ActualCompletionDate`          DATETIME     NULL,
            `RequestedCompletion`           DATETIME     NULL,
            `ProductionVariance`            VARCHAR(255) NULL,
            `RequestedOSD`                  VARCHAR(255) NULL,
            `DaysLate`                      INT          NULL,
            `PreviousEstCompletionDate`     DATETIME     NULL,
            `EstCompletionDateChange`       DATETIME     NULL,
            `ScheduledStartDate`            DATETIME     NULL,
            `ActualStartDate`               DATETIME     NULL,
            `InvoiceDate`                   DATETIME     NULL,
            `CustNum`                       VARCHAR(255) NULL,
            `OrderShipTo`                   VARCHAR(255) NULL,
            `LineItemShipTo`                VARCHAR(255) NULL,
            `CreditHold`                    VARCHAR(255) NULL,
            `Slsman`                        VARCHAR(255) NULL,
            `OrderStatus`                   VARCHAR(255) NULL,
            `ProductCode`                   VARCHAR(255) NULL,
            `Price`                         DOUBLE       NULL,
            `ExtPrice`                      DOUBLE       NULL,
            `SectionCount`                  INT          NULL,
            `EstCompletionDateChangeReason` TEXT         NULL,
            `OrderDate`                     DATETIME     NULL,
            `OrderLineStatus`               VARCHAR(255) NULL,
            `PromiseDate`                   DATETIME     NULL,
            `LastShipDate`                  DATETIME     NULL,
            `QtyInvoiced`                   DOUBLE       NULL,
            `SalesTax1`                     VARCHAR(255) NULL,
            `SalesTax2`                     VARCHAR(255) NULL,
            `TotalCOPrice`                  DOUBLE       NULL,
            `ProjectManagerDataName`        VARCHAR(255) NULL,
            `CustomerName`                  VARCHAR(255) NULL,
            `UploadDate`                    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
            `LastModified`                  DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                                            ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Saved Reports (Report Builder presets) ──────────────────────────
        CREATE TABLE IF NOT EXISTS `SavedReports` (
            `Id`                  INT          NOT NULL AUTO_INCREMENT,
            `Name`                LONGTEXT     NOT NULL,
            `Mode`                VARCHAR(50)  NOT NULL,
            `FilterJson`          LONGTEXT     NOT NULL,
            `SelectedColumnsJson` LONGTEXT     NOT NULL,
            `ChartGroupByField`   VARCHAR(255) NULL,
            `ChartAggregateField` VARCHAR(255) NULL,
            `ChartAggregateOp`    VARCHAR(50)  NOT NULL DEFAULT 'Count',
            `LastModified`        DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                                  ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Custom Data grid (user-defined dynamic columns) ─────────────────
        CREATE TABLE IF NOT EXISTS `CustomColumns` (
            `Id`           INT          NOT NULL AUTO_INCREMENT,
            `Mode`         VARCHAR(50)  NOT NULL DEFAULT 'Orders',
            `Name`         LONGTEXT     NOT NULL,
            `Key`          VARCHAR(255) NOT NULL,
            `ColumnType`   VARCHAR(50)  NOT NULL DEFAULT 'Text',
            `SortOrder`    INT          NOT NULL DEFAULT 0,
            `LastModified` DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                           ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Custom Data rows attach to a real Order/Romp by (Mode, EntityId) rather than
        -- standing alone — see CustomRow.cs.
        CREATE TABLE IF NOT EXISTS `CustomRows` (
            `Id`           INT          NOT NULL AUTO_INCREMENT,
            `Mode`         VARCHAR(50)  NOT NULL DEFAULT 'Orders',
            `EntityId`     INT          NOT NULL DEFAULT 0,
            `ValuesJson`   LONGTEXT     NOT NULL,
            `SortOrder`    INT          NOT NULL DEFAULT 0,
            `LastModified` DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                           ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- Users / AuditEntries — accounts and the append-only interaction log ───
        CREATE TABLE IF NOT EXISTS `Users` (
            `Id`                 INT          NOT NULL AUTO_INCREMENT,
            `Email`              VARCHAR(255) NOT NULL,
            `FirstName`          LONGTEXT     NOT NULL,
            `LastName`           LONGTEXT     NOT NULL,
            `PasswordHash`       LONGTEXT     NOT NULL,
            `Role`               VARCHAR(50)  NOT NULL DEFAULT 'User',
            `IsActive`           TINYINT(1)   NOT NULL DEFAULT 1,
            `MustChangePassword` TINYINT(1)   NOT NULL DEFAULT 0,
            `LastLoginUtc`       DATETIME     NULL,
            `LastModified`       DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                                 ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        CREATE TABLE IF NOT EXISTS `AuditEntries` (
            `Id`           INT          NOT NULL AUTO_INCREMENT,
            `TimestampUtc` DATETIME     NOT NULL,
            `UserEmail`    VARCHAR(255) NOT NULL DEFAULT '',
            `UserName`     VARCHAR(255) NOT NULL DEFAULT '',
            `Action`       VARCHAR(100) NOT NULL DEFAULT '',
            `EntityType`   VARCHAR(100) NOT NULL DEFAULT '',
            `EntityId`     INT          NULL,
            `Details`      TEXT         NOT NULL,
            `MachineName`  VARCHAR(255) NOT NULL DEFAULT '',
            `LastModified` DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                           ON UPDATE CURRENT_TIMESTAMP(3),
            PRIMARY KEY (`Id`)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

        -- LastModifiedBy — who last wrote the row (Orders/Romps/Sites only)
        ALTER TABLE `Orders` ADD COLUMN IF NOT EXISTS `LastModifiedBy` VARCHAR(255) NOT NULL DEFAULT '';
        ALTER TABLE `Romps`  ADD COLUMN IF NOT EXISTS `LastModifiedBy` VARCHAR(255) NOT NULL DEFAULT '';
        ALTER TABLE `Sites`  ADD COLUMN IF NOT EXISTS `LastModifiedBy` VARCHAR(255) NOT NULL DEFAULT '';

        -- IsClosed — explicit close flag for a Site (see Site.cs; needed for zero-ROMP sites)
        ALTER TABLE `Sites`  ADD COLUMN IF NOT EXISTS `IsClosed` TINYINT(1) NOT NULL DEFAULT 0;

        -- ================================================================
        -- Done. Connect the app using:
        --   Server=YOUR_HOST; Port=3306; Database=pmdashboard;
        --   User=YOUR_USER; Password=YOUR_PASSWORD; AllowPublicKeyRetrieval=true;
        -- The app will seed built-in status / equipment / rule data on first launch.
        -- ================================================================
        """;
}
