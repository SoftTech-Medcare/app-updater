using Avalonia;
using Avalonia.Controls;
#if DEBUG
using Avalonia.Diagnostics;
#endif
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using ReactiveUI;
using System.Windows.Input;
using Updater.ViewModels;
using System;
using Updater.Utils;

namespace Updater.Views
{
    public partial class AlertWindow : ReactiveWindow<AlertViewModel>
    {
        

        public AlertWindow()
        {
            InitializeComponent();
#if DEBUG
            this.AttachDevTools();
#endif
            this.WhenActivated(d => d(ViewModel!.Ok.Subscribe((_)=> Close())));
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void Show()
        {
            base.Show();

            this.SetWindowStartupLocationWorkaround();
        }
    }
}
