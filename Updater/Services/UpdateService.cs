using Avalonia;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Updater.Properties;
using Updater.Utils;
using UpdaterLib;

namespace Updater.Services
{
    public partial class UpdateService
    {
        public static bool AlreadyDownloaded = false;
        public static bool UseManifestSystem = false;
        public static UpgradeInfoWrapper? CurrentUpgradeInfo = null;

        /// <summary>Tar entry currently being written (for progress UI).</summary>
        public static string? CurrentExtractEntry { get; private set; }

        private const int TarHeaderBufferSize = 512;
        private const int TarContentBufferSize = 64 * 1024;
        private const int ExtractProgressThrottleMs = 200;

        /// <summary>True when the update server has a newer Updater package than this running build.</summary>
        public static bool SelfUpdateAdvertised = false;

        /// <summary>True when only the updater package needs to be fetched (main app already current).</summary>
        public static bool SelfUpdateOnlyMode = false;

        public static string? SelfUpdateTargetVersion = null;
        public static string? SelfUpdateTargetFileName = null;

        public class VersionInfo
        {
            public string? Version { get; set; }
            public string? File { get; set; }
            public DateTimeOffset LastModified { get; set; }
        }

        public class LatestVersionInfo
        {
            public VersionInfo? Stable { get; set; }
            public VersionInfo? PreRelease { get; set; }
        }

        public static string GetMD5HashFromFile(string fileName)
        {
            using (var md5 = MD5.Create())
            {
                using (var stream = File.OpenRead(fileName))
                {
                    return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", string.Empty);
                }
            }
        }

        public async Task<LatestVersionInfo?> GetLatestVersionInfo()
        {
            var server = Settings.Default.UpdateServer;
            var appName = Settings.Default.AppName;
            var includePreRelease = Settings.Default.EnablePreReleaseVersions;

            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(appName))
            {
                return null;
            }

            return await GetLatestVersionInfoAsync(server, appName, includePreRelease);
        }

        public static async Task<LatestVersionInfo?> GetLatestVersionInfoAsync(string server, string appName, bool includePreRelease)
        {
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(appName))
            {
                return null;
            }

            var url = $"{server.TrimEnd('/')}/update/{Uri.EscapeDataString(appName)}/latest-info?includePreRelease={includePreRelease}";

            try
            {
                using var client = CreateCheckHttpClient();
                using var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogError($"Error getting latest version info for '{appName}': HTTP {(int)response.StatusCode}");
                    return null;
                }

