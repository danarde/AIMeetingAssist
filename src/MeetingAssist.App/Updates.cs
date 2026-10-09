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

    private readonly UpdateManager _manager = new(new GithubSource(Repository, null, false));

    public void DownloadInBackground()
    {
        if (!_manager.IsInstalled) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var update = await _manager.CheckForUpdatesAsync();
                if (update is null) return;

                await _manager.DownloadUpdatesAsync(update);
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

    /// <summary>Call on quit. The updater waits for this process to exit, then installs.</summary>
    public void ApplyOnExit()
    {
        try
        {
            if (!_manager.IsInstalled || _manager.UpdatePendingRestart is not { } pending) return;

            Log.Information("Installing update {Version} on exit", pending.Version);
            _manager.WaitExitThenApplyUpdates(pending, silent: true, restart: false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not start the update on exit");
        }
    }
}
