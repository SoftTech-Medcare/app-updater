using System;
using System.Text.RegularExpressions;
using Updater.Properties;

namespace Updater.Services
{
    public class SettingService
    {
        public void SetByArgs(string[] args)
        {
            string? key = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (i == 0)
                {
                    if (!IsSettingCommand(args[i]))
                    {
                        throw new Exception("Invalid arg format.");
                    }
                    continue;
                }
                var arg = args[i];
                var split = arg.Split("=");
                if (split.Length > 1)
                {
                    Set(split[0], split[1]);
                }
                else
                {
                    if (key == null)
                    {
                        key = arg;
                        continue;
                    }
                    else
                    {
                        Set(key, arg);
                        key = null;
                    }
                }
            }

            if (key != null)
            {
                throw new Exception("Invalid arg format.");
            }
        }

        public void Set(string key, string value)
        {
            switch (key)
            {
                case nameof(Settings.Default.UpdateServer):
                    Settings.Default.UpdateServer = string.IsNullOrWhiteSpace(value)
                        ? ""
                        : (Regex.IsMatch(value, @"^https?://") ? value : "http://" + value);
                    break;
                case nameof(Settings.Default.AppName):
                    Settings.Default.AppName = value;
                    break;
                case nameof(Settings.Default.ClientAppPath):
                    Settings.Default.ClientAppPath = value;
                    break;
                case nameof(Settings.Default.AutoReboot):
                    Settings.Default.AutoReboot = Convert.ToBoolean(value);
                    break;
                case nameof(Settings.Default.ProgressFullscreen):
                    Settings.Default.ProgressFullscreen = Convert.ToBoolean(value);
                    break;
                case nameof(Settings.Default.EnablePreReleaseVersions):
                    Settings.Default.EnablePreReleaseVersions = Convert.ToBoolean(value);
                    break;
                default:
                    throw new Exception($"Unknown setting: {key}");
            }

            Settings.Default.Save();
        }

        public bool IsSettingCommand(string arg)
        {
            var normalized = arg.Replace("-", "").ToLower();
            return normalized == "set" || normalized == "s";
        }
    }
}