                return await response.Content.ReadFromJsonAsync<LatestVersionInfo>();
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error getting latest version info for '{appName}': {ex.Message}");
                return null;
            }
        }

        /// <summary>Version checks and small JSON calls (update server on LAN or WAN).</summary>
        private static readonly TimeSpan CheckHttpTimeout = TimeSpan.FromSeconds(60);

        /// <summary>Full package downloads (app + updater tarballs can be large on slow links).</summary>
        private static readonly TimeSpan DownloadHttpTimeout = TimeSpan.FromMinutes(30);

        private static HttpClient CreateUpdateHttpClient(TimeSpan timeout)
        {
            var client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = true,
                CheckCertificateRevocationList = false,
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            })
            {
                Timeout = timeout
            };
            client.DefaultRequestHeaders.Add("User-Agent", $"AppUpdater/{GetUpdaterVersion()}");
            return client;
        }

        private static HttpClient CreateCheckHttpClient() => CreateUpdateHttpClient(CheckHttpTimeout);

        private static HttpClient CreateDownloadHttpClient() => CreateUpdateHttpClient(DownloadHttpTimeout);

        private static string BuildDownloadUrl(string server, string appName, bool includePreRelease)
        {
            var baseUrl = server.TrimEnd('/');
            const string updaterApp = "Updater";

            // Self-update always uses the standalone Updater publish tarball, not the app's download-upgrade bundle.
            if (SelfUpdateOnlyMode)
            {
                return $"{baseUrl}/update/{Uri.EscapeDataString(updaterApp)}/download?includePreRelease={includePreRelease}";
            }

            if (UseManifestSystem && CurrentUpgradeInfo != null)
            {
                var fromVersion = Uri.EscapeDataString(CurrentUpgradeInfo.CurrentVersion ?? "");
                return $"{baseUrl}/update/{Uri.EscapeDataString(appName)}/download-upgrade?fromVersion={fromVersion}&includePrerelease={includePreRelease}";
            }

            return $"{baseUrl}/update/{Uri.EscapeDataString(appName)}/download?includePreRelease={includePreRelease}";
        }

        private static string ResolveDownloadFileName(HttpResponseMessage response)
        {
            var disposition = response.Content.Headers.ContentDisposition;
            var fileName = disposition?.FileNameStar ?? disposition?.FileName;
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return PathHelper.SanitizeDownloadFileName(fileName);
            }

            if (UseManifestSystem)
            {
                return "upgrade-package.tar.gz";
            }

            if (SelfUpdateOnlyMode)
            {
                if (!string.IsNullOrWhiteSpace(SelfUpdateTargetFileName))
                {
                    return SelfUpdateTargetFileName;
                }

                if (!string.IsNullOrWhiteSpace(SelfUpdateTargetVersion))
                {
                    return $"updater-{PathHelper.SanitizePathSegment(SelfUpdateTargetVersion)}.tar.gz";
                }

                return "updater-update.tar.gz";
            }

            return "unknown.gz";
        }

        public delegate void OnProgress(long currentSize, long totalSize, float percent);
        public delegate void OnInstallProgress(long currentSize, long totalSize, float percent);

        public async Task<string> Download(OnProgress onUpdateProgress)
        {
            var server = Settings.Default.UpdateServer;
            var appName = Settings.Default.AppName;
            var downloadPath = AppDomain.CurrentDomain.BaseDirectory;

            // if already downloaded skip download again (Only for old system or if filename matches)
            string? currentFile = GetCurrentFile();
            if (!UseManifestSystem && !SelfUpdateOnlyMode && !SelfUpdateAdvertised
                && !string.IsNullOrWhiteSpace(currentFile) && AlreadyDownloaded)
            {
                Console.WriteLine("Already downloaded, so skip and extract current file...");
                var info = new FileInfo(currentFile);
                Dispatcher.UIThread.Post(() =>
                {
                    onUpdateProgress?.Invoke(info.Length, info.Length, 100f);
                });

                return currentFile;
            }

            var includePreRelease = Settings.Default.EnablePreReleaseVersions;
            var url = BuildDownloadUrl(server, appName, includePreRelease);

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            using var client = CreateDownloadHttpClient();
            using (var res = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                long? totalToReceive = res.Content.Headers.ContentLength;
                long totalDownloaded = 0;
                var fileName = ResolveDownloadFileName(res);
                var lastModified = res.Content.Headers.LastModified?.UtcDateTime ?? DateTime.UtcNow;
                string filePath = Path.Combine(downloadPath, fileName);
                using (var stream = await res.Content.ReadAsStreamAsync())
                {
                    if (!totalToReceive.HasValue)
                    {
                        totalToReceive = stream.Length;
                    }
                    const int step = 1024; 
                    var buffer = new byte[step];
                    using (var sw = File.Create(filePath))
                    {
                        int read = 0;
                        Stopwatch timer = new Stopwatch();
                        timer.Start();
                        do
                        {
                            read = await stream.ReadAsync(buffer, 0, step);
                            totalDownloaded += read;

                            sw.Write(buffer, 0, read);
                            var percent = (double)totalDownloaded / totalToReceive * 100;
                            if (timer.ElapsedMilliseconds > 16.65 || percent == 100)
                            {
                                Dispatcher.UIThread.Post(() =>
                                {
                                    onUpdateProgress?.Invoke(totalDownloaded, totalToReceive.Value , (float)percent);
                                });
                                timer.Restart();
                            }
                            
                        } while (read > 0);
                        timer.Stop();
                    }
                }

                File.SetLastWriteTimeUtc(filePath, lastModified);

                return filePath;
            }
        }

        /// <summary>
        /// After a combined app upgrade, ensure updater self-update is staged under pending-update
        /// even if the bundle step failed (downloads standalone Updater when needed).
        /// </summary>
        public async Task StageSelfUpdateIfNeededAsync(OnProgress onExtractProgress, OnInstallProgress onInstallProgress)
        {
            if (!SelfUpdateAdvertised)
            {
                return;
            }

            var pendingDir = GetSelfUpdatePendingDirectory();
            if (Directory.Exists(pendingDir) && Directory.EnumerateFileSystemEntries(pendingDir).Any())
            {
                Logger.LogUpgradeOutput($"Self-update already staged at {pendingDir}");
                return;
            }

            Logger.LogUpgradeOutput(
                "Self-update not staged from app bundle; downloading standalone Updater package. " +
                "Common causes: legacy /download (no manifest bundle), missing User-Agent AppUpdater/x.y.z on server build, " +
                "or no updater-self-update-* folder in the upgrade package.");
            var wasSelfOnly = SelfUpdateOnlyMode;
            try
            {
                SelfUpdateOnlyMode = true;
                var tarball = await Download(onExtractProgress);
                Directory.CreateDirectory(pendingDir);
                await ExtractTarballFile(tarball, pendingDir, onExtractProgress, onInstallProgress);
            }
            finally
            {
                SelfUpdateOnlyMode = wasSelfOnly;
            }
        }

        public async Task ExtractTarballFile(string filePath, string destinationPath, OnProgress onExtractProgress, OnInstallProgress onInstallProgress)
        {
            Logger.LogUpgradeOutput($"=== Starting ExtractTarballFile ===");
            Logger.LogUpgradeOutput($"Source file: {filePath}");
            Logger.LogUpgradeOutput($"Destination: {destinationPath}");
            
            var fileInfo = new FileInfo(filePath);
            Logger.LogUpgradeOutput($"File size: {fileInfo.Length} bytes");
            
            // Try to determine versions from package manifest if available
            string? fromVersion = null;
            string? toVersion = null;
            
            PathHelper.ValidateWritablePath(filePath, nameof(filePath));
            PathHelper.ValidateWritablePath(destinationPath, nameof(destinationPath));

            Directory.CreateDirectory(destinationPath);

            Logger.LogUpgradeEvent(new UpgradeLog
            {
                Timestamp = DateTimeOffset.Now,
                Status = UpgradeStatus.InProgress,
                Stage = UpgradeStage.Extract,
                Message = "Extracting archive"
            });

            // Stream gzip -> tar directly (same as manifest self-update path). Avoids writing an
            // intermediate .tar inside destinationPath, which could collide with tar entries and
            // leave only the first file extracted (e.g. updater.deps.json only).
            using (FileStream source = File.OpenRead(filePath))
            using (ProgressStream progressStream = new ProgressStream(source))
            using (GZipStream unzipped = new GZipStream(progressStream, CompressionMode.Decompress))
            {
                var compressedTotal = source.Length;
                long lastDownloadProgressMs = 0;
                await ExtractTar(unzipped, destinationPath, (c, t, p) =>
                {
                    ReportProgressOnUiThread(onInstallProgress, c, t, p);

                    var now = Environment.TickCount64;
                    if (compressedTotal <= 0 || now - lastDownloadProgressMs < ExtractProgressThrottleMs)
                    {
                        return;
                    }

                    lastDownloadProgressMs = now;
                    var extractPercent = (float)Math.Min(
                        100.0,
                        (double)progressStream.BytesRead / compressedTotal * 100);
                    ReportProgressOnUiThread(
                        onExtractProgress,
                        progressStream.BytesRead,
                        compressedTotal,
                        extractPercent);
                });
                ReportProgressOnUiThread(
                    onExtractProgress,
                    progressStream.BytesRead,
                    compressedTotal,
                    100f);
            }

            var extractedFiles = Directory.Exists(destinationPath)
                ? Directory.GetFiles(destinationPath, "*", SearchOption.AllDirectories)
                : Array.Empty<string>();
            Logger.LogUpgradeOutput(
                $"Tar extraction completed: {extractedFiles.Length} file(s) in {destinationPath}");
            if (extractedFiles.Length > 0)
            {
                Logger.LogUpgradeOutput(
                    "Extracted sample: " + string.Join(", ", extractedFiles.Take(8).Select(Path.GetFileName)));
            }
            
            var packageManifestPath = Path.Combine(destinationPath, "package-manifest.json");
            if (SelfUpdateOnlyMode)
            {
                if (File.Exists(packageManifestPath))
                {
                    Logger.LogUpgradeOutput("Self-update: unpacking updater archive from legacy upgrade bundle...");
                    await ApplySelfUpdateFromUpgradePackageAsync(destinationPath);
                }
                else
                {
                    Logger.LogUpgradeOutput("Self-update: Updater tarball extracted to pending-update");
                    ValidateUpdaterPayload(destinationPath);
                }
            }
            else if (File.Exists(packageManifestPath))
            {
                Logger.LogUpgradeOutput("Manifest package detected. Reading package manifest...");
                try
                {
                    var packageManifest = DeserializeJsonFile<UpgradePackageManifest>(packageManifestPath);
                    if (packageManifest != null)
                    {
                        fromVersion = packageManifest.FromVersion;
                        toVersion = packageManifest.ToVersion;
                        Logger.LogUpgradeOutput($"Package manifest: From {fromVersion} to {toVersion}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogUpgradeOutput($"Warning: Failed to parse package manifest: {ex.Message}");
                }
                
                Console.WriteLine("Manifest package detected. Applying upgrades...");
                Logger.LogUpgradeOutput("Applying upgrades from manifest package...");
                await ApplyUpgrades(destinationPath, packageManifestPath);
            }
            else
            {
                Logger.LogUpgradeOutput("No package manifest found - standard app update");
            }

            Logger.LogUpgradeOutput("=== ExtractTarballFile completed ===");
        }

        private static void ValidateUpdaterPayload(string directory)
        {
            var payloadDir = directory;
            if (!File.Exists(Path.Combine(directory, "Updater.dll")))
            {
                var nested = Directory.GetDirectories(directory)
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, "Updater.dll")));
                if (nested != null)
                {
                    throw new InvalidOperationException(
                        $"Updater payload is nested in {Path.GetFileName(nested)}/; expected flat layout in {directory}");
                }

                var found = Directory.Exists(directory)
                    ? string.Join(", ", Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                        .Take(20)
                        .Select(Path.GetFileName))
                    : "(directory missing)";
                throw new FileNotFoundException(
                    $"Updater self-update incomplete: Updater.dll not found in {directory}. Found: {found}");
            }

            if (!File.Exists(Path.Combine(payloadDir, "Updater.runtimeconfig.json")))
            {
                throw new FileNotFoundException(
                    $"Updater self-update incomplete: Updater.runtimeconfig.json missing in {directory}");
            }

            var fileCount = Directory.GetFiles(payloadDir, "*", SearchOption.AllDirectories).Length;
            if (fileCount < 5)
            {
                throw new InvalidOperationException(
                    $"Updater self-update incomplete: expected a full publish output but only {fileCount} file(s) in {directory}");
            }
        }

        /// <summary>
        /// Legacy download-upgrade bundles nest the Updater .tar.gz under upgrades/updater-self-update-*.
        /// Unpack without parsing manifest JSON (avoids tar padding/null-byte JSON issues).
        /// </summary>
        private static async Task ApplySelfUpdateFromUpgradePackageAsync(string destinationPath)
        {
            var upgradesDir = Path.Combine(destinationPath, "upgrades");
            if (!Directory.Exists(upgradesDir))
            {
                throw new DirectoryNotFoundException($"Self-update upgrades folder not found: {upgradesDir}");
            }

            var applied = false;
            foreach (var upgradeDir in Directory.GetDirectories(upgradesDir))
            {
                var upgradeId = Path.GetFileName(upgradeDir);
                if (!IsSelfUpdateUpgradeId(upgradeId))
                {
                    continue;
                }

                await ApplySelfUpdateUpgradeDirectoryAsync(upgradeDir, upgradeId);
                applied = true;
            }

            if (!applied)
            {
                throw new FileNotFoundException("No updater .tar.gz found in self-update upgrade package");
            }

            var packageManifestPath = Path.Combine(destinationPath, "package-manifest.json");
            if (File.Exists(packageManifestPath))
            {
                File.Delete(packageManifestPath);
            }

            if (Directory.Exists(upgradesDir))
            {
                Directory.Delete(upgradesDir, true);
            }

            var outerTar = Directory.GetFiles(destinationPath, "*.tar")
                .Concat(Directory.GetFiles(destinationPath, "*.tar.gz"))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}upgrades{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .ToList();
            foreach (var leftover in outerTar)
            {
                try
                {
                    File.Delete(leftover);
                }
                catch (Exception ex)
                {
                    Logger.LogUpgradeOutput($"Warning: could not delete leftover archive {leftover}: {ex.Message}");
                }
            }
        }

        private static T? DeserializeJsonFile<T>(string path) => JsonFileReader.Read<T>(path);

        private static string ResolveInstallTargetPath(string appDestinationPath, string? fileTarget, bool isSelfUpdateUpgrade)
        {
            if (string.IsNullOrWhiteSpace(fileTarget))
            {
                return appDestinationPath;
            }

            if (Path.IsPathRooted(fileTarget))
            {
                return fileTarget;
            }

            if (isSelfUpdateUpgrade)
            {
                return Path.Combine(GetUpdaterInstallDirectory(), fileTarget);
            }

            return Path.Combine(appDestinationPath, fileTarget);
        }

        private static async Task ApplySelfUpdateUpgradeDirectoryAsync(string upgradePath, string? upgradeId)
        {
            var version = ParseSelfUpdateVersionFromId(upgradeId) ?? SelfUpdateTargetVersion ?? GetUpdaterVersion();
            var pendingDir = GetSelfUpdatePendingDirectory(version);
            Directory.CreateDirectory(pendingDir);

            var archives = Directory.GetFiles(upgradePath, "*.tar.gz");
            if (archives.Length == 0)
            {
                throw new FileNotFoundException($"No updater archive in {upgradePath}");
            }

            foreach (var archive in archives)
            {
                Logger.LogUpgradeOutput($"Self-update: extracting {archive} -> {pendingDir}");
                using var fs = File.OpenRead(archive);
                using var gzip = new GZipStream(fs, CompressionMode.Decompress);
                await ExtractTar(gzip, pendingDir);
            }

            ValidateUpdaterPayload(pendingDir);
        }

        private static async Task ApplyAppUpdateFromDirectoryAsync(string upgradePath, string destinationPath)
        {
            var archives = Directory.GetFiles(upgradePath, "*.tar.gz");
            if (archives.Length == 0)
            {
                throw new FileNotFoundException($"No app update archive in {upgradePath}");
            }

            foreach (var archive in archives)
            {
                Logger.LogUpgradeOutput($"App update: extracting {archive} -> {destinationPath}");
                using var fs = File.OpenRead(archive);
                using var gzip = new GZipStream(fs, CompressionMode.Decompress);
                await ExtractTar(gzip, destinationPath);
            }
        }

        private async Task ApplyUpgrades(string destinationPath, string packageManifestPath)
        {
            try 
            {
                Logger.LogUpgradeOutput("=== Starting ApplyUpgrades ===");
                Logger.LogUpgradeOutput($"Package manifest path: {packageManifestPath}");
                Logger.LogUpgradeOutput($"Destination path: {destinationPath}");

                var manifest = DeserializeJsonFile<UpgradePackageManifest>(packageManifestPath);
                if (manifest == null || manifest.Upgrades == null) 
                {
                    Logger.LogUpgradeOutput("No upgrades found in manifest");
                    return;
                }
                
                Logger.LogUpgradeOutput($"Found {manifest.Upgrades.Count} upgrade(s) to apply");
                Logger.LogUpgradeOutput($"From version: {manifest.FromVersion ?? "unknown"}");
                Logger.LogUpgradeOutput($"To version: {manifest.ToVersion ?? "unknown"}");
                
                var upgradesDir = Path.Combine(destinationPath, "upgrades");

                // Install app (and intermediate) upgrades before staging updater self-update.
                var orderedUpgradeIds = manifest.Upgrades
                    .OrderBy(id => IsSelfUpdateUpgradeId(id) ? 1 : 0)
                    .ToList();

                foreach (var upgradeId in orderedUpgradeIds)
                {
                    Logger.LogUpgradeOutput($"\n--- Processing upgrade: {upgradeId} ---");
                    
                    var upgradePath = Path.Combine(upgradesDir, upgradeId);
                    if (!Directory.Exists(upgradePath))
                    {
                        var errorMsg = $"Upgrade folder not found: {upgradePath}";
                        Logger.LogError(errorMsg);
                        throw new DirectoryNotFoundException(errorMsg);
                    }

                    if (IsSelfUpdateUpgradeId(upgradeId))
                    {
                        Logger.LogUpgradeEvent(new UpgradeLog
                        {
                            Timestamp = DateTimeOffset.Now,
                            UpgradeId = upgradeId,
                            Status = UpgradeStatus.Started,
                            Stage = UpgradeStage.Install,
                            Message = "Staging updater self-update"
                        });

                        await ApplySelfUpdateUpgradeDirectoryAsync(upgradePath, upgradeId);

                        Logger.LogUpgradeEvent(new UpgradeLog
                        {
                            Timestamp = DateTimeOffset.Now,
                            UpgradeId = upgradeId,
                            Status = UpgradeStatus.Completed,
                            Stage = UpgradeStage.Install,
                            Message = $"Updater staged at {GetSelfUpdatePendingDirectory(ParseSelfUpdateVersionFromId(upgradeId))}"
                        });
                        Logger.LogUpgradeOutput($"Self-update staged: {upgradeId}");
                        continue;
                    }

                    var upgradeManifestPath = Path.Combine(upgradePath, "manifest.json");
                    UpgradeManifest? upgradeManifest = null;
                    if (File.Exists(upgradeManifestPath))
                    {
                        upgradeManifest = DeserializeJsonFile<UpgradeManifest>(upgradeManifestPath);
                    }

                    if (upgradeManifest == null)
                    {
                        if (upgradeId.StartsWith("app-update", StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.LogUpgradeOutput($"Manifest unreadable for {upgradeId}; extracting app archive directly");
                            await ApplyAppUpdateFromDirectoryAsync(upgradePath, destinationPath);
                            continue;
                        }

                        var parseError = $"Failed to parse manifest for {upgradeId}";
                        Logger.LogError(parseError);
                        throw new InvalidOperationException(parseError);
                    }
                    
                    Logger.LogUpgradeEvent(new UpgradeLog
                    {
                        Timestamp = DateTimeOffset.Now,
                        UpgradeId = upgradeId,
                        UpgradeName = upgradeManifest.Name,
                        Status = UpgradeStatus.Started,
                        Stage = UpgradeStage.Install,
                        Message = $"Starting upgrade: {upgradeManifest.Name ?? upgradeId}"
                    });
                    
                    // Scripts (Pre)
                    if (!string.IsNullOrEmpty(upgradeManifest.PreInstallScript))
                    {
                        var scriptPath = Path.Combine(upgradePath, upgradeManifest.PreInstallScript);
                        if (File.Exists(scriptPath))
                        {
                            Logger.LogUpgradeEvent(new UpgradeLog
                            {
                                Timestamp = DateTimeOffset.Now,
                                UpgradeId = upgradeId,
                                UpgradeName = upgradeManifest.Name,
                                Status = UpgradeStatus.InProgress,
                                Stage = UpgradeStage.PreInstall,
                                Message = $"Running PreInstallScript: {upgradeManifest.PreInstallScript}"
                            });
                            
                            Logger.LogInfo($"Running PreInstallScript: {upgradeManifest.PreInstallScript}");
                            RunScript(scriptPath, upgradePath, destinationPath);
                            
                            Logger.LogUpgradeEvent(new UpgradeLog
                            {
                                Timestamp = DateTimeOffset.Now,
                                UpgradeId = upgradeId,
                                UpgradeName = upgradeManifest.Name,
                                Status = UpgradeStatus.InProgress,
                                Stage = UpgradeStage.PreInstall,
                                Message = $"PreInstallScript completed: {upgradeManifest.PreInstallScript}"
                            });
                        }
                    }

                    // Files (Binaries & Configs)
                    if (upgradeManifest.Files != null && upgradeManifest.Files.Count > 0)
                    {
                        Logger.LogUpgradeOutput($"Installing {upgradeManifest.Files.Count} file(s)");
                        Logger.LogUpgradeEvent(new UpgradeLog
                        {
                            Timestamp = DateTimeOffset.Now,
                            UpgradeId = upgradeId,
                            UpgradeName = upgradeManifest.Name,
                            Status = UpgradeStatus.InProgress,
                            Stage = UpgradeStage.Install,
                            Message = $"Installing {upgradeManifest.Files.Count} file(s)"
                        });

                        foreach (var file in upgradeManifest.Files)
                        {
                            if (string.IsNullOrEmpty(file.Path)) continue;
                            
                            var sourceFile = Path.Combine(upgradePath, file.Path);
                            var targetPath = ResolveInstallTargetPath(
                                destinationPath,
                                file.Target,
                                IsSelfUpdateUpgradeId(upgradeId));
                            
                            Logger.LogUpgradeOutput($"Installing file: {file.Path} -> {targetPath}");
                            
                            // Ensure target dir exists
                            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                            
                            if (File.Exists(sourceFile))
                            {
                                if (file.Explode)
                                {
                                    Logger.LogUpgradeOutput($"Extracting archive: {sourceFile}");
                                    using (var fs = File.OpenRead(sourceFile))
                                    {
                                        if (sourceFile.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                                        {
                                            using (var gzip = new GZipStream(fs, CompressionMode.Decompress))
                                            {
                                                await ExtractTar(gzip, targetPath);
                                            }
                                        }
                                        else
                                        {
                                            await ExtractTar(fs, targetPath);
                                        }
                                    }
                                    Logger.LogUpgradeOutput($"Extraction completed: {targetPath}");
                                }
                                else
                                {
                                    File.Copy(sourceFile, targetPath, true);
                                    Logger.LogUpgradeOutput($"File copied: {targetPath}");
                                    
                                    if (OperatingSystem.IsLinux()) 
                                    {
                                        if (!string.IsNullOrEmpty(file.Permissions))
                                        {
                                            Chmod(targetPath, file.Permissions);
                                            Logger.LogUpgradeOutput($"Set permissions {file.Permissions} on {targetPath}");
                                        }
                                        else if (file.IsExecutable)
                                        {
                                            Chmod(targetPath, "+x");
                                            Logger.LogUpgradeOutput($"Set executable permission on {targetPath}");
                                        }
                                    }
                                }
                            }
                            else
                            {
                                var errorMsg = $"Source file not found: {sourceFile}";
                                Logger.LogUpgradeOutput($"ERROR: {errorMsg}");
                                if (file.IsRequired)
                                {
                                    throw new FileNotFoundException(errorMsg);
                                }
                            }
                        }
                    }

                    // Scripts (Post)
                    if (!string.IsNullOrEmpty(upgradeManifest.PostInstallScript))
                    {
                        var scriptPath = Path.Combine(upgradePath, upgradeManifest.PostInstallScript);
                        if (File.Exists(scriptPath))
                        {
                            Logger.LogUpgradeEvent(new UpgradeLog
                            {
                                Timestamp = DateTimeOffset.Now,
                                UpgradeId = upgradeId,
                                UpgradeName = upgradeManifest.Name,
                                Status = UpgradeStatus.InProgress,
                                Stage = UpgradeStage.PostInstall,
                                Message = $"Running PostInstallScript: {upgradeManifest.PostInstallScript}"
                            });
                            
                            Logger.LogInfo($"Running PostInstallScript: {upgradeManifest.PostInstallScript}");
                            RunScript(scriptPath, upgradePath, destinationPath);
                            
                            Logger.LogUpgradeEvent(new UpgradeLog
                            {
                                Timestamp = DateTimeOffset.Now,
                                UpgradeId = upgradeId,
                                UpgradeName = upgradeManifest.Name,
                                Status = UpgradeStatus.InProgress,
                                Stage = UpgradeStage.PostInstall,
                                Message = $"PostInstallScript completed: {upgradeManifest.PostInstallScript}"
                            });
                        }
                    }
                    
                    Logger.LogUpgradeEvent(new UpgradeLog
                    {
                        Timestamp = DateTimeOffset.Now,
                        UpgradeId = upgradeId,
                        UpgradeName = upgradeManifest.Name,
                        Status = UpgradeStatus.Completed,
                        Stage = UpgradeStage.Install,
                        Message = $"Upgrade completed successfully: {upgradeManifest.Name ?? upgradeId}"
                    });
                    
                    Logger.LogUpgradeOutput($"Upgrade completed: {upgradeId}");
                }
                
                Logger.LogUpgradeOutput("\n=== Cleanup ===");
                // Cleanup
                File.Delete(packageManifestPath);
                Logger.LogUpgradeOutput($"Deleted package manifest: {packageManifestPath}");
                
                if (Directory.Exists(upgradesDir)) 
                {
                    Directory.Delete(upgradesDir, true);
                    Logger.LogUpgradeOutput($"Deleted upgrades directory: {upgradesDir}");
                }
                
                Logger.LogUpgradeOutput("=== ApplyUpgrades completed successfully ===");
            }
            catch (Exception ex)
            {
                var errorMsg = $"Error applying upgrades: {ex.Message}";
                Logger.LogUpgradeOutput($"ERROR: {errorMsg}");
                Logger.LogUpgradeEvent(new UpgradeLog
                {
                    Timestamp = DateTimeOffset.Now,
                    Status = UpgradeStatus.Failed,
                    Stage = UpgradeStage.Install,
                    Message = errorMsg,
                    Error = ex.ToString()
                });
                Logger.LogError("Error applying upgrades", ex);
                throw;
            }
        }

        private void RunScript(string scriptPath, string workingDir, string destinationPath)
        {
            try
            {
                Logger.LogUpgradeOutput($"Starting script: {scriptPath}");
                Logger.LogUpgradeOutput($"Working directory: {workingDir}");
                Logger.LogUpgradeOutput($"Destination path: {destinationPath}");

                var info = new ProcessStartInfo
                {
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                // Add destination path as argument
                var args = $"\"{destinationPath}\"";

                if (OperatingSystem.IsLinux())
                {
                    // Ensure script is executable
                    Chmod(scriptPath, "+x");
                    
                    info.FileName = "/bin/bash";
                    info.Arguments = $"\"{scriptPath}\" {args}";
                }
                else
                {
                    // Windows - assume batch or just run it
                    info.FileName = scriptPath;
                    info.Arguments = args;
                }
                
                Logger.LogUpgradeOutput($"Executing: {info.FileName} {info.Arguments}");
                
                using (var process = Process.Start(info))
                {
                    if (process == null)
                    {
                        throw new Exception("Failed to start process");
                    }

                    // Capture all output to upgrade.log
                    process.OutputDataReceived += (sender, e) => 
                    { 
                        if (e.Data != null) 
                        {
                            Logger.LogUpgradeOutput($"[STDOUT] {e.Data}");
                            Logger.LogInfo($"[Script Output] {e.Data}");
                        }
                    };
                    process.ErrorDataReceived += (sender, e) => 
                    { 
                        if (e.Data != null) 
                        {
                            Logger.LogUpgradeOutput($"[STDERR] {e.Data}");
                            Logger.LogError($"[Script Error] {e.Data}");
                        }
                    };
                    
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    
                    process.WaitForExit();
                    
                    Logger.LogUpgradeOutput($"Script exited with code: {process.ExitCode}");
                    
                    if (process.ExitCode != 0)
                    {
                        throw new Exception($"Script exited with code {process.ExitCode}");
                    }
                }
                
                Logger.LogUpgradeOutput($"Script completed successfully: {scriptPath}");
            }
            catch (Exception ex)
            {
                Logger.LogUpgradeOutput($"Script failed: {ex.Message}");
                Logger.LogError($"Failed to run script {scriptPath}", ex);
                throw;
            }
        }
        
        /// <summary>
        /// Reads exactly one 512-byte tar header. Returns false on clean EOF before any byte.
        /// GZip/file streams may return partial reads; we must loop until 512 or EOF.
        /// </summary>
        private static async Task<bool> TryReadTarHeaderAsync(Stream stream, byte[] headerBuffer)
        {
            int offset = 0;
            while (offset < TarHeaderBufferSize)
            {
                int read = await stream.ReadAsync(headerBuffer, offset, TarHeaderBufferSize - offset);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            if (offset == 0)
            {
                return false;
            }

            if (offset < TarHeaderBufferSize)
            {
                var partialAllZero = true;
                for (var i = 0; i < offset; i++)
                {
                    if (headerBuffer[i] != 0)
                    {
                        partialAllZero = false;
                        break;
                    }
                }

                if (!partialAllZero)
                {
                    throw new IOException(
                        $"Truncated tar header at end of archive ({offset}/{TarHeaderBufferSize} bytes)");
                }

                Array.Clear(headerBuffer, offset, TarHeaderBufferSize - offset);
            }

            return true;
        }

        private static string GetTarEntryPath(ReadOnlySpan<byte> header)
        {
            var namePart = Encoding.ASCII.GetString(header.Slice(0, 100)).TrimEnd('\0');
            var prefixPart = Encoding.ASCII.GetString(header.Slice(345, 155)).TrimEnd('\0');
            if (string.IsNullOrEmpty(prefixPart))
            {
                return namePart;
            }

            if (string.IsNullOrEmpty(namePart))
            {
                return prefixPart;
            }

            return $"{prefixPart.TrimEnd('/')}/{namePart}";
        }

        private static char GetTarTypeFlag(ReadOnlySpan<byte> header) => (char)header[156];

        private static async Task SkipTarEntryPaddingAsync(Stream stream, byte[] buffer, long fileSize)
        {
            long padding = (512 - (fileSize % 512)) % 512;
            if (padding <= 0)
            {
                return;
            }

            long remaining = padding;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, buffer.Length);
                int read = await stream.ReadAsync(buffer, 0, toRead);
                if (read == 0)
                {
                    throw new IOException("Unexpected end of archive while skipping tar padding");
                }

                remaining -= read;
            }
        }

        private static async Task<string> ReadTarEntryStringAsync(Stream stream, byte[] buffer, long length)
        {
            using var ms = new MemoryStream();
            long remaining = length;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, buffer.Length);
                int read = await stream.ReadAsync(buffer, 0, toRead);
                if (read == 0)
                {
                    throw new IOException("Unexpected end of archive while reading tar meta entry");
                }

                await ms.WriteAsync(buffer, 0, read);
                remaining -= read;
            }

            await SkipTarEntryPaddingAsync(stream, buffer, length);
            return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\0');
        }

        private static async Task<long> SkipTarEntryContentAsync(Stream stream, byte[] buffer, long fileSize)
        {
            long skipped = 0;
            long remaining = fileSize;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, buffer.Length);
                int read = await stream.ReadAsync(buffer, 0, toRead);
                skipped += read;
                if (read == 0)
                {
                    throw new IOException("Unexpected end of archive while skipping tar entry");
                }

                remaining -= read;
            }

            await SkipTarEntryPaddingAsync(stream, buffer, fileSize);
            return skipped;
        }

        private static void ReportProgressOnUiThread(OnProgress? onProgress, long current, long total, float percent)
        {
            if (onProgress == null)
            {
                return;
            }

            Dispatcher.UIThread.Post(() => onProgress(current, total, percent));
        }

        private static void ReportProgressOnUiThread(
            OnInstallProgress? onProgress,
            long current,
            long total,
            float percent)
        {
            if (onProgress == null)
            {
                return;
            }

            Dispatcher.UIThread.Post(() => onProgress(current, total, percent));
        }

        private static void ReportExtractProgress(
            OnProgress? onProgress,
            ref long lastReportMs,
            long current,
            long total,
            float percent)
        {
            if (onProgress == null || total <= 0)
            {
                return;
            }

            var now = Environment.TickCount64;
            if (percent < 100f && now - lastReportMs < ExtractProgressThrottleMs)
            {
                return;
            }

            lastReportMs = now;
            ReportProgressOnUiThread(onProgress, current, total, percent);
        }

        public static async Task ExtractTar(Stream stream, string outputDir, OnProgress? onProgress = null)
        {
            var headerBuffer = new byte[TarHeaderBufferSize];
            var contentBuffer = new byte[TarContentBufferSize];
            Directory.CreateDirectory(outputDir);
            long lastReportMs = 0;
            CurrentExtractEntry = null;

            string? pendingPath = null;
            var entriesProcessed = 0;

            try
            {
                while (true)
                {
                    if (!await TryReadTarHeaderAsync(stream, headerBuffer))
                    {
                        break;
                    }

                    if (headerBuffer.All(b => b == 0))
                    {
                        break;
                    }

                    var typeFlag = GetTarTypeFlag(headerBuffer);
                    var rawPath = GetTarEntryPath(headerBuffer);
                    string sizeStr = Encoding.ASCII.GetString(headerBuffer, 124, 12);
                    if (!PathHelper.TryParseTarSizeField(sizeStr, out long fileSize))
                    {
                        throw new InvalidDataException(
                            $"Invalid tar size field for '{rawPath}': '{sizeStr.Trim()}'");
                    }

                    entriesProcessed++;

                    if (typeFlag == 'L')
                    {
                        pendingPath = await ReadTarEntryStringAsync(stream, contentBuffer, fileSize);
                        Logger.LogUpgradeOutput($"Tar long name: {pendingPath}");
                        continue;
                    }

                    if (typeFlag == 'K')
                    {
                        await ReadTarEntryStringAsync(stream, contentBuffer, fileSize);
                        Logger.LogUpgradeOutput($"Tar long link skipped: {rawPath}");
                        continue;
                    }

                    if (typeFlag is 'x' or 'g')
                    {
                        Logger.LogUpgradeOutput($"Skipping tar meta: {rawPath} ({fileSize} bytes)");
                        await SkipTarEntryContentAsync(stream, contentBuffer, fileSize);
                        continue;
                    }

                    if (typeFlag is '2' or '3' or '4' or '6')
                    {
                        Logger.LogUpgradeOutput($"Skipping tar non-file entry: {rawPath} (type {typeFlag})");
                        await SkipTarEntryContentAsync(stream, contentBuffer, fileSize);
                        continue;
                    }

                    if (typeFlag == '5' || fileSize == 0)
                    {
                        Logger.LogUpgradeOutput($"Skipping tar directory: {rawPath}");
                        await SkipTarEntryPaddingAsync(stream, contentBuffer, fileSize);
                        continue;
                    }

                    var fileName = PathHelper.SanitizeTarEntryRelativePath(pendingPath ?? rawPath);
                    pendingPath = null;

                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        Logger.LogUpgradeOutput("Skipping tar entry with empty path after sanitization");
                        await SkipTarEntryContentAsync(stream, contentBuffer, fileSize);
                        continue;
                    }

                    var filePath = Path.Combine(outputDir, fileName);
                    var fullOutputDir = Path.GetFullPath(outputDir);
                    var fullFilePath = Path.GetFullPath(filePath);
                    if (!fullFilePath.StartsWith(fullOutputDir + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                        fullFilePath != fullOutputDir)
                    {
                        Logger.LogError($"Skipping file with suspicious path: {fileName}");
                        await SkipTarEntryContentAsync(stream, contentBuffer, fileSize);
                        continue;
                    }

                    if (fileSize > 0)
                    {
                        CurrentExtractEntry = Path.GetFileName(fileName);
                        Logger.LogUpgradeOutput($"Extracting: {fileName} ({fileSize} bytes)");

                        var directory = Path.GetDirectoryName(filePath);
                        if (!string.IsNullOrEmpty(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }

                        var allowProcessKill = !filePath.Contains(
                            $"{Path.DirectorySeparatorChar}pending-update{Path.DirectorySeparatorChar}",
                            StringComparison.OrdinalIgnoreCase);

                        int retry = 0;
                        while (true)
                        {
                            try
                            {
                                long fileBytesWritten = 0;
                                using (var fs = File.Create(filePath))
                                {
                                    long remaining = fileSize;
                                    while (remaining > 0)
                                    {
                                        int toRead = (int)Math.Min(remaining, contentBuffer.Length);
                                        int read = await stream.ReadAsync(contentBuffer, 0, toRead);
                                        if (read == 0)
                                        {
                                            throw new IOException(
                                                $"Unexpected end of archive while extracting {fileName} " +
                                                $"({fileBytesWritten}/{fileSize} bytes written)");
                                        }

                                        await fs.WriteAsync(contentBuffer, 0, read);
                                        fileBytesWritten += read;
                                        remaining -= read;

                                        var filePercent = (float)fileBytesWritten / fileSize * 100f;
                                        ReportExtractProgress(
                                            onProgress,
                                            ref lastReportMs,
                                            fileBytesWritten,
                                            fileSize,
                                            filePercent);
                                    }
                                }

                                break;
                            }
                            catch (Exception e)
                            {
                                Logger.LogError($"Extract write error (attempt {retry + 1}): {fileName}", e);
                                await Task.Delay(TimeSpan.FromMilliseconds(250));
                                retry++;
                                if (allowProcessKill && retry > 2)
                                {
                                    try
                                    {
                                        var processName = GetFileProcessName(filePath);
                                        if (processName != null)
                                        {
                                            var p = Process.GetProcessesByName(processName).FirstOrDefault();
                                            p?.Kill();
                                        }
                                    }
                                    catch (Exception killError)
                                    {
                                        Logger.LogError("kill process error", killError);
                                    }
                                }

                                if (retry > 8)
                                {
                                    throw;
                                }
                            }
                        }
                    }

                    await SkipTarEntryPaddingAsync(stream, contentBuffer, fileSize);
                }

                Logger.LogUpgradeOutput($"Tar reader processed {entriesProcessed} header(s)");
            }
            finally
            {
                CurrentExtractEntry = null;
                if (onProgress != null)
                {
                    Dispatcher.UIThread.Post(() => onProgress(1, 1, 100f));
                }
            }
        }

        private static bool CheckVersion(UpdateInfo? lastVersion, string? currentfile)
        {
            if (lastVersion == null)
            {
                return false;
            }
            if (lastVersion != null && !string.IsNullOrWhiteSpace(currentfile))
            {
                var fileInfo = new FileInfo(currentfile);
                var latestVersion = GetVersionFromFileName(currentfile);
                if (latestVersion != null && !string.IsNullOrWhiteSpace(lastVersion.Version)
                    && Version.Parse(lastVersion.Version) < Version.Parse(latestVersion))
                {
                    return false;
                }

                if (lastVersion.Modified < fileInfo.LastWriteTimeUtc)
                {
                    return false;
                }
            }

            return true;
        }

        public static string? GetVersionFromFileName(string filePath) =>
            PathHelper.TryParseVersionFromPackageFileName(filePath);

        /// <summary>
        /// Pending legacy app download package in the updater install folder (excludes updater self-update archives).
        /// </summary>
        private static string? GetCurrentFile()
        {
            return Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.tar.gz")
                .Where(path => !PathHelper.IsStaleUpdaterDownloadPackage(path))
                .OrderByDescending(path => new FileInfo(path).LastWriteTimeUtc)
                .FirstOrDefault();
        }

        /// <summary>
        /// Removes leftover updater self-update download archives from the install directory.
        /// </summary>
        public static void CleanupStaleDownloadPackages(string? exceptPackagePath = null)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string? exceptFullPath = null;
            if (!string.IsNullOrWhiteSpace(exceptPackagePath))
            {
                try
                {
                    exceptFullPath = Path.GetFullPath(exceptPackagePath);
                }
                catch (ArgumentException)
                {
                    exceptFullPath = exceptPackagePath;
                }
            }

            foreach (var path in Directory.EnumerateFiles(baseDir, "*.tar.gz"))
            {
                if (exceptFullPath != null)
                {
                    try
                    {
                        if (string.Equals(Path.GetFullPath(path), exceptFullPath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // ignore invalid paths
                    }
                }

                if (!PathHelper.IsStaleUpdaterDownloadPackage(path))
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                    Logger.LogUpgradeOutput($"Removed stale download package: {Path.GetFileName(path)}");
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to remove stale download package: {Path.GetFileName(path)}", ex);
                }
            }
        }

        /// <summary>
        /// Installed app version for update checks — from persisted settings only, not download tarballs.
        /// </summary>
        private static (string? version, DateTimeOffset? modified, string? checksum) GetCurrentVersionInfo()
        {
            var lastVersion = Settings.Default.LastVersion;
            if (lastVersion != null && !string.IsNullOrWhiteSpace(lastVersion.Version))
            {
                return (lastVersion.Version, lastVersion.Modified, null);
            }

            return (null, null, null);
        }

        /// <summary>
        /// Gets the current version string for display purposes.
        /// Returns "Unknown" if no version can be determined.
        /// </summary>
        public static string GetCurrentVersionString()
        {
            var (version, _, _) = GetCurrentVersionInfo();
            return version ?? "Unknown";
        }

        public static string GetFileProcessName(string filePath)
        {
            if (OperatingSystem.IsLinux())
            {
                string fileName = Path.GetFileName(filePath);

                return fileName;
            }
            else
            {
                Process[] procs = Process.GetProcesses();
                string fileName = Path.GetFileName(filePath);

                foreach (Process proc in procs)
                {
                    if (proc.MainWindowHandle != new IntPtr(0) && !proc.HasExited)
                    {
                        ProcessModule[] arr = new ProcessModule[proc.Modules.Count];

                        foreach (ProcessModule pm in proc.Modules)
                        {
                            if (pm.ModuleName == fileName)
                                return proc.ProcessName;
                        }
                    }
                }
            }

            return null;
        }

        public static string GetUpdaterVersion()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                if (version != null)
                {
                    return $"{version.Major}.{version.Minor}.{version.Build}";
                }
            }
            catch
            {
            }
            return "1.0.0";
        }

        /// <summary>
        /// Directory containing the running updater (publish output / install root).
        /// </summary>
        public static string GetUpdaterInstallDirectory()
        {
            return Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        }

        private static void Chmod(string path, string permissions)
        {
            try
            {
                using (var process = Process.Start("chmod", $"{permissions} \"{path}\""))
                {
                    process.WaitForExit();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to chmod {path} to {permissions}", ex);
            }
        }
    }
}
