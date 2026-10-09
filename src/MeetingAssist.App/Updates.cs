using Serilog;
using Velopack;
using Velopack.Sources;

namespace MeetingAssist.App;

/// <summary>
/// Updates from the project's GitHub Releases, without ever showing anything: a new version is
/// downloaded in the background after start and installed when the app quits, so the next start
/// runs it. Nothing appears mid-meeting, where any window could end up in a screen share.
///
/// A build run from the source tree is not installed, and does none of this.
/// </summary>
public sealed class Updates
{
    private const string Repository = "https://github.com/danarde/AIMeetingAssist";

    private UpdateManager? _manager;

    public void DownloadInBackground()
    {
        if (Installed() is not { } manager) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var update = await manager.CheckForUpdatesAsync();
                if (update is null) return;

                await manager.DownloadUpdatesAsync(update);
                Log.Information("Update {Version} downloaded; it installs when the app quits",
                    update.TargetFullRelease.Version);
            }
            catch (Exception ex)
            {
                // Offline, rate-limited by GitHub, a release still uploading: try again next start.
                Log.Warning(ex, "Update check failed");
            }
        });
    }

    /// <summary>
    /// The update manager, or null when this copy is not installed. Created on first use, and
    /// never in a constructor: it throws in a process that did not start through
    /// <see cref="Program.Main"/>, such as a test or a tool hosting the app's windows.
    /// </summary>
    private UpdateManager? Installed()
    {
        try
        {
            _manager ??= new UpdateManager(new GithubSource(Repository, null, false));
            return _manager.IsInstalled ? _manager : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Updates are off: Velopack is not available in this process");
            return null;
        }
    }

    /// <summary>Call on quit. The updater waits for this process to exit, then installs.</summary>
    public void ApplyOnExit()
    {
        try
        {
            if (Installed() is not { UpdatePendingRestart: { } pending } manager) return;

            Log.Information("Installing update {Version} on exit", pending.Version);
            manager.WaitExitThenApplyUpdates(pending, silent: true, restart: false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not start the update on exit");
        }
    }
}
