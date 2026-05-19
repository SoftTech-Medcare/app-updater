using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
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
        public ReactiveCommand<Unit, string?> Retry { get; private set; }
        public bool AutoReboot => Settings.Default.AutoReboot;

        public DownloadProgressViewModel()
        {
            Retry = ReactiveCommand.CreateFromObservable(StartDownload);
        }

        public IObservable<string?> StartDownload()
        {
            IsFailed = false;
            return Observable.StartAsync(async () =>
            {
                try
                {
                    var update = new UpdateService();
                    
                    // Determine versions for logging
                    string? fromVersion = null;
                    string? toVersion = null;
                    long? packageSize = null;
                    
                    // Try to get current version
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
                    
                    // Start upgrade session
                    Logger.StartUpgradeSession(fromVersion, toVersion, packageSize);
                    Logger.LogUpgradeEvent(new UpgradeLog
                    {
                        Timestamp = DateTimeOffset.Now,
                        Status = UpgradeStatus.Started,
                        Stage = UpgradeStage.Download,
                        Message = "Starting download"
                    });
                    
                    var filePath = await update.Download(OnDownloadProgress);
                    
                    // Get actual file size
                    if (File.Exists(filePath))
                    {
                        var fileInfo = new FileInfo(filePath);
                        packageSize = fileInfo.Length;
                        // Try to get version from filename
                        if (string.IsNullOrEmpty(toVersion))
                        {
                            toVersion = UpdateService.GetVersionFromFileName(fileInfo.Name);
                        }
                    }
                    
                    Logger.LogUpgradeEvent(new UpgradeLog
                    {
                        Timestamp = DateTimeOffset.Now,
                        Status = UpgradeStatus.Completed,
                        Stage = UpgradeStage.Download,
                        Message = $"Download completed: {filePath}",
                        Details = new Dictionary<string, object> { { "filePath", filePath }, { "size", packageSize ?? 0 } }
                    });
                    
                    Console.WriteLine($"download complete! file: {filePath}");
                    IsDownloaded = true;

                    return filePath;
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
                }

                IsFailed = true;

                return null;
            });
        }

        public IObservable<string?> StartExtract(string sourcePath)
        {
            return Observable.StartAsync(async () =>
            {
                try
                {
                    Console.WriteLine("Start extracting...");
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
                    var extracted = await update.ExtractTarballFile(sourcePath, destination, OnExtractProgress, OnInstallProgress);

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
                    
                    Console.WriteLine($"extract and install complete!");
                    return extracted;
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
                    Console.WriteLine($"Failed extracting: {e}");

                    IsFailed = true;

                    return null;
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

        public IObservable<Unit> CleanAndFinish(string extracted, string sourcePath)
        {
            return Observable.Start(() =>
            {
                try
                {
                    Console.WriteLine("Save version and clean up...");
                    Logger.LogUpgradeEvent(new UpgradeLog
                    {
                        Timestamp = DateTimeOffset.Now,
                        Status = UpgradeStatus.InProgress,
                        Stage = UpgradeStage.Cleanup,
                        Message = "Cleaning up temporary files"
                    });
                    
                    if (!string.IsNullOrEmpty(extracted) && File.Exists(extracted))
                    {
                        File.Delete(extracted);
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
                    Console.WriteLine($"modified: {info.LastWriteTimeUtc}");

                    if (!UpdateService.SelfUpdateOnlyMode)
                    {
                        var newVersion = new UpdateInfo
                        {
                            Modified = info.LastWriteTimeUtc,
                            Version = version
                        };
                        Settings.Default.LastVersion = newVersion;
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
                    
                    // End upgrade session with success
                    Logger.EndUpgradeSession(UpgradeStatus.Completed);

                    Console.WriteLine("Saved and Cleaned!");
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
                    Console.WriteLine($"Failed cleaning: {e}");
                    throw;
                }
            });
        }
    }
}
