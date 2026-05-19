using Avalonia;
using Avalonia.Controls;
#if DEBUG
using Avalonia.Diagnostics;
#endif
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using ReactiveUI;
using Updater.ViewModels;
using System;
using System.ComponentModel;
using Updater.Utils;
using Avalonia.Controls.ApplicationLifetimes;

namespace Updater.Views
{
    public partial class UpdateAvailableWindow : ReactiveWindow<UpdateAvailableViewModel>
    {
        private bool exitRequested;

        public UpdateAvailableWindow()
        {
            InitializeComponent();
#if DEBUG
            this.AttachDevTools();
#endif
            Closing += OnClosing;
        }

        private void OnClosing(object? sender, CancelEventArgs e)
        {
            if (exitRequested)
            {
                return;
            }

            exitRequested = true;
            UpdateAvailableViewModel.ExitCancelled();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void Show()
        {
            var screenSize = Screens?.Primary?.Bounds;
            if (screenSize.HasValue)
            {
                Width = screenSize.Value.Width * 0.5f;
                Height = screenSize.Value.Height * 0.5f;
            }

            base.Show();

            this.SetWindowStartupLocationWorkaround();
        }
    }
}
