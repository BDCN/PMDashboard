using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Data.Sqlite;
using PMDashboard.Services;

namespace PMDashboard.Dialogs;

/// <summary>
/// "Connect to network database" — open to EVERY user, signed in or not (sign-in screen,
/// first-run screen, sidebar). Pointing a computer at the shared file is not an admin decision:
/// a new install has no account yet except whatever it creates locally, and the Configuration
/// page needs a Super Admin, so that path would force a throwaway local admin on every new PC.
///
/// Only an existing SAT database can be chosen (never creates one — "Create Network Database"
/// stays on the Super-Admin Configuration page). Switching restarts SAT, since the database is
/// chosen once at startup before anything opens it.
/// </summary>
public static class DatabaseConnectFlow
{
    /// <summary>Short description of where this computer's data is right now.</summary>
    public static string Describe() =>
        DatabaseConfig.IsTravelMode ? "Travel Mode (offline copy of the network database)"
        : NetworkDb.IsShared        ? "Shared network database (synced to this computer)"
        :                             "This computer only (not shared)";

    /// <summary>
    /// Lets the user pick the shared database file, checks it, saves it and starts a new SAT.
    /// True → a new SAT has been started; the caller must close (dialogs: DialogResult = false,
    /// main window: Application.Current.Shutdown()).
    /// </summary>
    public static bool ConnectToNetwork(Window? owner)
    {
        if (DatabaseConfig.IsTravelMode)
        {
            Box(owner, "You're in Travel Mode. Use \"Sync After Travel\" first — it brings your offline " +
                       "changes back into the network database and reconnects this computer.",
                MessageBoxImage.Information);
            return false;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title           = "Select the shared SAT database file on the network",
            Filter          = "SAT database (*.db)|*.db|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        var previous = DatabaseConfig.NetworkDbPath;
        if (!string.IsNullOrWhiteSpace(previous))
        {
            try
            {
                var dir = Path.GetDirectoryName(previous);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    dlg.InitialDirectory = dir;
                    dlg.FileName         = Path.GetFileName(previous);
                }
            }
            catch { /* share offline — just open the dialog in its default folder */ }
        }
        if ((owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog()) != true) return false;
        var path = dlg.FileName;

        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(DatabaseConfig.LocalDbPath),
                          StringComparison.OrdinalIgnoreCase))
        {
            Box(owner, "That's this computer's own database file, not the shared one. Pick the SAT database " +
                       "on the network share.", MessageBoxImage.Warning);
            return false;
        }
        if (NetworkDb.IsShared && string.Equals(path, DatabaseConfig.NetworkDbPath, StringComparison.OrdinalIgnoreCase))
        {
            Box(owner, "This computer is already using that database.", MessageBoxImage.Information);
            return false;
        }
        if (NetworkDb.CheckReachable(path) is { } problem)
        {
            Box(owner, problem, MessageBoxImage.Warning);
            return false;
        }
        if (NotASatDatabase(path) is { } why)
        {
            Box(owner, why, MessageBoxImage.Warning);
            return false;
        }

        if (NetworkDb.CheckWritable(path) is { } readOnly &&
            Ask(owner, readOnly + "\n\nConnect anyway (view only until the permission is fixed)?") != MessageBoxResult.Yes)
            return false;

        var onShare = IsOnNetworkShare(path);
        if (Ask(owner,
                $"Connect this computer to the shared database?\n\n{path}\n\n" +
                (onShare ? "" : "⚠ This file is on a drive of this computer, not a network share — other people " +
                                "won't be able to reach it.\n\n") +
                "SAT will restart, then you sign in with your account on the shared database. " +
                "This computer's own local data is kept but no longer used.") != MessageBoxResult.Yes)
            return false;

        // Moving to a different shared file: send this computer's unsent changes to the current one
        // first — the working copy is rebuilt from the new file at the restart.
        if (NetworkSync.IsEnabled)
        {
            var last = NetworkSync.SyncNow();
            if (!last.Ok && Ask(owner,
                    "Some changes made on this computer couldn't be sent to the current shared database:\n" +
                    $"{last.Error}\n\nSwitch anyway? Those changes won't reach the current shared database.") != MessageBoxResult.Yes)
                return false;
        }

        try { NetworkDb.EnsureRollbackJournal(path); } catch { /* re-checked at every startup */ }
        DatabaseConfig.ApplyNetwork(path);
        return StartNewInstance(owner);
    }

    /// <summary>Null when <paramref name="path"/> holds a SAT database, otherwise why not.</summary>
    private static string? NotASatDatabase(string path)
    {
        try
        {
            using var conn = new SqliteConnection(
                NetworkDb.ConnectionString(path, SqliteOpenMode.ReadOnly) + ";Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Users','Sites','PMs')";
            if (Convert.ToInt32(cmd.ExecuteScalar()) < 3)
                return $"That file isn't a SAT database:\n{path}\n\n" +
                       "Ask your SAT administrator for the location of the shared database file.";
            return null;
        }
        catch (Exception ex)
        {
            return $"That file can't be read as a database:\n{path}\n\n{ex.Message}";
        }
    }

    private static bool IsOnNetworkShare(string path)
    {
        if (path.StartsWith(@"\\")) return true;
        try { return new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network; }
        catch { return true; } // can't tell — don't nag
    }

    private static bool StartNewInstance(Window? owner)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Box(owner, "The new database setting is saved, but SAT couldn't restart itself:\n" + ex.Message +
                       "\n\nClose SAT and open it again.", MessageBoxImage.Information);
            return true; // setting is saved either way — close this instance
        }
    }

    private static void Box(Window? owner, string text, MessageBoxImage icon)
    {
        const string title = "SAT — Network Database";
        if (owner != null) MessageBox.Show(owner, text, title, MessageBoxButton.OK, icon);
        else               MessageBox.Show(text, title, MessageBoxButton.OK, icon);
    }

    private static MessageBoxResult Ask(Window? owner, string text)
    {
        const string title = "SAT — Network Database";
        return owner != null
            ? MessageBox.Show(owner, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
    }
}
