using Avalonia.Threading;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Updater.Properties;
using Updater.Services;
using Updater.Utils;
using UpdaterLib;

namespace Updater.ViewModels
{
    public class DownloadProgressViewModel : ViewModelBase
    {
        private float percent;
        private float archiveExtractPercent;
        private string stepTitle = "";
        private string detailText = "";
        private bool isFailed;
        private bool downloaded;

        public float Percent
        {
            get => percent;
            set
            {
                this.RaiseAndSetIfChanged(ref percent, value);
                this.RaisePropertyChanged(nameof(PercentText));
            }
        }

        public string PercentText => $"{Math.Round(Percent)}%";

        /// <summary>Current pipeline step (static while that step runs).</summary>
        public string StepTitle { get => stepTitle; set => this.RaiseAndSetIfChanged(ref stepTitle, value); }

        /// <summary>Sub-label: current file, byte counts, etc.</summary>
        public string DetailText
        {
            get => detailText;
            set
            {
                this.RaiseAndSetIfChanged(ref detailText, value);
                this.RaisePropertyChanged(nameof(HasDetailText));
            }
        }

        public bool HasDetailText => !string.IsNullOrWhiteSpace(DetailText);

        public bool IsFailed { get => isFailed; set => this.RaiseAndSetIfChanged(ref isFailed, value); }
        public bool IsDownloaded { get => downloaded; set => this.RaiseAndSetIfChanged(ref downloaded, value); }
        public ReactiveCommand<Unit, UpgradeStepResult> Retry { get; private set; }
        public bool AutoReboot => Settings.Default.AutoReboot;

        public void MarkUpgradeComplete()
        {
            ApplyProgress(
                UpdateService.SelfUpdateOnlyMode
                    ? "Updater update staged"
                    : "Update completed",
                UpdateService.SelfUpdateOnlyMode
                    ? "Restarting updater..."
                    : null,
                100f);
        }

        public void MarkPipelineFailed(string stage, string? detail = null)
        {
            var message = string.IsNullOrWhiteSpace(detail)
                ? $"{stage} failed. Please try again or contact administrator."
                : $"{stage} failed: {detail}";

            void Apply()
            {
                IsFailed = true;
                StepTitle = message;
                DetailText = "";
                Percent = 0f;
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                Apply();
            }
            else
            {
                Dispatcher.UIThread.Post(Apply);
            }
        }

        public DownloadProgressViewModel()
        {
            Retry = ReactiveCommand.CreateFromObservable(StartDownload);
        }

        public IObservable<UpgradeStepResult> StartDownload()
        {
            IsFailed = false;
            IsDownloaded = false;
            archiveExtractPercent = 0f;
            ApplyProgress(DownloadStepTitle, null, 0f);
            return Observable.StartAsync(async () =>
            {
                try
                {
                    var update = new UpdateService();

                    string? fromVersion = null;
                    string? toVersion = null;
                    long? packageSize = null;

                    var lastVersion = Settings.Default.LastVersion;
                    if (lastVersion != null)
                    {
                        fromVersion = lastVersion.Version;
                    }

                    if (UpdateService.SelfUpdateOnlyMode)
                    {
                        fromVersion = UpdateService.GetUpdaterVersion();
                        toVersion = UpdateService.SelfUpdateTargetVersion;
                    }
                    else if (UpdateService.UseManifestSystem && UpdateService.CurrentUpgradeInfo != null)
                    {
                        fromVersion = UpdateService.CurrentUpgradeInfo.CurrentVersion;
                        toVersion = UpdateService.CurrentUpgradeInfo.TargetVersion;
                        packageSize = UpdateService.CurrentUpgradeInfo.PackageSize;
                    }

                    Logger.StartUpgradeSession(fromVersion, toVersion, packageSize);
                    Logger.LogUpgradeEvent(new UpgradeLog
                    {
                        Timestamp = DateTimeOffset.Now,
                        Status = UpgradeStatus.Started,
                        Stage = UpgradeStage.Download,
                        Message = "Starting download"
                    });

                    var filePath = await update.Download(OnDownloadProgress);

                    if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                    {
                        const string msg = "Download did not produce a package file.";
                        Logger.EndUpgradeSession(UpgradeStatus.Failed, msg);
                        MarkPipelineFailed("Download", msg);
                        return UpgradeStepResult.Failed(msg);
                    }

                    var fileInfo = new FileInfo(filePath);
                    packageSize = fileInfo.Length;
                    if (string.IsNullOrEmpty(toVersion))
                    {
                        toVersion = UpdateService.GetVersionFromFileName(fileInfo.Name);
                    }

                    Logger.LogUpgradeEvent(new UpgradeLog
                    {
                        Timestamp = DateTimeOffset.Now,
                        Status = UpgradeStatus.Completed,
                        Stage = UpgradeStage.Download,
                        Message = $"Download completed: {filePath}",
                        Details = new Dictionary<string, object> { { "filePath", filePath }, { "size", packageSize } }
                    });

                    IsDownloaded = true;
                    return UpgradeStepResult.Succeeded(filePath);
                }
                catch (Exception e)
                {
                    Logger.LogUpgradeEvent(new UpgradeLog
                    {
                        Timestamp = DateTimeOffset.Now,
                        Status = UpgradeStatus.Failed,
                        Stage = UpgradeStage.Download,
                        Message = "Download failed",
                        Error = e.ToString()
                    });
                    Logger.EndUpgradeSession(UpgradeStatus.Failed, e.Message);
                    Logger.LogError("download failed", e);
                    MarkPipelineFailed("Download", e.Message);
                    return UpgradeStepResult.Failed(e.Message);
                }
            });
        }

