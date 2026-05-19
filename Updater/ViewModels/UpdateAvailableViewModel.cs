using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ReactiveUI;
using System;
using System.Reactive;
using Updater.Properties;
using Updater.Services;
using Updater.Utils;
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

            Confirm = ReactiveCommand.CreateFromTask(async () =>
            {
                await Dispatcher.UIThread.InvokeAsync(StartDownloadFlowOnUiThread);
            });

            Cancel = ReactiveCommand.Create(() => { });
        }

        private void StartDownloadFlowOnUiThread()
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            {
                return;
            }

            try
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                var previous = desktop.MainWindow;
                var progressVm = new DownloadProgressViewModel();
                var progressWindow = new DownloadProgressWindow(progressVm)
                {
                    Topmost = true,
                    WindowStartupLocation = previous?.WindowStartupLocation ?? WindowStartupLocation.CenterScreen
                };

                desktop.MainWindow = progressWindow;
                progressWindow.Show();
                progressWindow.Activate();

                if (Settings.Default.ProgressFullscreen)
                {
                    progressWindow.WindowState = WindowState.FullScreen;
                }

                // Signal HemoBox before hiding the prompt (closing it can exit the app on Linux).
                Console.Out.WriteLine("!!Update!!");
                Console.Out.Flush();

                if (previous is Window oldWindow && !ReferenceEquals(oldWindow, progressWindow))
                {
                    oldWindow.Hide();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to open download window", ex);
                Console.WriteLine($"Update flow error: {ex.Message}");
                throw;
            }
        }

        public string CurrentVersion => currentVersion;
        public string LatestVersion => latestVersion;
        public string AppStatusLine => appStatusLine;
        public bool HasAppStatusLine => !string.IsNullOrWhiteSpace(appStatusLine);
        public string SelfUpdateLine => selfUpdateLine;
        public bool HasSelfUpdateLine => !string.IsNullOrWhiteSpace(selfUpdateLine);
    }
}
