using Avalonia;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Updater.Properties;
using Updater.Utils;

namespace Updater.Services
{
    public partial class UpdateService
    {
        private const string UpdaterServerAppName = "Updater";

        private sealed class UpdateCheckOutcome
        {
            public bool AppNeedsUpdate { get; set; }
            public bool SkipLegacyCheck { get; set; }
        }

        private readonly record struct VersionProbe(string? Version, DateTimeOffset? Modified, string? Checksum);

        public async Task<bool> CheckUpdate()
        {
            if (!await ValidateCheckConfigurationAsync())
            {
                return false;
            }

            ResetCheckState();
            CleanupStaleDownloadPackages();

            var server = Settings.Default.UpdateServer.TrimEnd('/');
            var appName = Settings.Default.AppName;
            var includePreRelease = Settings.Default.EnablePreReleaseVersions;
            var (version, lastMod, checksum) = GetCurrentVersionInfo();
            var probe = new VersionProbe(version, lastMod, checksum);

            var outcome = await RunChecksAsync(server, appName, probe, includePreRelease);

            SelfUpdateOnlyMode = SelfUpdateAdvertised && !outcome.AppNeedsUpdate;

            // Stdout / UI contract (HemoBox, HemoCheckIn): true only when app AND updater are current.
            // false → UpdateAvailableWindow (or force download); true → "Already Up-To-Date" + exit.
            return IsFullyUpToDate(outcome);
        }

        private static bool IsFullyUpToDate(UpdateCheckOutcome outcome) =>
            !outcome.AppNeedsUpdate && !SelfUpdateAdvertised;

        private async Task<UpdateCheckOutcome> RunChecksAsync(
            string server,
            string appName,
            VersionProbe probe,
            bool includePreRelease)
        {
            var outcome = new UpdateCheckOutcome();

            if (!string.IsNullOrEmpty(probe.Version))
            {
                await TryManifestCheckAsync(server, appName, probe, includePreRelease, outcome);
            }

            if (!outcome.AppNeedsUpdate && !outcome.SkipLegacyCheck)
            {
                var legacyOk = await TryLegacyCheckAsync(server, appName, probe, includePreRelease, outcome);
                if (!legacyOk)
                {
                    return outcome;
                }
            }

            if (!SelfUpdateAdvertised && string.IsNullOrEmpty(probe.Version))
            {
                await TrySelfUpdateFallbackAsync(server, includePreRelease);
            }

            return outcome;
        }