        public IObservable<UpgradeStepResult> StartExtract(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                const string msg = "Extract called without a package path.";
                MarkPipelineFailed("Installation", msg);
                return Observable.Return(UpgradeStepResult.Failed(msg));
            }

            return Observable.StartAsync(async () =>
            {
                try
                {
                    archiveExtractPercent = 0f;
                    ApplyProgress(ExtractStepTitle, null, 0f);

                    Logger.LogUpgradeEvent(new UpgradeLog
                    {
                        Timestamp = DateTimeOffset.Now,
                        Status = UpgradeStatus.InProgress,
                        Stage = UpgradeStage.Extract,
                        Message = "Starting extraction and installation"
                    });

                    var destination = UpdateService.SelfUpdateOnlyMode
                        ? UpdateService.GetSelfUpdatePendingDirectory()
                        : Settings.Default.ClientAppPath;

                    if (UpdateService.SelfUpdateOnlyMode)
                    {
                        Directory.CreateDirectory(destination);
                    }

                    PathHelper.ValidateWritablePath(sourcePath, nameof(sourcePath));
                    PathHelper.ValidateWritablePath(destination, nameof(destination));
                    Logger.LogUpgradeOutput($"Extract source: {sourcePath}");
                    Logger.LogUpgradeOutput($"Extract destination: {destination}");

                    var update = new UpdateService();
                    await update.ExtractTarballFile(sourcePath, destination, OnExtractProgress, OnInstallProgress);

                    if (!UpdateService.SelfUpdateOnlyMode && UpdateService.SelfUpdateAdvertised)
                    {
                        await update.StageSelfUpdateIfNeededAsync(OnExtractProgress, OnInstallProgress);
                    }

                    Logger.LogUpgradeEvent(new UpgradeLog
                    {
                        Timestamp = DateTimeOffset.Now,
                        Status = UpgradeStatus.Completed,
                        Stage = UpgradeStage.Extract,
                        Message = "Extraction and installation completed"
                    });

                    return UpgradeStepResult.Succeeded(sourcePath);
                }
                catch (Exception e)
                {
                    Logger.LogUpgradeEvent(new UpgradeLog
                    {
                        Timestamp = DateTimeOffset.Now,
                        Status = UpgradeStatus.Failed,
                        Stage = UpgradeStage.Extract,
                        Message = "Extraction failed",
                        Error = e.ToString()
                    });
                    Logger.EndUpgradeSession(UpgradeStatus.Failed, e.Message);
                    Logger.LogError("extract failed", e);
                    MarkPipelineFailed("Installation", e.Message);
                    return UpgradeStepResult.Failed(e.Message);
                }
            });
        }

