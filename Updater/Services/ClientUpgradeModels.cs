using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Updater.Services
{
    public class CheckUpgradeRequest
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("modified")]
        public DateTimeOffset? Modified { get; set; }

        [JsonPropertyName("checksum")]
        public string? Checksum { get; set; }

        /// <summary>Only used for legacy /check to distinguish app-only vs combined; omitted on check-upgrades.</summary>
        [JsonPropertyName("includeSelfUpdate")]
        public bool? IncludeSelfUpdate { get; set; }
    }

    public class SelfUpdateCheckInfo
    {
        [JsonPropertyName("available")]
        public bool Available { get; set; }

        [JsonPropertyName("currentVersion")]
        public string? CurrentVersion { get; set; }

        [JsonPropertyName("targetVersion")]
        public string? TargetVersion { get; set; }

        [JsonPropertyName("packageFile")]
        public string? PackageFile { get; set; }
    }

    public class UpgradeInfoWrapper
    {
        [JsonPropertyName("currentVersion")]
        public string? CurrentVersion { get; set; }

        [JsonPropertyName("targetVersion")]
        public string? TargetVersion { get; set; }

        [JsonPropertyName("upgrades")]
        public List<UpgradeSummary>? Upgrades { get; set; }

        [JsonPropertyName("packageSize")]
        public long PackageSize { get; set; }

        [JsonPropertyName("requiresDownload")]
        public bool RequiresDownload { get; set; }

        [JsonPropertyName("selfUpdate")]
        public SelfUpdateCheckInfo? SelfUpdate { get; set; }
    }

    public class UpgradeSummary
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("priority")]
        public int Priority { get; set; }
    }

    public class UpgradePackageManifest
    {
        [JsonPropertyName("fromVersion")]
        public string? FromVersion { get; set; }

        [JsonPropertyName("toVersion")]
        public string? ToVersion { get; set; }

        [JsonPropertyName("upgrades")]
        public List<string>? Upgrades { get; set; }
    }

    public class UpgradeManifest
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("files")]
        public List<UpgradeFileParams> Files { get; set; } = new List<UpgradeFileParams>();

        [JsonPropertyName("preInstallScript")]
        public string? PreInstallScript { get; set; }

        [JsonPropertyName("postInstallScript")]
        public string? PostInstallScript { get; set; }
    }



    public class UpgradeFileParams
    {
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("target")]
        public string? Target { get; set; }

        [JsonPropertyName("permissions")]
        public string? Permissions { get; set; }

        [JsonPropertyName("required")]
        public bool IsRequired { get; set; }

        [JsonPropertyName("executable")]
        public bool IsExecutable { get; set; }

        [JsonPropertyName("explode")]
        public bool Explode { get; set; }

        [JsonPropertyName("runOrder")]
        public int RunOrder { get; set; }

        [JsonPropertyName("checksum")]
        public string? Checksum { get; set; }
    }
}
