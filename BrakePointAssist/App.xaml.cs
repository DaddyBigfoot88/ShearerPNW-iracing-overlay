using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using ShearerPNW.iRacingOverlay.Core;

namespace ShearerPNW.iRacingOverlay
{
    public partial class App : Application
    {
        public static OverlayManager OverlayManager { get; private set; }
        public static TelemetryEngine TelemetryEngine { get; private set; }
        public static PluginSystem PluginSystem { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Set up global exception handling
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            try
            {
                // Initialize core systems
                InitializeCoreServices();
                
                // Initialize data directory
                EnsureDataDirectories();
                
                base.OnStartup(e);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start ShearerPNW iRacing Overlay:\n\n{ex.Message}", 
                    "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        private void InitializeCoreServices()
        {
            // Initialize telemetry engine
            TelemetryEngine = new TelemetryEngine();
            
            // Initialize plugin system
            PluginSystem = new PluginSystem();
            
            // Initialize overlay manager
            OverlayManager = new OverlayManager(TelemetryEngine, PluginSystem);
            
            // Load plugins
            PluginSystem.LoadPlugins();
        }

        private void EnsureDataDirectories()
        {
            var directories = new[]
            {
                "Data",
                "Data/Config",
                "Data/Tracks", 
                "Data/BrakePoints",
                "Data/Setups",
                "Data/Layouts",
                "Data/Plugins",
                "Data/Logs"
            };

            foreach (var dir in directories)
            {
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                // Gracefully shut down core systems
                OverlayManager?.Dispose();
                TelemetryEngine?.Dispose();
                PluginSystem?.Dispose();
            }
            catch (Exception ex)
            {
                // Log shutdown errors but don't prevent exit
                File.WriteAllText($"Data/Logs/shutdown_error_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log", ex.ToString());
            }

            base.OnExit(e);
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            var errorMessage = $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nThe application will continue running.";
            
            MessageBox.Show(errorMessage, 
                "ShearerPNW iRacing Overlay - Error", 
                MessageBoxButton.OK, 
                MessageBoxImage.Warning);
            
            // Log the error
            LogError(e.Exception, "DispatcherUnhandledException");
            
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception;
            var errorMessage = $"A critical error occurred:\n\n{ex?.Message ?? "Unknown error"}\n\nThe application will now exit.";
            
            MessageBox.Show(errorMessage, 
                "ShearerPNW iRacing Overlay - Critical Error", 
                MessageBoxButton.OK, 
                MessageBoxImage.Error);
                
            // Log the error
            LogError(ex, "UnhandledException");
        }

        private void LogError(Exception exception, string source)
        {
            try
            {
                var logPath = $"Data/Logs/error_{DateTime.Now:yyyy-MM-dd}.log";
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {exception}\n\n";
                File.AppendAllText(logPath, logEntry);
            }
            catch
            {
                // If we can't log, don't throw another exception
            }
        }
    }
}
