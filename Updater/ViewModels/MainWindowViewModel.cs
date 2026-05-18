using Avalonia.Controls;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Updater.Properties;
using Updater.Services;

namespace Updater.ViewModels
{
    public class MainWindowViewModel : ViewModelBase
    {
        private string appPath;
        private string server;
        private string appName;
        private string updaterPackageAppName;
        private bool isFullscreen;
        private bool autoReboot;
        private bool enablePreReleaseVersions;
        private bool includeSelfUpdateInCheck;

        public ICommand SaveConfig { get; }
        public ICommand Find { get; }
        public ReactiveCommand<Unit, Unit> Close { get; }

        public MainWindowViewModel()
        {
            FindFolder = new Interaction<Unit, string?>();

            AppPath = Settings.Default.ClientAppPath;
            Server = Settings.Default.UpdateServer;
            AppName = Settings.Default.AppName;
            UpdaterPackageAppName = Settings.Default.UpdaterPackageAppName;
            IsFullscreen = Settings.Default.ProgressFullscreen;
            AutoReboot = Settings.Default.AutoReboot;
            EnablePreReleaseVersions = Settings.Default.EnablePreReleaseVersions;
            IncludeSelfUpdateInCheck = Settings.Default.IncludeSelfUpdateInCheck;

            SaveConfig = ReactiveCommand.Create(() =>
            {

                Settings.Default.ClientAppPath = AppPath;
                Settings.Default.UpdateServer = string.IsNullOrWhiteSpace(Server) ? "" : (Regex.IsMatch(Server, @"^https?://") ? Server : "http://" + Server);
                Settings.Default.AppName = AppName;
                Settings.Default.UpdaterPackageAppName = string.IsNullOrWhiteSpace(UpdaterPackageAppName) ? "Updater" : UpdaterPackageAppName.Trim();
                Settings.Default.ProgressFullscreen = IsFullscreen;
                Settings.Default.AutoReboot = AutoReboot;
                Settings.Default.EnablePreReleaseVersions = EnablePreReleaseVersions;
                Settings.Default.IncludeSelfUpdateInCheck = IncludeSelfUpdateInCheck;

                Settings.Default.Save();
                Settings.Default.Reload();

                Server = Settings.Default.UpdateServer;
            });

            Find = ReactiveCommand.CreateFromTask(async () =>
            {
                var result = await FindFolder.Handle(Unit.Default);
                if (result != null)
                {
                    AppPath = result;
                }
            });

            Close = ReactiveCommand.Create(() => { });
        }

        public string AppPath { get => appPath; set => this.RaiseAndSetIfChanged(ref appPath, value); }
        public string AppName { get => appName; set => this.RaiseAndSetIfChanged(ref appName, value); }
        public string UpdaterPackageAppName { get => updaterPackageAppName; set => this.RaiseAndSetIfChanged(ref updaterPackageAppName, value); }
        public string Server { get => server; set => this.RaiseAndSetIfChanged(ref server, value); }
        public bool IsFullscreen { get => isFullscreen; set => this.RaiseAndSetIfChanged(ref isFullscreen, value); }
        public bool AutoReboot { get => autoReboot; set => this.RaiseAndSetIfChanged(ref autoReboot, value); }
        public bool EnablePreReleaseVersions { get => enablePreReleaseVersions; set => this.RaiseAndSetIfChanged(ref enablePreReleaseVersions, value); }
        public bool IncludeSelfUpdateInCheck { get => includeSelfUpdateInCheck; set => this.RaiseAndSetIfChanged(ref includeSelfUpdateInCheck, value); }

        public string UpdaterVersion => UpdateService.GetUpdaterVersion();

        public Interaction<Unit, string?> FindFolder { get; }
    }
}
