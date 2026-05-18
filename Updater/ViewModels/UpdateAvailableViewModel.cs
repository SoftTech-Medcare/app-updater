using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using ReactiveUI;
using System;
using System.Reactive;
using Updater.Properties;
using Updater.Views;

namespace Updater.ViewModels
{
    public class UpdateAvailableViewModel : ViewModelBase
    {
        private readonly string currentVersion;
        private readonly string latestVersion;
        private readonly string appStatusLine;
        private readonly string selfUpdateLine;

        public ReactiveCommand<Unit, Unit> Confirm { get; }
        public ReactiveCommand<Unit, Unit> Cancel { get; }

        public UpdateAvailableViewModel(
            string currentVersion,
            string latestVersion,
            bool applicationAlreadyUpToDate,
            string selfUpdateLine)
        {
            this.currentVersion = currentVersion;
            this.latestVersion = latestVersion;
            this.selfUpdateLine = selfUpdateLine?.Trim() ?? "";

            appStatusLine = applicationAlreadyUpToDate
                ? "Application is already on the latest version."
                : "";

            Confirm = ReactiveCommand.Create(() =>
            {
                // 2nd output : Use this output code to detect it in the target app, eg. to automatically close the running instance
                Console.Out.WriteLine("!!Update!!");

                var desktop = (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!;
                var current = desktop.MainWindow;

                desktop.MainWindow = new DownloadProgressWindow();
                desktop.MainWindow.DataContext = new DownloadProgressViewModel();
                desktop.MainWindow.Topmost = true;
                desktop.MainWindow.Show();

                if (Settings.Default.ProgressFullscreen)
                {
                    desktop.MainWindow.WindowState = Avalonia.Controls.WindowState.FullScreen;
                }

                current.Close();
            });

            Cancel = ReactiveCommand.Create(() => { });
        }

        public string CurrentVersion => currentVersion;
        public string LatestVersion => latestVersion;
        public string AppStatusLine => appStatusLine;
        public bool HasAppStatusLine => !string.IsNullOrWhiteSpace(appStatusLine);
        public string SelfUpdateLine => selfUpdateLine;
        public bool HasSelfUpdateLine => !string.IsNullOrWhiteSpace(selfUpdateLine);
    }
}