        private async Task TryManifestCheckAsync(
            string server,
            string appName,
            VersionProbe probe,
            bool includePreRelease,
            UpdateCheckOutcome outcome)
        {
            try
            {
                using var client = CreateCheckHttpClient();
                var url = BuildUpdateUrl(server, appName, "check-upgrades", includePreRelease);
                using var response = await client.PostAsJsonAsync(url, CreateCheckBody(probe));

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var info = await response.Content.ReadFromJsonAsync<UpgradeInfoWrapper>();
                    if (info?.RequiresDownload == true)
                    {
                        UseManifestSystem = true;
                        CurrentUpgradeInfo = info;
                        ApplySelfUpdateFromResponse(info);
                        outcome.AppNeedsUpdate = HasApplicationUpgrade(info.Upgrades);
                        outcome.SkipLegacyCheck = true;
                    }
                    else
                    {
                        ClearSelfUpdateState();
                        outcome.SkipLegacyCheck = true;
                    }

                    return;
                }

                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    ClearSelfUpdateState();
                    outcome.SkipLegacyCheck = true;
                    return;
                }

                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    Logger.LogError($"Check upgrades failed with {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Check upgrades exception: {ex.Message}");
            }
        }

        /// <returns>false when the check could not complete (caller should abort).</returns>
        private async Task<bool> TryLegacyCheckAsync(
            string server,
            string appName,
            VersionProbe probe,
            bool includePreRelease,
            UpdateCheckOutcome outcome)
        {
            var url = BuildUpdateUrl(server, appName, "check", includePreRelease);
            var statusCode = 0;

            try
            {
                var upToDate = await PostLegacyCheckAsync(url, probe, onHttpError: code => statusCode = code);

                if (upToDate == null)
                {
                    return false;
                }

                if (upToDate == true)
                {
                    var currentFile = GetCurrentFile();
                    if (!string.IsNullOrWhiteSpace(currentFile))
                    {
                        AlreadyDownloaded = true;
                    }
                    return true;
                }

                UseManifestSystem = false;
                CurrentUpgradeInfo = null;
                outcome.AppNeedsUpdate = await ResolveAppNeedsUpdateWhenLegacyReportsUpdateAsync(url, probe);
                return true;
            }
            catch (HttpRequestException)
            {
                return false;
            }
            catch (Exception ex) when (ex is TaskCanceledException or TimeoutException)
            {
                await App.ShowAlert($"Error on calling server ({statusCode}). Please contact administrator.");
                return false;
            }
        }

        private static async Task<bool> ResolveAppNeedsUpdateWhenLegacyReportsUpdateAsync(
            string legacyCheckUrl,
            VersionProbe probe)
        {
            var appOnlyUpToDate = await PostLegacyCheckAsync(legacyCheckUrl, probe, includeSelfUpdateInRequest: false);
            if (appOnlyUpToDate != true)
            {
                return true;
            }

            await TrySelfUpdateFallbackAsync(Settings.Default.UpdateServer.TrimEnd('/'), Settings.Default.EnablePreReleaseVersions);
            return false;
        }

        private static async Task<bool?> PostLegacyCheckAsync(
            string url,
            VersionProbe probe,
            bool? includeSelfUpdateInRequest = null,
            Action<int>? onHttpError = null)
        {
            using var client = CreateCheckHttpClient();
            using var response = await client.PostAsJsonAsync(url, CreateCheckBody(probe, includeSelfUpdateInRequest));

            if (!response.IsSuccessStatusCode)
            {
                onHttpError?.Invoke((int)response.StatusCode);
                Logger.LogError(
                    $"error calling server ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
                await App.ShowAlert(
                    $"Error on calling server ({(int)response.StatusCode}). Please contact administrator.");
                return null;
            }

            return await response.Content.ReadFromJsonAsync<bool>();
        }

        private static async Task<bool> ValidateCheckConfigurationAsync()
        {
            var server = Settings.Default.UpdateServer;
            var appName = Settings.Default.AppName;
            var appFolder = Settings.Default.ClientAppPath;

            if (string.IsNullOrWhiteSpace(server))
            {
                await App.ShowAlert("Please config server URI first.");
                return false;
            }

            if (!Uri.TryCreate(server, UriKind.Absolute, out _))
            {
                await App.ShowAlert("The update server is not valid URI.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(appName))
            {
                await App.ShowAlert("Please config app name first.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(appFolder))
            {
                await App.ShowAlert("Please config the app folder path first.");
                return false;
            }

            return true;
        }

        private static void ResetCheckState()
        {
            UseManifestSystem = false;
            CurrentUpgradeInfo = null;
            SelfUpdateOnlyMode = false;
            AlreadyDownloaded = false;
            ClearSelfUpdateState();
        }

        private static void ClearSelfUpdateState()
        {
            SelfUpdateAdvertised = false;
            SelfUpdateTargetVersion = null;
            SelfUpdateTargetFileName = null;
        }

        private static void ApplySelfUpdateFromResponse(UpgradeInfoWrapper info)
        {
            if (info.SelfUpdate?.Available == true)
            {
                SelfUpdateAdvertised = true;
                SelfUpdateTargetVersion = PathHelper.SanitizePathSegment(info.SelfUpdate.TargetVersion);
                SelfUpdateTargetFileName = string.IsNullOrWhiteSpace(info.SelfUpdate.PackageFile)
                    ? null
                    : PathHelper.SanitizeDownloadFileName(info.SelfUpdate.PackageFile);
                return;
            }

            var selfUpgrade = info.Upgrades?.FirstOrDefault(u => IsSelfUpdateUpgradeId(u.Id));
            if (selfUpgrade != null)
            {
                SelfUpdateAdvertised = true;
                SelfUpdateTargetVersion = PathHelper.SanitizePathSegment(
                    ParseSelfUpdateVersionFromId(selfUpgrade.Id) ?? info.TargetVersion);
                SelfUpdateTargetFileName = null;
                return;
            }

            ClearSelfUpdateState();
        }

        private static bool HasApplicationUpgrade(System.Collections.Generic.List<UpgradeSummary>? upgrades)
        {
            return upgrades?.Any(u => !IsSelfUpdateUpgradeId(u.Id)) == true;
        }

        public static bool IsSelfUpdateUpgradeId(string? upgradeId) =>
            !string.IsNullOrWhiteSpace(upgradeId)
            && upgradeId.StartsWith("updater-self-update", StringComparison.OrdinalIgnoreCase);

        private static string? ParseSelfUpdateVersionFromId(string? upgradeId)
        {
            const string prefix = "updater-self-update-";
            return upgradeId?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
                ? upgradeId[prefix.Length..]
                : null;
        }

        private static CheckUpgradeRequest CreateCheckBody(VersionProbe probe, bool? includeSelfUpdate = null) =>
            new()
            {
                Version = probe.Version,
                Modified = probe.Modified,
                Checksum = probe.Checksum,
                IncludeSelfUpdate = includeSelfUpdate
            };

        private static string BuildUpdateUrl(string server, string appName, string action, bool includePreRelease)
        {
            var prereleaseQuery = action == "check-upgrades"
                ? $"includePrerelease={includePreRelease}"
                : $"includePreRelease={includePreRelease}";
            return $"{server}/update/{Uri.EscapeDataString(appName)}/{action}?{prereleaseQuery}";
        }

        private static async Task TrySelfUpdateFallbackAsync(string server, bool includePreRelease)
        {
            try
            {
                var info = await GetLatestVersionInfoAsync(server, UpdaterServerAppName, includePreRelease);
                var chosen = includePreRelease && info?.PreRelease != null ? info.PreRelease : info?.Stable;
                if (chosen == null || string.IsNullOrWhiteSpace(chosen.Version))
                {
                    return;
                }

                if (!Version.TryParse(chosen.Version.Trim(), out var remote))
                {
                    return;
                }

                if (!Version.TryParse(GetUpdaterVersion(), out var local))
                {
                    local = new Version(0, 0, 0);
                }

                if (remote <= local)
                {
                    return;
                }

                SelfUpdateAdvertised = true;
                SelfUpdateTargetVersion = PathHelper.SanitizePathSegment(chosen.Version.Trim());
                SelfUpdateTargetFileName = string.IsNullOrWhiteSpace(chosen.File)
                    ? null
                    : PathHelper.SanitizeDownloadFileName(chosen.File);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Self-update fallback check failed: {ex.Message}");
            }
        }

        /// <summary>One-line summary for the update-available dialog.</summary>
        /// <summary>Staging folder for a pending updater self-update (under the running updater install root).</summary>
        public static string GetSelfUpdatePendingDirectory(string? version = null)
        {
            var resolved = PathHelper.SanitizePathSegment(
                version ?? SelfUpdateTargetVersion ?? GetUpdaterVersion());
            return Path.Combine(
                GetUpdaterInstallDirectory(),
                "pending-update",
                $"updater-{resolved}");
        }

        public static string GetSelfUpdateSummaryLine()
        {
            if (!SelfUpdateAdvertised)
            {
                return string.Empty;
            }

            var line = $"App updater download: {GetUpdaterVersion()} → {SelfUpdateTargetVersion ?? "Unknown"}";
            if (!string.IsNullOrWhiteSpace(SelfUpdateTargetFileName))
            {
                line += $" ({SelfUpdateTargetFileName})";
            }

            return line;
        }
    }
}
