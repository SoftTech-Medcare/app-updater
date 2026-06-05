using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Updater.Services;

namespace Updater.Utils
{
    public static class Logger
    {

        public const string error_log_filename = "error.log";
        public const string upgrade_log_filename = "upgrade.log";
        public const string upgrade_json_log_filename = "upgrade-history.json";

        private static UpgradeSession? _currentSession;
        private static readonly object _sessionLock = new object();

        public static void LogError(string msg, Exception? e = null)
        {
            try
            {
                var path = CheckAndReturnFile(error_log_filename);

                string errorTxt = $"{DateTimeOffset.Now}: {msg} || {e?.Message ?? "-"}";
                if (e?.InnerException != null)
                {
                    errorTxt += $" || {e.InnerException}";
                }
                if (!string.IsNullOrWhiteSpace(e?.StackTrace))
                {
                    errorTxt += $"\n{e?.StackTrace}";
                }
                errorTxt += "\n\n";

                File.AppendAllText(path, errorTxt);
            }
            catch
            {
                // Install dir may be read-only; never let logging mask the original failure.
            }
        }

        public static void LogInfo(string msg)
        {
            Console.WriteLine(msg);
        }

        /// <summary>
        /// Logs output to upgrade.log file (captures all process output)
        /// </summary>
        public static void LogUpgradeOutput(string message)
        {
            var path = GetUpgradeLogPath();
            var logEntry = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}: {message}\n";
            File.AppendAllText(path, logEntry);
        }

        /// <summary>
        /// Starts a new upgrade session for structured JSON logging
        /// </summary>
        public static void StartUpgradeSession(string? fromVersion = null, string? toVersion = null, long? packageSize = null)
        {
            lock (_sessionLock)
            {
                _currentSession = new UpgradeSession
                {
                    StartTime = DateTimeOffset.Now,
                    FromVersion = fromVersion,
                    ToVersion = toVersion,
                    PackageSize = packageSize,
                    OverallStatus = UpgradeStatus.Started
                };
                LogUpgradeOutput($"=== Upgrade Session Started ===\nFrom: {fromVersion ?? "unknown"}\nTo: {toVersion ?? "unknown"}\nPackage Size: {packageSize ?? 0} bytes");
            }
        }

        /// <summary>
        /// Logs an upgrade event to structured JSON log
        /// </summary>
        public static void LogUpgradeEvent(UpgradeLog logEntry)
        {
            lock (_sessionLock)
            {
                if (_currentSession == null)
                {
                    // Auto-start session if not started
                    StartUpgradeSession();
                }

                _currentSession.Upgrades.Add(logEntry);
                SaveUpgradeSession();
            }

            // Also log to upgrade.log
            var message = $"[{logEntry.Stage}] {logEntry.UpgradeName ?? logEntry.UpgradeId ?? "Unknown"}: {logEntry.Message ?? logEntry.Status.ToString()}";
            if (!string.IsNullOrEmpty(logEntry.Error))
            {
                message += $" - ERROR: {logEntry.Error}";
            }
            LogUpgradeOutput(message);
        }

        /// <summary>
        /// Ends the current upgrade session
        /// </summary>
        public static void EndUpgradeSession(UpgradeStatus status, string? error = null)
        {
            lock (_sessionLock)
            {
                if (_currentSession == null) return;

                _currentSession.EndTime = DateTimeOffset.Now;
                _currentSession.OverallStatus = status;
                _currentSession.Error = error;
                SaveUpgradeSession();
                
                var duration = _currentSession.EndTime.Value - _currentSession.StartTime;
                LogUpgradeOutput($"=== Upgrade Session Ended ===\nStatus: {status}\nDuration: {duration.TotalSeconds:F2} seconds");
                if (!string.IsNullOrEmpty(error))
                {
                    LogUpgradeOutput($"Error: {error}");
                }

                _currentSession = null;
            }
        }

        private static void SaveUpgradeSession()
        {
            if (_currentSession == null) return;

            var path = GetUpgradeJsonLogPath();
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            // Read existing sessions
            var sessions = new List<UpgradeSession>();
            if (File.Exists(path))
            {
                try
                {
                    var existingJson = File.ReadAllText(path);
                    var existingSessions = JsonSerializer.Deserialize<List<UpgradeSession>>(existingJson, options);
                    if (existingSessions != null)
                    {
                        sessions = existingSessions;
                    }
                }
                catch
                {
                    // If we can't read existing file, start fresh
                }
            }

            // Update or add current session
            var existingIndex = sessions.FindIndex(s => s.SessionId == _currentSession.SessionId);
            if (existingIndex >= 0)
            {
                sessions[existingIndex] = _currentSession;
            }
            else
            {
                sessions.Add(_currentSession);
            }

            // Keep only last 50 sessions to prevent file from growing too large
            if (sessions.Count > 50)
            {
                sessions = sessions.OrderByDescending(s => s.StartTime).Take(50).ToList();
            }

            // Write back
            var json = JsonSerializer.Serialize(sessions, options);
            File.WriteAllText(path, json);
        }

        private static string GetUpgradeLogPath()
        {
            return CheckAndReturnFile(upgrade_log_filename);
        }

        private static string GetUpgradeJsonLogPath()
        {
            var basePath = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(basePath, upgrade_json_log_filename);
        }

        private static string CheckAndReturnFile(string filename, int increment = 0)
        {
            string file = filename;
            if (increment > 0)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var ex = Path.GetExtension(file);
                file = $"{name}-{increment}{ex}";
            }

            var basePath = AppDomain.CurrentDomain.BaseDirectory;
            var path = Path.Combine(basePath, file);
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length >= 1024 * 1024 * 2)
                {
                    return CheckAndReturnFile(filename, increment + 1);
                }
            }

            return path;
        }
    }
}
