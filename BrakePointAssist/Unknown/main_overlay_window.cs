using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ShearerPNW.iRacingOverlay.Panels;

namespace ShearerPNW.iRacingOverlay
{
    /// <summary>
    /// Main overlay window that hosts all panels and provides transparent overlay functionality
    /// </summary>
    public partial class OverlayWindow : Window
    {
        private readonly OverlayManager _overlayManager;
        private readonly Canvas _overlayCanvas;
        private readonly DispatcherTimer _hideTimer;
        private ConfigWindow _configWindow;
        private bool _isConfigMode = false;
        private bool _isLocked = false;
        private OverlayPanel _draggedPanel;
        private Point _dragStartPoint;

        public OverlayWindow()
        {
            InitializeComponent();
            InitializeOverlay();
        }

        private void InitializeComponent()
        {
            // Window properties for overlay
            Title = "ShearerPNW iRacing Overlay";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            WindowState = WindowState.Maximized;
            
            // Make window click-through when not in config mode
            IsHitTestVisible = false;

            // Create main canvas
            _overlayCanvas = new Canvas
            {
                Background = Brushes.Transparent,
                ClipToBounds = false
            };

            Content = _overlayCanvas;

            // Setup overlay manager
            _overlayManager = new OverlayManager(_overlayCanvas);
            _overlayManager.DataUpdated += OnDataUpdated;

            // Setup hide timer for configuration mode
            _hideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _hideTimer.Tick += OnHideTimerTick;

            // Register hotkeys
            RegisterHotkeys();
        }

        private void InitializeOverlay()
        {
            try
            {
                // Register core panels
                RegisterCorePanels();

                // Load default layout
                LoadDefaultLayout();

                // Start telemetry
                _overlayManager.StartTelemetry();

                // Show welcome message
                ShowStatusMessage("ShearerPNW iRacing Overlay Started\nPress F12 to configure", 3000);
            }
            catch (Exception ex)
            {
                ShowStatusMessage($"Initialization Error: {ex.Message}", 5000);
            }
        }

        private void RegisterCorePanels()
        {
            // Register all core panels
            _overlayManager.RegisterPanel(new LapCounterPanel());
            _overlayManager.RegisterPanel(new TimingPanel());
            _overlayManager.RegisterPanel(new CarConditionPanel());
            _overlayManager.RegisterPanel(new FlagPanel());
            
            // Position panels in default locations
            PositionPanelsDefault();
        }

        private void PositionPanelsDefault()
        {
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;

            // Position panels around the screen edges
            var lapCounter = _overlayManager.GetPanel<LapCounterPanel>("LapCounter");
            if (lapCounter != null)
            {
                lapCounter.Position = new Point(50, 50);
            }

            var timing = _overlayManager.GetPanel<TimingPanel>("Timing");
            if (timing != null)
            {
                timing.Position = new Point(screenWidth - 350, 50);
            }

            var carCondition = _overlayManager.GetPanel<CarConditionPanel>("CarCondition");
            if (carCondition != null)
            {
                carCondition.Position = new Point(50, screenHeight - 250);
            }

            var flag = _overlayManager.GetPanel<FlagPanel>("Flag");
            if (flag != null)
            {
                flag.Position = new Point(screenWidth - 200, screenHeight - 130);
            }
        }

        private void LoadDefaultLayout()
        {
            try
            {
                // Try to load last saved layout
                var layoutManager = new LayoutManager();
                var availableLayouts = layoutManager.GetAvailableLayouts();
                
                if (availableLayouts.Contains("Default"))
                {
                    var defaultLayout = layoutManager.LoadLayout("Default");
                    if (defaultLayout != null)
                    {
                        _overlayManager.LoadLayout(defaultLayout);
                        return;
                    }
                }

                // If no default layout, save current positions as default
                var currentLayout = _overlayManager.SaveCurrentLayout("Default", "Default overlay layout");
                layoutManager.SaveLayout(currentLayout);
            }
            catch (Exception ex)
            {
                ShowStatusMessage($"Layout loading error: {ex.Message}", 3000);
            }
        }

        #region Hotkey Management

