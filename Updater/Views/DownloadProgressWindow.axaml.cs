using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using ReactiveUI;
using Updater.ViewModels;
using System;
using Updater.Utils;
using Avalonia.Threading;
using Updater.Properties;
using Updater.Services;
using UpdaterLib;

namespace Updater.Views
{
    public partial class DownloadProgressWindow : ReactiveWindow<DownloadProgressViewModel>
    {
        public DownloadProgressWindow()
        {
            InitializeComponent();
#if DEBUG
            this.AttachDevTools();
#endif
            this.WhenActivated(d => d(ViewModel!.StartDownload().Subscribe(OnDownloadCompleted)));
        }

        private void OnDownloadCompleted(UpgradeStepResult downloadResult)
        {
            if (!downloadResult.Success)
            {
                return;
            }

            ViewModel!.StartExtract(downloadResult.PackagePath!).Subscribe(OnExtractCompleted);
        }

        private void OnExtractCompleted(UpgradeStepResult extractResult)
        {
            if (!extractResult.Success)
            {
                return;
            }

            ViewModel!.CleanAndFinish(extractResult).Subscribe(OnCleanupCompleted);
        }

        private void OnCleanupCompleted(UpgradeStepResult cleanupResult)
        {
            if (!cleanupResult.Success)
            {
                return;
            }

            OnUpgradePipelineFinished();
        }

        private void OnUpgradePipelineFinished()
        {
            Dispatcher.UIThread.Post(() =>
            {
                ViewModel?.MarkUpgradeComplete();

                Console.Out.WriteLine("!!Finish!!");

                if (Settings.Default.AutoReboot)
                {
                    if (OperatingSystem.IsLinux())
                    {
                        "sudo reboot".Cmd();
                    }
                    else if (OperatingSystem.IsMacOS())
                    {
                        "sudo shutdown -r now".Cmd();
                    }
                    else
                    {
                        "shutdown /r /t:0".Cmd();
                    }

                    return;
                }

                if (UpdateService.SelfUpdateOnlyMode)
                {
                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    {
                        desktop.Shutdown();
                    }

                    Environment.Exit(0);
                    return;
                }

                Close();
            });
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
