using System;
using System.IO;
using System.Text.Json;

namespace Updater.Utils
{
    /// <summary>
    /// Reads JSON manifest files produced by upgrade packaging / tar extract.
    /// Trims BOM and null padding and deserializes only the first JSON value so trailing garbage does not fail the parse.
    /// </summary>
    public static class JsonFileReader
    {
        public static JsonSerializerOptions DefaultOptions { get; } = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        public static T? Read<T>(string path, JsonSerializerOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return default;
            }

            var bytes = File.ReadAllBytes(path);
            return Deserialize<T>(path, bytes, options);
        }

        public static T? Deserialize<T>(string sourceLabel, ReadOnlySpan<byte> utf8Json, JsonSerializerOptions? options = null)
        {
            var span = TrimManifestBytes(utf8Json);
            if (span.IsEmpty)
            {
                return default;
            }

            options ??= DefaultOptions;

            try
            {
                var reader = new Utf8JsonReader(span, new JsonReaderOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

                if (!reader.Read())
                {
                    return default;
                }

                using var doc = JsonDocument.ParseValue(ref reader);
                return doc.RootElement.Deserialize<T>(options);
            }
            catch (JsonException ex)
            {
                Logger.LogUpgradeOutput(
                    $"JSON parse failed for {sourceLabel} ({span.Length} bytes): {ex.Message}");
                return default;
            }
        }

        /// <summary>Trim BOM, leading/trailing null bytes, and trailing whitespace after tar extract.</summary>
        internal static ReadOnlySpan<byte> TrimManifestBytes(ReadOnlySpan<byte> data)
        {
            var start = 0;
            var end = data.Length;

            if (end - start >= 3 && data[start] == 0xEF && data[start + 1] == 0xBB && data[start + 2] == 0xBF)
            {
                start += 3;
            }

            while (start < end && data[start] == 0)
            {
                start++;
            }

            while (end > start)
            {
                var b = data[end - 1];
                if (b == 0 || b == (byte)'\r' || b == (byte)'\n' || b == (byte)'\t' || b == (byte)' ')
                {
                    end--;
                }
                else
                {
                    break;
                }
            }

            return data.Slice(start, end - start);
        }
    }
}