        private void RegisterHotkeys()
        {
            // Global hotkeys would be registered here
            // For now, we'll use window-level key handlers when the window has focus
            KeyDown += OnKeyDown;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (!IsHitTestVisible) return; // Only respond in config mode

            switch (e.Key)
            {
                case Key.F12:
                    ToggleConfigMode();
                    break;
                case Key.F1:
                    _overlayManager.TogglePanel("LapCounter");
                    break;
                case Key.F2:
                    _overlayManager.TogglePanel("Timing");
                    break;
                case Key.F3:
                    _overlayManager.TogglePanel("CarCondition");
                    break;
                case Key.F4:
                    _overlayManager.TogglePanel("Flag");
                    break;
                case Key.L:
                    if (Keyboard.Modifiers == ModifierKeys.Control)
                        ToggleLockPanels();
                    break;
                case Key.R:
                    if (Keyboard.Modifiers == ModifierKeys.Control)
                        ResetPanelPositions();
                    break;
                case Key.Escape:
                    ExitConfigMode();
                    break;
            }
        }

        #endregion

        #region Configuration Mode

        private void ToggleConfigMode()
        {
            _isConfigMode = !_isConfigMode;
            
            if (_isConfigMode)
            {
                EnterConfigMode();
            }
            else
            {
                ExitConfigMode();
            }
        }

        private void EnterConfigMode()
        {
            IsHitTestVisible = true;
            _overlayCanvas.Background = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0));
            
            // Show configuration overlay
            ShowConfigOverlay();
            
            // Enable panel dragging
            EnablePanelDragging();
            
            // Start hide timer
            _hideTimer.Start();
            
