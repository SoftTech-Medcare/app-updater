using Avalonia.Threading;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.IO;
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
        private string progressTxt;
        private string labelTxt;
        private bool isFailed;
        private bool downloaded;

        public float Percent { get => percent; set => this.RaiseAndSetIfChanged(ref percent, value); }
        public string ProgressTxt { get => progressTxt; set => this.RaiseAndSetIfChanged(ref progressTxt, value); }
        public string LabelTxt { get => labelTxt; set => this.RaiseAndSetIfChanged(ref labelTxt, value); }

        public bool IsFailed { get => isFailed; set => this.RaiseAndSetIfChanged(ref isFailed, value); }
        public bool IsDownloaded { get => downloaded; set => this.RaiseAndSetIfChanged(ref downloaded, value); }
        public ReactiveCommand<Unit, UpgradeStepResult> Retry { get; private set; }
        public bool AutoReboot => Settings.Default.AutoReboot;

        public void MarkUpgradeComplete()
        {
            ProgressTxt = "";
            Percent = 100f;
            LabelTxt = UpdateService.SelfUpdateOnlyMode
                ? "Updater update staged. Restarting updater..."
                : "Update completed successfully.";
        }

        public void MarkPipelineFailed(string stage, string? detail = null)
        {
            var message = string.IsNullOrWhiteSpace(detail)
                ? $"{stage} failed. Please try again or contact administrator."
                : $"{stage} failed: {detail}";

            void Apply()
            {
                IsFailed = true;
                LabelTxt = message;
                ProgressTxt = "";
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
                    string? version = null;
                    if (UpdateService.SelfUpdateOnlyMode)
                    {
                        version = UpdateService.SelfUpdateTargetVersion;
                    }
                    else if (UpdateService.UseManifestSystem && UpdateService.CurrentUpgradeInfo != null
                        && !string.IsNullOrWhiteSpace(UpdateService.CurrentUpgradeInfo.TargetVersion))
                    {
                        version = UpdateService.CurrentUpgradeInfo.TargetVersion;
                    }

                    if (string.IsNullOrWhiteSpace(version))
                    {
                        version = UpdateService.GetVersionFromFileName(info.Name);
                    }

                    if (!UpdateService.SelfUpdateOnlyMode)
                    {
                        Settings.Default.LastVersion = new UpdateInfo
                        {
                            Modified = info.LastWriteTimeUtc,
                            Version = version
                        };
                        Settings.Default.Save();
                    }

                    File.Delete(sourcePath);

                    Logger.LogUpgradeEvent(new UpgradeLog
                    {
                        Timestamp = DateTimeOffset.Now,
                        Status = UpgradeStatus.Completed,
                        Stage = UpgradeStage.Cleanup,
                        Message = UpdateService.SelfUpdateOnlyMode
                            ? $"Updater self-update staged successfully. Target: {version}"
                            : $"Upgrade completed successfully. New version: {version}"
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

        private void OnDownloadProgress(long downloaded, long totalSize, float percent)
        {
            var current = Helper.SizeSuffix(downloaded);
            var total = Helper.SizeSuffix(totalSize);
            ProgressTxt = $"{current}/{total}";
            LabelTxt = UpdateService.SelfUpdateOnlyMode
                ? $"Downloading App Updater... {percent / 100:P2}"
                : $"Downloading Update... {percent / 100:P2}";
            Percent = percent;
        }

        private void OnExtractProgress(long progress, long totalSize, float percent)
        {
            var current = Helper.SizeSuffix(progress);
            var total = Helper.SizeSuffix(totalSize);
            ProgressTxt = $"{current}/{total}";
            LabelTxt = $"Extracting... {percent / 100:P2}";
            Percent = percent;
        }

        private void OnInstallProgress(long progress, long totalSize, float percent)
        {
            var entry = UpdateService.CurrentExtractEntry;
            if (!string.IsNullOrEmpty(entry) && totalSize > 0)
            {
                ProgressTxt = $"{Helper.SizeSuffix(progress)}/{Helper.SizeSuffix(totalSize)}";
                LabelTxt = UpdateService.SelfUpdateOnlyMode
                    ? $"Installing {entry}... {percent / 100:P2}"
                    : $"Installing {entry}... {percent / 100:P2}";
            }
            else
            {
                ProgressTxt = "";
                LabelTxt = UpdateService.SelfUpdateOnlyMode
                    ? $"Installing App Updater... {percent / 100:P2}"
                    : $"Installing Update... {percent / 100:P2}";
            }

            Percent = percent;
        }
    }
}
