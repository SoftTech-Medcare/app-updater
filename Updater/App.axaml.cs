using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
using System.Linq;
using System.Threading.Tasks;
using Updater.Properties;
using Updater.Services;
using Updater.Utils;
using Updater.ViewModels;
using Updater.Views;

namespace Updater
{
    public partial class App : Application
    {

        private bool error = false;
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Startup += Desktop_Startup;
            }

            base.OnFrameworkInitializationCompleted();
        }

        private async void Desktop_Startup(object? sender, ControlledApplicationLifetimeStartupEventArgs e)
        {
            string[] commanlineArgs = e.Args;

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                if (commanlineArgs.Length > 0)
                {
                    if (commanlineArgs.Any(x => x == "check" || x == "c"))
                    {
                        var service = new UpdateService();
                        bool upToDate;
                        try
                        {
                            upToDate = await service.CheckUpdate();
                        }
                        catch (Exception error)
                        {
                            Console.WriteLine($"error checking: {error.Message}");
                            throw;
                        }

                        // 1st output: true = fully up to date (no app or updater update). false = show update UI.
                        Console.Out.WriteLine(upToDate);

                        if (!upToDate && !error)
                        {
                            if (commanlineArgs.Any(x => x.Replace("-", "") == "force" || x.Replace("-", "") == "f"))
                            {
                                desktop.MainWindow = new DownloadProgressWindow();
                                desktop.MainWindow.DataContext = new DownloadProgressViewModel();
                                desktop.MainWindow.Topmost = true;
                                desktop.MainWindow.Show();

                                if (Settings.Default.ProgressFullscreen)
                                {
                                    desktop.MainWindow.WindowState = WindowState.FullScreen;
                                }
                            }
                            else
                            {
                                desktop.MainWindow = new UpdateAvailableWindow
                                {
                                    DataContext = new UpdateAvailableViewModel(
                                        currentVersion: GetCurrentVersion(),
                                        latestVersion: await GetLatestVersionInfo(service),
                                        applicationAlreadyUpToDate: UpdateService.SelfUpdateOnlyMode,
                                        selfUpdateLine: UpdateService.GetSelfUpdateSummaryLine())
                                };
                                desktop.MainWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;

                                desktop.MainWindow.Show();

                                desktop.MainWindow.Topmost = true;
                                desktop.MainWindow.SetAlwaysOnTop();
                            }
                        }
                        else
                        {
                            if (desktop.Windows.Count == 0)
                            {
                                Console.WriteLine("exiting...");
                                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                                desktop.Shutdown();

                                //not close? force requesting to exit!
                                Environment.Exit(0);
                            }
                        }
                    }
                }
                else
                {
                    desktop.MainWindow = new MainWindow
                    {
                        DataContext = new MainWindowViewModel(),
                    };
                }
            }
        }

        public static async Task ShowAlert(string msg)
        {
            (Current as App)!.error = true;
            var alert = new AlertWindow();
            alert.DataContext = new AlertViewModel(msg);

            var desktop = ((IClassicDesktopStyleApplicationLifetime)Current!.ApplicationLifetime!);
            if (desktop.MainWindow != null)
            {
                await alert.ShowDialog(desktop.MainWindow);
            }
            else
            {
                desktop.MainWindow = alert;

                desktop.MainWindow.Show();

                desktop.MainWindow.Topmost = true;
                desktop.MainWindow.SetAlwaysOnTop();
            }
        }

        private static string GetCurrentVersion()
        {
            return UpdateService.GetCurrentVersionString();
        }

        private static async Task<string> GetLatestVersionInfo(UpdateService service)
        {
            try
            {
                var latestInfo = await service.GetLatestVersionInfo();
                if (latestInfo != null)
                {
                    if (Settings.Default.EnablePreReleaseVersions && latestInfo.PreRelease != null)
                    {
                        return latestInfo.PreRelease.Version ?? "Unknown";
                    }
                    return latestInfo.Stable?.Version ?? "Unknown";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting latest version info: {ex.Message}");
            }
            return "Unknown";
        }
    }
}


