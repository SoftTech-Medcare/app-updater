using Avalonia;
using FluentHttpClient;
using System;
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

            var server = Settings.Default.UpdateServer.TrimEnd('/');
            var appName = Settings.Default.AppName;
            var includePreRelease = Settings.Default.EnablePreReleaseVersions;
            var includeSelfUpdate = Settings.Default.IncludeSelfUpdateInCheck;
            var (version, lastMod, checksum) = GetCurrentVersionInfo();
            var probe = new VersionProbe(version, lastMod, checksum);

            var outcome = await RunChecksAsync(server, appName, probe, includePreRelease, includeSelfUpdate);

            SelfUpdateOnlyMode = SelfUpdateAdvertised && !outcome.AppNeedsUpdate;
            return !outcome.AppNeedsUpdate && !SelfUpdateAdvertised;
        }

        private async Task<UpdateCheckOutcome> RunChecksAsync(
            string server,
            string appName,
            VersionProbe probe,
            bool includePreRelease,
            bool includeSelfUpdate)
        {
            var outcome = new UpdateCheckOutcome();

            if (!string.IsNullOrEmpty(probe.Version))
            {
                await TryManifestCheckAsync(server, appName, probe, includePreRelease, includeSelfUpdate, outcome);
            }

            if (!outcome.AppNeedsUpdate && !outcome.SkipLegacyCheck)
            {
                var legacyOk = await TryLegacyCheckAsync(server, appName, probe, includePreRelease, includeSelfUpdate, outcome);
                if (!legacyOk)
                {
                    return outcome;
                }
            }

            if (includeSelfUpdate && !SelfUpdateAdvertised && string.IsNullOrEmpty(probe.Version))
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
            bool includeSelfUpdate,
            UpdateCheckOutcome outcome)
        {
            try
            {
                using var client = CreateUpdateHttpClient();
                var url = BuildUpdateUrl(server, appName, "check-upgrades", includePreRelease);
                using var response = await client.PostAsJsonAsync(url, CreateCheckBody(probe, includeSelfUpdate));

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var info = await response.Content.ReadFromJsonAsync<UpgradeInfoWrapper>();
                    if (info?.RequiresDownload == true)
                    {
                        UseManifestSystem = true;
                        CurrentUpgradeInfo = info;
                        ApplySelfUpdateFromResponse(info);
                        outcome.AppNeedsUpdate = HasApplicationUpgrade(info.Upgrades);
                    }
                    return;
                }

                if (response.StatusCode == HttpStatusCode.NoContent)
                {
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
            bool includeSelfUpdate,
            UpdateCheckOutcome outcome)
        {
            var url = BuildUpdateUrl(server, appName, "check", includePreRelease);
            var statusCode = 0;

            try
            {
                using var client = CreateUpdateHttpClient();
                var upToDate = await PostLegacyCheckAsync(
                    client, url, probe, includeSelfUpdate, onHttpError: code => statusCode = code);

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
                outcome.AppNeedsUpdate = includeSelfUpdate
                    ? await ResolveAppNeedsUpdateWhenLegacyReportsUpdateAsync(client, url, probe)
                    : true;
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
            HttpClient client,
            string legacyCheckUrl,
            VersionProbe probe)
        {
            var appOnlyUpToDate = await PostLegacyCheckAsync(client, legacyCheckUrl, probe, includeSelfUpdate: false);
            if (appOnlyUpToDate != true)
            {
                return true;
            }

            await TrySelfUpdateFallbackAsync(Settings.Default.UpdateServer.TrimEnd('/'), Settings.Default.EnablePreReleaseVersions);
            return false;
        }

        private static async Task<bool?> PostLegacyCheckAsync(
            HttpClient client,
            string url,
            VersionProbe probe,
            bool includeSelfUpdate,
            Action<int>? onHttpError = null)
        {
            return await client.UsingRoute(url)
                .WithJsonContent(CreateCheckBody(probe, includeSelfUpdate))
                .WithRequestTimeout(5)
                .PostAsync()
                .OnFailureAsync(async res =>
                {
                    onHttpError?.Invoke((int)res.StatusCode);
                    Logger.LogError($"error calling server ({(int)res.StatusCode}): {await res.GetResponseStringAsync()}");
                    await App.ShowAlert($"Error on calling server ({(int)res.StatusCode}). Please contact administrator.");
                }, false)
                .DeserializeJsonAsync<bool>();
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
                SelfUpdateTargetVersion = info.SelfUpdate.TargetVersion;
                SelfUpdateTargetFileName = info.SelfUpdate.PackageFile;
                return;
            }

            var selfUpgrade = info.Upgrades?.FirstOrDefault(u => IsSelfUpdateUpgradeId(u.Id));
            if (selfUpgrade != null)
            {
                SelfUpdateAdvertised = true;
                SelfUpdateTargetVersion = ParseSelfUpdateVersionFromId(selfUpgrade.Id) ?? selfUpgrade.Name;
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

        private static CheckUpgradeRequest CreateCheckBody(VersionProbe probe, bool includeSelfUpdate) =>
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
                var info = await GetLatestVersionInfoAsync(server, GetUpdaterPackageAppName(), includePreRelease);
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
                SelfUpdateTargetVersion = chosen.Version.Trim();
                SelfUpdateTargetFileName = chosen.File;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Self-update fallback check failed: {ex.Message}");
            }
        }

        /// <summary>One-line summary for the update-available dialog.</summary>
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
