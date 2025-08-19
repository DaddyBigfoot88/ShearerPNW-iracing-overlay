using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using ShearerPNW.iRacingOverlay.Panels;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ShearerPNW.iRacingOverlay
{
    /// <summary>
    /// Main application class for ShearerPNW iRacing Overlay
    /// </summary>
    public partial class App : Application
    {
        private static Mutex _instanceMutex;
        private NotifyIcon _notifyIcon;
        private OverlayWindow _overlayWindow;
        private GlobalHotkeyManager _hotkeyManager;
        private bool _isExiting = false;

        [STAThread]
        public static void Main()
        {
            // Ensure single instance
            const string appName = "ShearerPNW_iRacing_Overlay";
            _instanceMutex = new Mutex(true, appName, out bool createdNew);

            if (!createdNew)
            {
                // Another instance is already running
                MessageBox.Show("ShearerPNW iRacing Overlay is already running.\n\nCheck your system tray for the overlay icon.",
                    "Already Running", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var app = new App();
                app.InitializeComponent();
                app.Run();
            }
            finally
            {
                _instanceMutex?.ReleaseMutex();
                _instanceMutex?.Dispose();
            }
        }

        private void InitializeComponent()
        {
            // Set up global exception handling
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            // Initialize application
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            
            // Setup system tray
            InitializeSystemTray();
            
            // Setup global hotkeys
            InitializeGlobalHotkeys();
            
            // Create overlay window
            CreateOverlayWindow();
        }

        private void InitializeSystemTray()
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = LoadApplicationIcon(),
                Text = "ShearerPNW iRacing Overlay",
                Visible = true
            };

            // Create context menu
            var contextMenu = new ContextMenuStrip();
            
            contextMenu.Items.Add("Show Overlay", null, (s, e) => ShowOverlay());
            contextMenu.Items.Add("Hide Overlay", null, (s, e) => HideOverlay());
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("Configuration", null, (s, e) => ShowConfiguration());
            contextMenu.Items.Add("Brake Point Assistant", null, (s, e) => ShowBrakePointAssistant());
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("About", null, (s, e) => ShowAbout());
            contextMenu.Items.Add("Exit", null, (s, e) => ExitApplication());

            _notifyIcon.ContextMenuStrip = contextMenu;
            _notifyIcon.DoubleClick += (s, e) => ToggleConfigurationMode();

            // Show startup notification
            _notifyIcon.ShowBalloonTip(3000, "ShearerPNW iRacing Overlay", 
                "Overlay started successfully!\nDouble-click icon to configure.", ToolTipIcon.Info);
        }

        private System.Drawing.Icon LoadApplicationIcon()
        {
            try
            {
                // Try to load from embedded resource or file
                var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
                if (File.Exists(iconPath))
                {
                    return new System.Drawing.Icon(iconPath);
                }
                
                // Fallback to system icon
                return SystemIcons.Application;
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        private void InitializeGlobalHotkeys()
        {
            _hotkeyManager = new GlobalHotkeyManager();
            
            // Register global hotkeys
            try
            {
                _hotkeyManager.RegisterHotkey(ModifierKeys.Control | ModifierKeys.Shift, Keys.F12, () =>
                {
                    Dispatcher.BeginInvoke(() => ToggleConfigurationMode());
                });

                _hotkeyManager.RegisterHotkey(ModifierKeys.Control | ModifierKeys.Shift, Keys.F1, () =>
                {
                    Dispatcher.BeginInvoke(() => _overlayWindow?.TogglePanel("LapCounter"));
                });

                _hotkeyManager.RegisterHotkey(ModifierKeys.Control | ModifierKeys.Shift, Keys.F2, () =>
                {
                    Dispatcher.BeginInvoke(() => _overlayWindow?.TogglePanel("Timing"));
                });

                _hotkeyManager.RegisterHotkey(ModifierKeys.Control | ModifierKeys.Shift, Keys.F3, () =>
                {
                    Dispatcher.BeginInvoke(() => _overlayWindow?.TogglePanel("CarCondition"));
                });

                _hotkeyManager.RegisterHotkey(ModifierKeys.Control | ModifierKeys.Shift, Keys.F4, () =>
                {
                    Dispatcher.BeginInvoke(() => _overlayWindow?.TogglePanel("RelativeTiming"));
                });
            }
            catch (Exception ex)
            {
                // Hotkey registration failed, but continue without global hotkeys
                Debug.WriteLine($"Failed to register global hotkeys: {ex.Message}");
            }
        }

        private void CreateOverlayWindow()
        {
            _overlayWindow = new OverlayWindow();
            
            // Register additional panels
            RegisterAdvancedPanels();
            
            // Show the overlay
            _overlayWindow.Show();
        }

        private void RegisterAdvancedPanels()
        {
            var overlayManager = _overlayWindow.GetOverlayManager();
            
            // Register advanced panels
            overlayManager.RegisterPanel(new RelativeTimingPanel());
            overlayManager.RegisterPanel(new TrackMapPanel());
            overlayManager.RegisterPanel(new FuelCalculatorPanel());
            overlayManager.RegisterPanel(new InputTelemetryPanel());
            
            // Position advanced panels
            PositionAdvancedPanels(overlayManager);
        }

        private void PositionAdvancedPanels(OverlayManager overlayManager)
        {
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;

            // Position advanced panels
            var relative = overlayManager.GetPanel<RelativeTimingPanel>("RelativeTiming");
            if (relative != null)
            {
                relative.Position = new Point(screenWidth - 370, 200);
                relative.IsVisible = false; // Hidden by default
            }

            var trackMap = overlayManager.GetPanel<TrackMapPanel>("TrackMap");
            if (trackMap != null)
            {
                trackMap.Position = new Point(screenWidth - 350, screenHeight - 350);
                trackMap.IsVisible = false; // Hidden by default
            }

            var fuel = overlayManager.GetPanel<FuelCalculatorPanel>("FuelCalculator");
            if (fuel != null)
            {
                fuel.Position = new Point(50, 200);
                fuel.IsVisible = false; // Hidden by default
            }

            var inputs = overlayManager.GetPanel<InputTelemetryPanel>("InputTelemetry");
            if (inputs != null)
            {
                inputs.Position = new Point(screenWidth - 250, 200);
                inputs.IsVisible = false; // Hidden by default
            }
        }

        #region System Tray Menu Actions

        private void ShowOverlay()
        {
            _overlayWindow?.Show();
        }

        private void HideOverlay()
        {
            _overlayWindow?.Hide();
        }

        private void ShowConfiguration()
        {
            _overlayWindow?.EnterConfigurationMode();
        }

        private void ToggleConfigurationMode()
        {
            _overlayWindow?.ToggleConfigurationMode();
        }

        private void ShowBrakePointAssistant()
        {
            try
            {
                // Launch the brake point assistant as a separate window
                var brakePointWindow = new BrakePointAssist.MainWindow();
                brakePointWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open Brake Point Assistant: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ShowAbout()
        {
            var aboutMessage = $"""
                ShearerPNW iRacing Overlay v2.0

                Professional iRacing overlay system with customizable panels for:
                • Lap tracking and timing
                • Car condition monitoring  
                • Race awareness tools
                • Strategy calculations
                • Input telemetry
                • Brake point training

                Global Hotkeys:
                Ctrl+Shift+F12 - Toggle Configuration
                Ctrl+Shift+F1-F4 - Toggle Panels

                Developer: ShearerPNW
                Framework: WPF .NET 8.0
                iRacing SDK: irsdkSharp
                
                © 2025 ShearerPNW. All rights reserved.
                """;

            MessageBox.Show(aboutMessage, "About ShearerPNW iRacing Overlay", 
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ExitApplication()
        {
            if (!_isExiting)
            {
                var result = MessageBox.Show("Are you sure you want to exit the ShearerPNW iRacing Overlay?",
                    "Exit Confirmation", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    _isExiting = true;
                    Shutdown();
                }
            }
        }

        #endregion

        #region Exception Handling

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            HandleException(e.Exception);
            e.Handled = true;
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            HandleException(e.ExceptionObject as Exception);
        }

        private void HandleException(Exception ex)
        {
            var errorMessage = $"An unexpected error occurred:\n\n{ex?.Message}\n\nThe overlay will continue running.";
            
            try
            {
                _notifyIcon?.ShowBalloonTip(5000, "ShearerPNW iRacing Overlay - Error", 
                    errorMessage, ToolTipIcon.Error);
            }
            catch
            {
                // If balloon tip fails, fall back to message box
                MessageBox.Show(errorMessage, "ShearerPNW iRacing Overlay - Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // Log the error
            LogError(ex);
        }

        private void LogError(Exception ex)
        {
            try
            {
                var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log");
                var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n";
                File.AppendAllText(logPath, logEntry);
            }
            catch
            {
                // Ignore logging errors
            }
        }

        #endregion

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _hotkeyManager?.Dispose();
                _notifyIcon?.Dispose();
                _overlayWindow?.Close();
            }
            catch
            {
                // Ignore cleanup errors
            }

            base.OnExit(e);
        }
    }

    /// <summary>
    /// Extension methods for OverlayWindow
    /// </summary>
    public static class OverlayWindowExtensions
    {
        public static OverlayManager GetOverlayManager(this OverlayWindow window)
        {
            // Use reflection to access private field if needed
            var field = typeof(OverlayWindow).GetField("_overlayManager", 
                BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(window) as OverlayManager;
        }

        public static void TogglePanel(this OverlayWindow window, string panelId)
        {
            var manager = window.GetOverlayManager();
            manager?.TogglePanel(panelId);
        }

        public static void EnterConfigurationMode(this OverlayWindow window)
        {
            // Use reflection to access private method if needed
            var method = typeof(OverlayWindow).GetMethod("EnterConfigMode", 
                BindingFlags.NonPublic | BindingFlags.Instance);
            method?.Invoke(window, null);
        }

        public static void ToggleConfigurationMode(this OverlayWindow window)
        {
            // Use reflection to access private method if needed
            var method = typeof(OverlayWindow).GetMethod("ToggleConfigMode", 
                BindingFlags.NonPublic | BindingFlags.Instance);
            method?.Invoke(window, null);
        }
    }

    /// <summary>
    /// Global hotkey manager using Windows API
    /// </summary>
    public class GlobalHotkeyManager : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private readonly Dictionary<int, Action> _hotkeys = new();
        private int _currentId = 1;
        private IntPtr _windowHandle;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public GlobalHotkeyManager()
        {
            // Create a hidden window to receive hotkey messages
            var windowHelper = new HiddenWindow();
            _windowHandle = windowHelper.Handle;
            windowHelper.WndProcHandler = WndProc;
        }

        public void RegisterHotkey(ModifierKeys modifiers, Keys key, Action callback)
        {
            var id = _currentId++;
            var success = RegisterHotKey(_windowHandle, id, (uint)modifiers, (uint)key);
            
            if (success)
            {
                _hotkeys[id] = callback;
            }
            else
            {
                throw new InvalidOperationException($"Failed to register hotkey: {modifiers}+{key}");
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_HOTKEY)
            {
                var id = wParam.ToInt32();
                if (_hotkeys.TryGetValue(id, out var callback))
                {
                    try
                    {
                        callback?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Hotkey callback error: {ex.Message}");
                    }
                }
            }

            return IntPtr.Zero;
        }

        public void Dispose()
        {
            foreach (var id in _hotkeys.Keys)
            {
                UnregisterHotKey(_windowHandle, id);
            }
            _hotkeys.Clear();
        }
    }

    /// <summary>
    /// Hidden window for receiving Windows messages
    /// </summary>
    public class HiddenWindow : System.Windows.Forms.NativeWindow
    {
        public delegate IntPtr WndProcDelegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        public WndProcDelegate WndProcHandler;

        public HiddenWindow()
        {
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            var result = WndProcHandler?.Invoke(m.HWnd, m.Msg, m.WParam, m.LParam);
            if (result != IntPtr.Zero)
            {
                m.Result = result;
                return;
            }

            base.WndProc(ref m);
        }
    }

    /// <summary>
    /// Extended OverlayWindow partial class to expose necessary methods
    /// </summary>
    public partial class OverlayWindow
    {
        // Make these methods public for the extension methods
        public new void EnterConfigMode()
        {
            base.EnterConfigMode();
        }

        public new void ToggleConfigMode()
        {
            base.ToggleConfigMode();
        }

        public OverlayManager GetManager()
        {
            return _overlayManager;
        }
    }
}