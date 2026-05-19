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
        private bool pipelineStarted;

        public DownloadProgressWindow()
            : this(new DownloadProgressViewModel())
        {
        }

        public DownloadProgressWindow(DownloadProgressViewModel viewModel)
        {
            DataContext = viewModel;
            InitializeComponent();
#if DEBUG
            this.AttachDevTools();
#endif
            Opened += OnWindowOpened;
        }

        private void OnWindowOpened(object? sender, EventArgs e)
        {
            StartUpgradePipelineIfNeeded();
        }

        private void StartUpgradePipelineIfNeeded()
        {
            if (pipelineStarted || ViewModel == null)
            {
                return;
            }

            pipelineStarted = true;
            ViewModel.StartDownload().Subscribe(
                OnDownloadCompleted,
                ex =>
                {
                    Logger.LogError("Download pipeline error", ex);
                    ViewModel?.MarkPipelineFailed("Download", ex.Message);
                });
        }

        private void OnDownloadCompleted(UpgradeStepResult downloadResult)
        {
            if (!downloadResult.Success)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                ViewModel?.StartExtract(downloadResult.PackagePath!).Subscribe(
                OnExtractCompleted,
                ex => ViewModel?.MarkPipelineFailed("Installation", ex.Message));
            });
        }

        private void OnExtractCompleted(UpgradeStepResult extractResult)
        {
            if (!extractResult.Success)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                ViewModel?.CleanAndFinish(extractResult).Subscribe(
                    OnCleanupCompleted,
                    ex => ViewModel?.MarkPipelineFailed("Cleanup", ex.Message));
            });
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
                Console.Out.Flush();

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
