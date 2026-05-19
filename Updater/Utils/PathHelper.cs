using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Updater.Utils
{
    internal static class PathHelper
    {
        private static readonly Regex VersionToken = new(
            @"(\d+\.\d+(?:\.\d+)?(?:\.\d+)?(?:-[a-zA-Z0-9.]+)?)",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// Makes a single path segment safe for directory/file names (version labels, etc.).
        /// </summary>
        public static string SanitizePathSegment(string? value, string fallback = "unknown")
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            var trimmed = value.Trim();

            // Upgrade display names look like "Updater Self-Update 2.0.11" — keep the version token only.
            var match = VersionToken.Match(trimmed);
            if (match.Success && trimmed.Contains(' ', StringComparison.Ordinal))
            {
                trimmed = match.Groups[1].Value;
            }

            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string(trimmed.Select(c => invalid.Contains(c) ? '_' : c).ToArray())
                .Trim('.', ' ');

            return string.IsNullOrEmpty(sanitized) ? fallback : sanitized;
        }

        /// <summary>
        /// Download archives left in the updater install directory (not installed app version markers).
        /// </summary>
        public static bool IsStaleUpdaterDownloadPackage(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            var name = Path.GetFileName(fileName);
            if (name.Equals("unknown.gz", StringComparison.OrdinalIgnoreCase)
                || name.Equals("updater-update.tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return name.StartsWith("updater-", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Strips .tar.gz / .tgz / trailing .tar from a package file name.</summary>
        public static string GetPackageBaseNameWithoutExtension(string fileName)
        {
            var name = Path.GetFileName(fileName);
            if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^7];
            }
            else if (name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4];
            }
            else
            {
                name = Path.GetFileNameWithoutExtension(name);
            }

            if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4];
            }

            return name;
        }

        /// <summary>Parses a version token from update file names (e.g. Box-2.0.13, update-2.0.13).</summary>
        public static string? TryParseVersionFromPackageFileName(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return null;
            }

            var baseName = GetPackageBaseNameWithoutExtension(Path.GetFileName(filePath));
            if (string.IsNullOrEmpty(baseName))
            {
                return null;
            }

            var splits = baseName.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (splits.Length > 1)
            {
                var candidate = splits[^1];
                if (System.Version.TryParse(candidate, out _))
                {
                    return candidate;
                }
            }

            var match = VersionToken.Match(baseName);
            return match.Success ? match.Groups[1].Value : null;
        }

        public static string SanitizeDownloadFileName(string? fileName, string fallback = "download.tar.gz")
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return fallback;
            }

            var baseName = Path.GetFileName(fileName.Trim().Trim('"', '\''));
            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string(baseName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());

            return string.IsNullOrEmpty(sanitized) ? fallback : sanitized;
        }

        /// <summary>
        /// Path for the decompressed .tar file inside a .tar.gz (GetFileNameWithoutExtension only strips .gz).
        /// </summary>
        public static string GetTarArchiveIntermediatePath(string destinationDir, string archivePath)
        {
            var name = Path.GetFileName(archivePath);
            if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^7];
            }
            else if (name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4];
            }
            else
            {
                name = Path.GetFileNameWithoutExtension(name);
            }

            return Path.Combine(destinationDir, name);
        }

        /// <summary>
        /// Normalizes tar entry paths and removes characters invalid on the current OS.
        /// </summary>
        public static string SanitizeTarEntryRelativePath(string fileName)
        {
            fileName = fileName.Replace('\\', '/').TrimStart('/');
            while (fileName.StartsWith("./", StringComparison.Ordinal))
            {
                fileName = fileName[2..];
            }
            var invalid = Path.GetInvalidFileNameChars();
            var segments = fileName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var safeSegments = segments.Select(s =>
            {
                var trimmed = s.Trim();
                return new string(trimmed.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            }).Where(s => !string.IsNullOrEmpty(s));

            return string.Join(Path.DirectorySeparatorChar, safeSegments);
        }

        /// <summary>Reads a ustar 12-byte size field (octal ASCII per POSIX).</summary>
        public static bool TryParseTarSizeField(string sizeField, out long fileSize)
        {
            fileSize = 0;
            var trimmed = sizeField.TrimEnd('\0', ' ').Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return true;
            }

            try
            {
                fileSize = Convert.ToInt64(trimmed, 8);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void ValidateWritablePath(string path, string paramName)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Path is empty.", paramName);
            }

            // Throws ArgumentException when illegal characters are present.
            _ = Path.GetFullPath(path);
        }
    }
}