        public IObservable<UpgradeStepResult> CleanAndFinish(UpgradeStepResult extractResult)
        {
            if (extractResult == null || !extractResult.Success || string.IsNullOrWhiteSpace(extractResult.PackagePath))
            {
                const string msg = "Cleanup called without a successful extract result.";
                MarkPipelineFailed("Cleanup", msg);
                return Observable.Return(UpgradeStepResult.Failed(msg));
            }

            var sourcePath = extractResult.PackagePath;
            var intermediateArtifact = extractResult.IntermediateArtifactPath;

            return Observable.Start(() =>
            {
                try
                {
                    ApplyProgress(FinishingStepTitle, "Removing temporary files", 100f);

                    Logger.LogUpgradeEvent(new UpgradeLog
                    {
                        Timestamp = DateTimeOffset.Now,
                        Status = UpgradeStatus.InProgress,
                        Stage = UpgradeStage.Cleanup,
                        Message = "Cleaning up temporary files"
                    });

                    if (!string.IsNullOrEmpty(intermediateArtifact) && File.Exists(intermediateArtifact))
                    {
                        File.Delete(intermediateArtifact);
                    }

                    if (!File.Exists(sourcePath))
                    {
                        throw new FileNotFoundException($"Package file not found for cleanup: {sourcePath}");
                    }

                    var info = new FileInfo(sourcePath);

                    string? appVersionToPersist = null;
                    if (!UpdateService.SelfUpdateOnlyMode)
                    {
                        appVersionToPersist = ResolvePostInstallRecordedAppVersion(info, out var versionResolveFailureReason);
                        if (string.IsNullOrWhiteSpace(appVersionToPersist))
                        {
                            var detail = string.IsNullOrWhiteSpace(versionResolveFailureReason)
                                ? "No diagnostic detail available."
                                : versionResolveFailureReason;
                            Logger.LogError(
                                $"Upgrade finished but installed version could not be resolved; leaving LastVersion unchanged. {detail}");
                            Logger.LogUpgradeOutput($"Cleanup: LastVersion not updated — {detail}");
                        }
                        else
                        {
                            Settings.Default.LastVersion = new UpdateInfo
                            {
                                Modified = info.LastWriteTimeUtc,
                                Version = appVersionToPersist
                            };
                            Settings.Default.Save();
                        }
                    }

                    File.Delete(sourcePath);
                    UpdateService.CleanupStaleDownloadPackages();

                    Logger.LogUpgradeEvent(new UpgradeLog
                    {
                        Timestamp = DateTimeOffset.Now,
                        Status = UpgradeStatus.Completed,
                        Stage = UpgradeStage.Cleanup,
                        Message = UpdateService.SelfUpdateOnlyMode
                            ? $"Updater self-update staged successfully. Target: {UpdateService.SelfUpdateTargetVersion ?? "(unknown)"}"
                            : string.IsNullOrWhiteSpace(appVersionToPersist)
                                ? "Upgrade completed successfully. Installed version was not persisted (see prior log)."
                                : $"Upgrade completed successfully. New version: {appVersionToPersist}"
                    });

                    Logger.EndUpgradeSession(UpgradeStatus.Completed);
                    return UpgradeStepResult.Succeeded(sourcePath);
                }
                catch (Exception e)
                {
                    Logger.LogUpgradeEvent(new UpgradeLog
                    {
                        Timestamp = DateTimeOffset.Now,
                        Status = UpgradeStatus.Failed,
                        Stage = UpgradeStage.Cleanup,
                        Message = "Cleanup failed",
                        Error = e.ToString()
                    });
                    Logger.EndUpgradeSession(UpgradeStatus.Failed, e.Message);
                    Logger.LogError("cleanning failed", e);
                    MarkPipelineFailed("Cleanup", e.Message);
                    return UpgradeStepResult.Failed(e.Message);
                }
            });
        }

        private string DownloadStepTitle =>
            UpdateService.SelfUpdateOnlyMode ? "Downloading app updater" : "Downloading update";

        private string ExtractStepTitle =>
            UpdateService.SelfUpdateOnlyMode ? "Extracting app updater" : "Extracting update";

        private static string FinishingStepTitle => "Finishing update";

        /// <summary>Prefix of the virtual main-app upgrade entry built by the update server.</summary>
        private const string AppUpdateUpgradeIdPrefix = "app-update-";

        /// <summary>
        /// Resolves the <strong>application</strong> version to persist as <see cref="Settings.LastVersion"/> after a successful app upgrade.
        /// Not used when only the updater self-updates — that flow must not overwrite app <see cref="Settings.LastVersion"/>.
        /// Order: manifest <c>targetVersion</c> → upgrade id <c>app-update-…</c> (suffix is the version) → package file name.
        /// </summary>
        /// <param name="unresolvedReason">When return is null: human-readable explanation of why each source failed.</param>
        private static string? ResolvePostInstallRecordedAppVersion(FileInfo packageFile, out string? unresolvedReason)
        {
            unresolvedReason = null;

            if (UpdateService.UseManifestSystem && UpdateService.CurrentUpgradeInfo != null)
            {
                var manifest = UpdateService.CurrentUpgradeInfo;
                if (!string.IsNullOrWhiteSpace(manifest.TargetVersion))
                {
                    return manifest.TargetVersion.Trim();
                }

                var fromUpgradeId = TryGetVersionFromAppUpdateUpgradeId(manifest);
                if (!string.IsNullOrWhiteSpace(fromUpgradeId))
                {
                    return fromUpgradeId.Trim();
                }
            }

            var parsedFromFileName = UpdateService.GetVersionFromFileName(packageFile.Name);
            if (!string.IsNullOrWhiteSpace(parsedFromFileName))
            {
                return parsedFromFileName.Trim();
            }

            unresolvedReason = BuildPostInstallVersionUnresolvedReason(packageFile);
            return null;
        }

