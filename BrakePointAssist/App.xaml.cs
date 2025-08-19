using System;
using System.Windows;
using System.Windows.Threading;

namespace BrakePointAssist
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // Set up global exception handling
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            base.OnStartup(e);
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show($"An unexpected error occurred:\n\n{e.Exception.Message}\n\nThe application will continue running.", 
                "ShearerPNW Brake Point Assistant - Error", 
                MessageBoxButton.OK, 
                MessageBoxImage.Warning);
            
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception;
            MessageBox.Show($"A critical error occurred:\n\n{ex?.Message ?? "Unknown error"}\n\nThe application will now exit.", 
                "ShearerPNW Brake Point Assistant - Critical Error", 
                MessageBoxButton.OK, 
                MessageBoxImage.Error);
        }
    }
}