            ShowStatusMessage("Configuration Mode Active\nF12: Exit | F1-F4: Toggle Panels | Ctrl+L: Lock | ESC: Exit", 5000);
        }

        private void ExitConfigMode()
        {
            _isConfigMode = false;
            IsHitTestVisible = false;
            _overlayCanvas.Background = Brushes.Transparent;
            
            // Hide configuration overlay
            HideConfigOverlay();
            
            // Disable panel dragging
            DisablePanelDragging();
            
            // Stop hide timer
            _hideTimer.Stop();
            
            // Save current layout
            SaveCurrentLayout();
        }

        private void ShowConfigOverlay()
        {
            // Add configuration UI elements to the canvas
            var configPanel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 26, 26, 26)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Width = 300,
                Height = 200,
                Child = CreateConfigPanelContent()
            };

            Canvas.SetLeft(configPanel, (SystemParameters.PrimaryScreenWidth - 300) / 2);
            Canvas.SetTop(configPanel, 100);
            
            configPanel.Name = "ConfigPanel";
            _overlayCanvas.Children.Add(configPanel);
        }

        private void HideConfigOverlay()
        {
            var configPanel = _overlayCanvas.Children.OfType<Border>()
                .FirstOrDefault(b => b.Name == "ConfigPanel");
            
            if (configPanel != null)
            {
                _overlayCanvas.Children.Remove(configPanel);
            }
        }

        private StackPanel CreateConfigPanelContent()
        {
            var content = new StackPanel { Margin = new Thickness(20) };

            // Title
            content.Children.Add(new TextBlock
            {
                Text = "OVERLAY CONFIGURATION",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 20)
            });

            // Hotkeys info
            var hotkeys = new[]
            {
                "F1: Toggle Lap Counter",
                "F2: Toggle Timing Panel",
                "F3: Toggle Car Condition",
                "F4: Toggle Flag Panel",
                "Ctrl+L: Lock/Unlock Panels",
                "Ctrl+R: Reset Positions",
                "F12/ESC: Exit Config Mode"
            };

            foreach (var hotkey in hotkeys)
            {
                content.Children.Add(new TextBlock
                {
                    Text = hotkey,
                    Foreground = Brushes.White,
                    FontSize = 12,
                    Margin = new Thickness(0, 2, 0, 2)
                });
            }

            // Buttons
            var buttonPanel = new StackPanel 
            { 
                Orientation = Orientation.Horizontal, 
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 15, 0, 0)
            };

            var advancedButton = new Button
            {
                Content = "Advanced Settings",
                Background = new SolidColorBrush(Color.FromRgb(227, 30, 36)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(15, 8, 15, 8),
                Margin = new Thickness(0, 0, 10, 0)
            };
            advancedButton.Click += OnAdvancedSettingsClick;
            buttonPanel.Children.Add(advancedButton);

            var closeButton = new Button
            {
                Content = "Close",
                Background = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(15, 8, 15, 8)
            };
            closeButton.Click += (s, e) => ExitConfigMode();
            buttonPanel.Children.Add(closeButton);

            content.Children.Add(buttonPanel);

            return content;
        }

        private void OnAdvancedSettingsClick(object sender, RoutedEventArgs e)
        {
            if (_configWindow == null)
            {
                _configWindow = new ConfigWindow(_overlayManager);
                _configWindow.Closed += (s, e) => _configWindow = null;
            }

            _configWindow.Show();
            _configWindow.Activate();
        }

        #endregion

        #region Panel Management

        private void EnablePanelDragging()
        {
            foreach (var panel in _overlayCanvas.Children.OfType<OverlayPanel>())
            {
                panel.MouseLeftButtonDown += OnPanelMouseDown;
                panel.MouseMove += OnPanelMouseMove;
                panel.MouseLeftButtonUp += OnPanelMouseUp;
                panel.Cursor = Cursors.SizeAll;
            }
        }

        private void DisablePanelDragging()
        {
            foreach (var panel in _overlayCanvas.Children.OfType<OverlayPanel>())
            {
                panel.MouseLeftButtonDown -= OnPanelMouseDown;
                panel.MouseMove -= OnPanelMouseMove;
                panel.MouseLeftButtonUp -= OnPanelMouseUp;
                panel.Cursor = Cursors.Arrow;
            }
        }

        private void OnPanelMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_isLocked) return;

            var panel = sender as OverlayPanel;
            if (panel != null)
            {
                _draggedPanel = panel;
                _dragStartPoint = e.GetPosition(_overlayCanvas);
                panel.CaptureMouse();
                e.Handled = true;
            }
        }

        private void OnPanelMouseMove(object sender, MouseEventArgs e)
        {
            if (_draggedPanel != null && e.LeftButton == MouseButtonState.Pressed && !_isLocked)
            {
                var currentPoint = e.GetPosition(_overlayCanvas);
                var deltaX = currentPoint.X - _dragStartPoint.X;
                var deltaY = currentPoint.Y - _dragStartPoint.Y;

                var newPosition = new Point(
                    Math.Max(0, _draggedPanel.Position.X + deltaX),
                    Math.Max(0, _draggedPanel.Position.Y + deltaY)
                );

                _draggedPanel.Position = newPosition;
                _dragStartPoint = currentPoint;
            }
        }

        private void OnPanelMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_draggedPanel != null)
            {
                _draggedPanel.ReleaseMouseCapture();
                _draggedPanel = null;
            }
        }

        private void ToggleLockPanels()
        {
            _isLocked = !_isLocked;
            var message = _isLocked ? "Panels Locked" : "Panels Unlocked";
            ShowStatusMessage(message, 2000);
        }

        private void ResetPanelPositions()
        {
            PositionPanelsDefault();
            ShowStatusMessage("Panel positions reset", 2000);
        }

        private void SaveCurrentLayout()
        {
            try
            {
                var layout = _overlayManager.SaveCurrentLayout("Default", "Default overlay layout");
                var layoutManager = new LayoutManager();
                layoutManager.SaveLayout(layout);
            }
            catch (Exception ex)
            {
                ShowStatusMessage($"Save error: {ex.Message}", 3000);
            }
        }

        #endregion

        #region Status Messages

        private void ShowStatusMessage(string message, int durationMs)
        {
            var statusPanel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 26, 26, 26)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(20, 10, 20, 10),
                Child = new TextBlock
                {
                    Text = message,
                    Foreground = Brushes.White,
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    TextAlignment = TextAlignment.Center
                }
            };

            Canvas.SetLeft(statusPanel, (SystemParameters.PrimaryScreenWidth - 400) / 2);
            Canvas.SetTop(statusPanel, 50);
            
            statusPanel.Name = "StatusMessage";
            _overlayCanvas.Children.Add(statusPanel);

            // Auto-hide after duration
            var hideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(durationMs)
            };
            hideTimer.Tick += (s, e) =>
            {
                hideTimer.Stop();
                _overlayCanvas.Children.Remove(statusPanel);
            };
            hideTimer.Start();
        }

        #endregion

        #region Event Handlers

        private void OnDataUpdated(object sender, OverlayDataContext dataContext)
        {
            // Data updates are handled by individual panels
            // This could be used for overlay-wide functionality
        }

        private void OnHideTimerTick(object sender, EventArgs e)
        {
            if (_isConfigMode)
            {
                ExitConfigMode();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _overlayManager?.StopTelemetry();
            _hideTimer?.Stop();
            _configWindow?.Close();
            base.OnClosed(e);
        }

        #endregion
    }

    /// <summary>
    /// Advanced configuration window for detailed overlay settings
    /// </summary>
    public class ConfigWindow : Window
    {
        private readonly OverlayManager _overlayManager;

        public ConfigWindow(OverlayManager overlayManager)
        {
            _overlayManager = overlayManager;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Title = "ShearerPNW iRacing Overlay - Advanced Configuration";
            Width = 800;
            Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(26, 26, 26));

            var mainGrid = new Grid();
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Left panel - categories
            var leftPanel = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(36, 36, 36)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(0, 0, 1, 0)
            };

            var categoryList = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Brushes.White,
                FontSize = 14,
                Margin = new Thickness(10)
            };

            categoryList.Items.Add("Panels");
            categoryList.Items.Add("Layouts");
            categoryList.Items.Add("Themes");
            categoryList.Items.Add("Hotkeys");
            categoryList.Items.Add("About");

            leftPanel.Child = categoryList;
            Grid.SetColumn(leftPanel, 0);
            mainGrid.Children.Add(leftPanel);

            // Right panel - content
            var rightPanel = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(26, 26, 26)),
                Padding = new Thickness(20)
            };

            var contentArea = new ScrollViewer
            {
                Content = CreatePanelsContent() // Default content
            };

            rightPanel.Child = contentArea;
            Grid.SetColumn(rightPanel, 1);
            mainGrid.Children.Add(rightPanel);

            categoryList.SelectionChanged += (s, e) =>
            {
                var selected = categoryList.SelectedItem?.ToString();
                contentArea.Content = selected switch
                {
                    "Panels" => CreatePanelsContent(),
                    "Layouts" => CreateLayoutsContent(),
                    "Themes" => CreateThemesContent(),
                    "Hotkeys" => CreateHotkeysContent(),
                    "About" => CreateAboutContent(),
                    _ => CreatePanelsContent()
                };
            };

            Content = mainGrid;
        }

        private StackPanel CreatePanelsContent()
        {
            var content = new StackPanel();

            content.Children.Add(new TextBlock
            {
                Text = "Panel Configuration",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 20)
            });

            content.Children.Add(new TextBlock
            {
                Text = "Configure individual overlay panels, their visibility, position, and settings.",
                Foreground = Brushes.White,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 20)
            });

            // Panel list with checkboxes
            var panelList = new StackPanel();
            
            var panels = new[]
            {
                ("Lap Counter", "LapCounter"),
                ("Timing Panel", "Timing"),
                ("Car Condition", "CarCondition"),
                ("Flag & Session", "Flag")
            };

            foreach (var (displayName, panelId) in panels)
            {
                var panel = new StackPanel 
                { 
                    Orientation = Orientation.Horizontal, 
                    Margin = new Thickness(0, 5, 0, 5) 
                };

                var checkbox = new CheckBox
                {
                    Content = displayName,
                    Foreground = Brushes.White,
                    IsChecked = true, // Would check actual panel visibility
                    VerticalAlignment = VerticalAlignment.Center
                };

                var settingsButton = new Button
                {
                    Content = "Settings",
                    Background = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(20, 0, 0, 0)
                };

                panel.Children.Add(checkbox);
                panel.Children.Add(settingsButton);
                panelList.Children.Add(panel);
            }

            content.Children.Add(panelList);

            return content;
        }

        private StackPanel CreateLayoutsContent()
        {
            var content = new StackPanel();

            content.Children.Add(new TextBlock
            {
                Text = "Layout Management",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 20)
            });

            content.Children.Add(new TextBlock
            {
                Text = "Save, load, and manage different overlay layouts for practice, qualifying, and racing.",
                Foreground = Brushes.White,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 20)
            });

            // Layout buttons
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal };
            
            var saveButton = new Button
            {
                Content = "Save Current Layout",
                Background = new SolidColorBrush(Color.FromRgb(0, 255, 102)),
                Foreground = Brushes.Black,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(15, 8, 15, 8),
                Margin = new Thickness(0, 0, 10, 0)
            };

            var loadButton = new Button
            {
                Content = "Load Layout",
                Background = new SolidColorBrush(Color.FromRgb(227, 30, 36)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(15, 8, 15, 8),
                Margin = new Thickness(0, 0, 10, 0)
            };

            buttonPanel.Children.Add(saveButton);
            buttonPanel.Children.Add(loadButton);
            content.Children.Add(buttonPanel);

            return content;
        }

        private StackPanel CreateThemesContent()
        {
            var content = new StackPanel();

            content.Children.Add(new TextBlock
            {
                Text = "Theme Settings",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 20)
            });

            content.Children.Add(new TextBlock
            {
                Text = "Customize overlay appearance, colors, fonts, and transparency.",
                Foreground = Brushes.White,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 20)
            });

            // Theme options would go here
            content.Children.Add(new TextBlock
            {
                Text = "Theme customization coming soon...",
                Foreground = Brushes.Gray,
                FontStyle = FontStyles.Italic
            });

            return content;
        }

        private StackPanel CreateHotkeysContent()
        {
            var content = new StackPanel();

            content.Children.Add(new TextBlock
            {
                Text = "Hotkey Configuration",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 20)
            });

            content.Children.Add(new TextBlock
            {
                Text = "Configure keyboard shortcuts for quick overlay control during racing.",
                Foreground = Brushes.White,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 20)
            });

            // Hotkey list
            var hotkeys = new[]
            {
                ("Toggle Configuration", "F12"),
                ("Toggle Lap Counter", "F1"),
                ("Toggle Timing Panel", "F2"),
                ("Toggle Car Condition", "F3"),
                ("Toggle Flag Panel", "F4"),
                ("Lock/Unlock Panels", "Ctrl+L"),
                ("Reset Panel Positions", "Ctrl+R")
            };

            foreach (var (action, key) in hotkeys)
            {
                var hotkeyPanel = new StackPanel 
                { 
                    Orientation = Orientation.Horizontal, 
                    Margin = new Thickness(0, 5, 0, 5) 
                };

                hotkeyPanel.Children.Add(new TextBlock
                {
                    Text = action,
                    Foreground = Brushes.White,
                    Width = 200,
                    VerticalAlignment = VerticalAlignment.Center
                });

                hotkeyPanel.Children.Add(new TextBox
                {
                    Text = key,
                    Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                    Padding = new Thickness(8, 4, 8, 4),
                    Width = 100,
                    IsReadOnly = true
                });

                content.Children.Add(hotkeyPanel);
            }

            return content;
        }

        private StackPanel CreateAboutContent()
        {
            var content = new StackPanel();

            content.Children.Add(new TextBlock
            {
                Text = "About ShearerPNW iRacing Overlay",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 20)
            });

            content.Children.Add(new TextBlock
            {
                Text = "Professional iRacing overlay system with customizable panels for lap tracking, car condition monitoring, race awareness, and strategy tools.",
                Foreground = Brushes.White,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 20),
                TextWrapping = TextWrapping.Wrap
            });

            var infoItems = new[]
            {
                "Version: 2.0.0",
                "Developer: ShearerPNW",
                "Framework: WPF .NET 8.0",
                "iRacing SDK: irsdkSharp",
                "Released: 2025"
            };

            foreach (var item in infoItems)
            {
                content.Children.Add(new TextBlock
                {
                    Text = item,
                    Foreground = Brushes.LightGray,
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 2)
                });
            }

            return content;
        }
    }
}