        private static string BuildPostInstallVersionUnresolvedReason(FileInfo packageFile)
        {
            var parts = new List<string>();

            if (UpdateService.UseManifestSystem && UpdateService.CurrentUpgradeInfo != null)
            {
                var manifest = UpdateService.CurrentUpgradeInfo;
                if (string.IsNullOrWhiteSpace(manifest.TargetVersion))
                {
                    parts.Add("check-upgrades targetVersion was empty");
                }

                var fromId = TryGetVersionFromAppUpdateUpgradeId(manifest);
                if (string.IsNullOrWhiteSpace(fromId))
                {
                    var ids = manifest.Upgrades == null || manifest.Upgrades.Count == 0
                        ? "(none)"
                        : string.Join(", ", manifest.Upgrades.Select(u => $"'{u.Id ?? "null"}'"));
                    parts.Add($"no id starting with '{AppUpdateUpgradeIdPrefix}' in upgrades list ({ids})");
                }
            }
            else if (!UpdateService.UseManifestSystem)
            {
                parts.Add("UseManifestSystem=false (legacy /download: no check-upgrades metadata for this install)");
            }
            else
            {
                parts.Add("CurrentUpgradeInfo is null (no check-upgrades payload on this session)");
            }

            parts.Add(
                $"package file name '{packageFile.Name}' did not yield a version (GetVersionFromFileName returned nothing)");

            return string.Join("; ", parts) + ".";
        }

        private static string? TryGetVersionFromAppUpdateUpgradeId(UpgradeInfoWrapper manifest)
        {
            var id = manifest.Upgrades?
                .Select(u => u.Id)
                .FirstOrDefault(s =>
                    !string.IsNullOrEmpty(s)
                    && s.StartsWith(AppUpdateUpgradeIdPrefix, StringComparison.OrdinalIgnoreCase)
                    && s.Length > AppUpdateUpgradeIdPrefix.Length);

            return string.IsNullOrEmpty(id) ? null : id[AppUpdateUpgradeIdPrefix.Length..];
        }

        private void ApplyProgress(string step, string? detail, float percent)
        {
            void Apply()
            {
                StepTitle = step;
                DetailText = detail ?? "";
                Percent = Math.Clamp(percent, 0f, 100f);
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                Apply();
            }
            else
            {
                Dispatcher.UIThread.Post(Apply);
            }
        }

        private void OnDownloadProgress(long downloaded, long totalSize, float percent)
        {
            var detail = totalSize > 0
                ? $"{Helper.SizeSuffix(downloaded)} / {Helper.SizeSuffix(totalSize)}"
                : Helper.SizeSuffix(downloaded);
            ApplyProgress(DownloadStepTitle, detail, percent);
        }

        private void OnExtractProgress(long progress, long totalSize, float percent)
        {
            archiveExtractPercent = percent;
            var detail = BuildExtractDetail(progress, totalSize);
            ApplyProgress(ExtractStepTitle, detail, percent);
        }

        private void OnInstallProgress(long progress, long totalSize, float percent)
        {
            var detail = BuildExtractDetail(progress, totalSize);
            var barPercent = archiveExtractPercent > 0f ? archiveExtractPercent : percent;
            ApplyProgress(ExtractStepTitle, detail, barPercent);
        }

        private static string? BuildExtractDetail(long progress, long totalSize)
        {
            var entry = UpdateService.CurrentExtractEntry;
            if (!string.IsNullOrEmpty(entry) && totalSize > 0)
            {
                return $"{entry} ({Helper.SizeSuffix(progress)} / {Helper.SizeSuffix(totalSize)})";
            }

            if (!string.IsNullOrEmpty(entry))
            {
                return entry;
            }

            if (totalSize > 0)
            {
                return $"{Helper.SizeSuffix(progress)} / {Helper.SizeSuffix(totalSize)}";
            }

            return null;
        }
    }
}
