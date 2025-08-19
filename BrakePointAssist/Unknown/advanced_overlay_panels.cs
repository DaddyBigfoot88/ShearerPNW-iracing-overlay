using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace ShearerPNW.iRacingOverlay.Panels
{
    #region Relative Timing Panel

    public class RelativeTimingPanel : OverlayPanel
    {
        private readonly StackPanel _relativeList;
        private readonly TextBlock _playerPositionText;
        private readonly ScrollViewer _scrollViewer;

        public RelativeTimingPanel()
        {
            PanelId = "RelativeTiming";
            DisplayName = "Relative Timing";
            Category = PanelCategory.RaceAwareness;

            Width = 320;
            Height = 400;

            var mainGrid = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(180, 26, 26, 26))
            };
            mainGrid.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 8,
                ShadowDepth = 2
            };

            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(25) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Header
            var header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0, 255, 102)),
                Child = new TextBlock
                {
                    Text = "RELATIVE TIMING",
                    Foreground = Brushes.Black,
                    FontWeight = FontWeights.Bold,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetRow(header, 0);
            mainGrid.Children.Add(header);

            // Player position display
            var positionPanel = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(227, 30, 36)),
                Child = _playerPositionText = new TextBlock
                {
                    Text = "P12 / 24",
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetRow(positionPanel, 1);
            mainGrid.Children.Add(positionPanel);

            // Relative list
            _relativeList = new StackPanel();
            _scrollViewer = new ScrollViewer
            {
                Content = _relativeList,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(5)
            };
            Grid.SetRow(_scrollViewer, 2);
            mainGrid.Children.Add(_scrollViewer);

            Content = mainGrid;
        }

        public override void UpdateData(OverlayDataContext dataContext)
        {
            // Update player position
            var playerPosition = dataContext.RaceAwareness.Standings?.FirstOrDefault(s => s.IsPlayer);
            if (playerPosition != null)
            {
                var totalCars = dataContext.RaceAwareness.Standings.Count;
                _playerPositionText.Text = $"P{playerPosition.Position} / {totalCars}";
            }

            // Update relative cars
            _relativeList.Children.Clear();
            
            var relativeCars = dataContext.RaceAwareness.RelativeCars?.Take(10).ToList() ?? new List<RelativeCarData>();
            
            foreach (var car in relativeCars)
            {
                var carPanel = CreateRelativeCarPanel(car);
                _relativeList.Children.Add(carPanel);
            }
        }

        private Border CreateRelativeCarPanel(RelativeCarData car)
        {
            var panel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(120, 60, 60, 60)),
                BorderBrush = GetClassColor(car.CarClass),
                BorderThickness = new Thickness(2, 0, 0, 0),
                Margin = new Thickness(0, 1, 0, 1),
                Padding = new Thickness(8, 4, 8, 4)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) }); // Position indicator
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Name
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) }); // Gap

            // Position indicator
            var positionIndicator = new TextBlock
            {
                Text = car.IsAhead ? "▲" : "▼",
                Foreground = car.IsAhead ? new SolidColorBrush(Colors.LimeGreen) : new SolidColorBrush(Colors.Orange),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetColumn(positionIndicator, 0);
            grid.Children.Add(positionIndicator);

            // Driver name and car number
            var namePanel = new StackPanel { Orientation = Orientation.Horizontal };
            
            namePanel.Children.Add(new TextBlock
            {
                Text = car.CarNumber,
                Foreground = GetClassColor(car.CarClass),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0)
            });

            namePanel.Children.Add(new TextBlock
            {
                Text = TruncateName(car.DriverName, 15),
                Foreground = car.IsOnTrack ? Brushes.White : Brushes.Gray,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            });

            Grid.SetColumn(namePanel, 1);
            grid.Children.Add(namePanel);

            // Gap time
            var gapText = new TextBlock
            {
                Text = FormatGap(car.GapToPlayer),
                Foreground = GetGapColor(car.GapToPlayer),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                FontFamily = new FontFamily("Consolas, Courier New")
            };
            Grid.SetColumn(gapText, 2);
            grid.Children.Add(gapText);

            panel.Child = grid;
            return panel;
        }

        private SolidColorBrush GetClassColor(string carClass)
        {
            return carClass?.ToUpper() switch
            {
                "LMP1" => new SolidColorBrush(Colors.Red),
                "LMP2" => new SolidColorBrush(Colors.Blue),
                "GTE" => new SolidColorBrush(Colors.Green),
                "GT3" => new SolidColorBrush(Colors.Orange),
                "GT4" => new SolidColorBrush(Colors.Purple),
                "CUP" => new SolidColorBrush(Colors.Yellow),
                _ => new SolidColorBrush(Colors.White)
            };
        }

        private SolidColorBrush GetGapColor(float gap)
        {
            if (Math.Abs(gap) < 1.0f) return new SolidColorBrush(Colors.Red);
            if (Math.Abs(gap) < 3.0f) return new SolidColorBrush(Colors.Orange);
            return new SolidColorBrush(Colors.White);
        }

        private string FormatGap(float gap)
        {
            var absGap = Math.Abs(gap);
            if (absGap < 60)
                return $"{gap:+0.0;-0.0}s";
            else
                return $"{gap / 60:+0.0;-0.0}m";
        }

        private string TruncateName(string name, int maxLength)
        {
            if (string.IsNullOrEmpty(name)) return "Unknown";
            return name.Length > maxLength ? name.Substring(0, maxLength - 3) + "..." : name;
        }

        public override OverlayPanelSettings GetSettings()
        {
            return new RelativeTimingPanelSettings
            {
                PanelId = PanelId,
                IsVisible = IsVisible,
                Position = Position,
                Size = new Size(Width, Height),
                Opacity = PanelOpacity
            };
        }

        public override void ApplySettings(OverlayPanelSettings settings)
        {
            if (settings is RelativeTimingPanelSettings relativeSettings)
            {
                IsVisible = relativeSettings.IsVisible;
                Position = relativeSettings.Position;
                PanelSize = relativeSettings.Size;
                PanelOpacity = relativeSettings.Opacity;
            }
        }
    }

    public class RelativeTimingPanelSettings : OverlayPanelSettings
    {
        public int MaxCarsToShow { get; set; } = 10;
        public bool ShowClassColors { get; set; } = true;
        public bool ShowCarNumbers { get; set; } = true;
    }

    #endregion

    #region Track Map Panel

    public class TrackMapPanel : OverlayPanel
    {
        private readonly Canvas _trackCanvas;
        private readonly TextBlock _trackNameText;
        private readonly Dictionary<int, Ellipse> _carMarkers = new();

        public TrackMapPanel()
        {
            PanelId = "TrackMap";
            DisplayName = "Track Map";
            Category = PanelCategory.RaceAwareness;

            Width = 300;
            Height = 300;

            var mainGrid = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(180, 26, 26, 26))
            };
            mainGrid.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 8,
                ShadowDepth = 2
            };

            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(25) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Header
            var header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                Child = new TextBlock
                {
                    Text = "TRACK MAP",
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetRow(header, 0);
            mainGrid.Children.Add(header);

            // Track name
            _trackNameText = new TextBlock
            {
                Text = "Loading...",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(_trackNameText, 1);
            mainGrid.Children.Add(_trackNameText);

            // Track canvas
            _trackCanvas = new Canvas
            {
                Background = new SolidColorBrush(Color.FromRgb(10, 10, 10)),
                Margin = new Thickness(5)
            };
            Grid.SetRow(_trackCanvas, 2);
            mainGrid.Children.Add(_trackCanvas);

            Content = mainGrid;
        }

        public override void UpdateData(OverlayDataContext dataContext)
        {
            var trackMap = dataContext.RaceAwareness.TrackMap;
            if (trackMap == null) return;

            _trackNameText.Text = trackMap.TrackName ?? "Unknown Track";

            // Clear old markers
            _trackCanvas.Children.Clear();
            _carMarkers.Clear();

            if (trackMap.CarPositions?.Any() != true) return;

            DrawTrackOutline();
            DrawCarMarkers(trackMap.CarPositions);
        }

        private void DrawTrackOutline()
        {
            var canvasWidth = _trackCanvas.ActualWidth;
            var canvasHeight = _trackCanvas.ActualHeight;

            if (canvasWidth <= 0 || canvasHeight <= 0) return;

            var centerX = canvasWidth / 2;
            var centerY = canvasHeight / 2;
            var radius = Math.Min(centerX, centerY) - 20;

            // Track outline (simplified circular track)
            var trackCircle = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                StrokeThickness = 4,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(trackCircle, centerX - radius);
            Canvas.SetTop(trackCircle, centerY - radius);
            _trackCanvas.Children.Add(trackCircle);

            // Start/finish line
            var startLine = new Line
            {
                X1 = centerX,
                Y1 = centerY - radius - 5,
                X2 = centerX,
                Y2 = centerY - radius + 5,
                Stroke = Brushes.White,
                StrokeThickness = 3
            };
            _trackCanvas.Children.Add(startLine);

            // S/F text
            var sfText = new TextBlock
            {
                Text = "S/F",
                Foreground = Brushes.White,
                FontSize = 8,
                FontWeight = FontWeights.Bold
            };
            Canvas.SetLeft(sfText, centerX - 8);
            Canvas.SetTop(sfText, centerY - radius - 20);
            _trackCanvas.Children.Add(sfText);
        }

        private void DrawCarMarkers(List<CarPosition> carPositions)
        {
            var canvasWidth = _trackCanvas.ActualWidth;
            var canvasHeight = _trackCanvas.ActualHeight;

            if (canvasWidth <= 0 || canvasHeight <= 0) return;

            var centerX = canvasWidth / 2;
            var centerY = canvasHeight / 2;
            var radius = Math.Min(centerX, centerY) - 20;

            foreach (var car in carPositions.Where(c => c.IsOnTrack))
            {
                // Calculate position on circle
                var angle = (car.TrackPct * 2 * Math.PI) - (Math.PI / 2); // Start at top
                var carX = centerX + (radius * Math.Cos(angle));
                var carY = centerY + (radius * Math.Sin(angle));

                var marker = new Ellipse
                {
                    Width = car.IsPlayer ? 8 : 6,
                    Height = car.IsPlayer ? 8 : 6,
                    Fill = GetCarMarkerColor(car)
                };

                if (car.IsPlayer)
                {
                    marker.Effect = new DropShadowEffect
                    {
                        Color = Colors.LimeGreen,
                        BlurRadius = 6,
                        ShadowDepth = 0
                    };
                }

                Canvas.SetLeft(marker, carX - marker.Width / 2);
                Canvas.SetTop(marker, carY - marker.Height / 2);
                
                _trackCanvas.Children.Add(marker);
                _carMarkers[car.CarIdx] = marker;
            }
        }

        private SolidColorBrush GetCarMarkerColor(CarPosition car)
        {
            if (car.IsPlayer)
                return new SolidColorBrush(Colors.LimeGreen);

            return car.CarClass?.ToUpper() switch
            {
                "LMP1" => new SolidColorBrush(Colors.Red),
                "LMP2" => new SolidColorBrush(Colors.Blue),
                "GTE" => new SolidColorBrush(Colors.Green),
                "GT3" => new SolidColorBrush(Colors.Orange),
                "GT4" => new SolidColorBrush(Colors.Purple),
                "CUP" => new SolidColorBrush(Colors.Yellow),
                _ => new SolidColorBrush(Colors.White)
            };
        }

        public override OverlayPanelSettings GetSettings()
        {
            return new TrackMapPanelSettings
            {
                PanelId = PanelId,
                IsVisible = IsVisible,
                Position = Position,
                Size = new Size(Width, Height),
                Opacity = PanelOpacity
            };
        }

        public override void ApplySettings(OverlayPanelSettings settings)
        {
            if (settings is TrackMapPanelSettings mapSettings)
            {
                IsVisible = mapSettings.IsVisible;
                Position = mapSettings.Position;
                PanelSize = mapSettings.Size;
                PanelOpacity = mapSettings.Opacity;
            }
        }
    }

    public class TrackMapPanelSettings : OverlayPanelSettings
    {
        public bool ShowClassColors { get; set; } = true;
        public bool ShowPlayerTrail { get; set; } = false;
        public int TrailLength { get; set; } = 50;
    }

    #endregion

    #region Fuel Calculator Panel

    public class FuelCalculatorPanel : OverlayPanel
    {
        private readonly TextBlock _currentFuelText;
        private readonly TextBlock _fuelPerLapText;
        private readonly TextBlock _fuelToFinishText;
        private readonly TextBlock _pitWindowText;
        private readonly ProgressBar _fuelBar;
        private readonly TextBlock _lapsRemainingText;

        public FuelCalculatorPanel()
        {
            PanelId = "FuelCalculator";
            DisplayName = "Fuel Calculator";
            Category = PanelCategory.Strategy;

            Width = 280;
            Height = 160;

            var mainGrid = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(180, 26, 26, 26))
            };
            mainGrid.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 8,
                ShadowDepth = 2
            };

            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(25) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Header
            var header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(255, 215, 0)),
                Child = new TextBlock
                {
                    Text = "FUEL CALCULATOR",
                    Foreground = Brushes.Black,
                    FontWeight = FontWeights.Bold,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetRow(header, 0);
            mainGrid.Children.Add(header);

            // Content
            var contentGrid = new Grid { Margin = new Thickness(10, 5, 10, 5) };
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(25) }); // Fuel bar
            contentGrid.RowDefinitions.Add(new RowDefinition()); // Current fuel
            contentGrid.RowDefinitions.Add(new RowDefinition()); // Per lap
            contentGrid.RowDefinitions.Add(new RowDefinition()); // To finish
            contentGrid.RowDefinitions.Add(new RowDefinition()); // Pit window

            // Fuel bar
            var fuelSection = new StackPanel();
            fuelSection.Children.Add(new TextBlock
            {
                Text = "FUEL REMAINING",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            _fuelBar = new ProgressBar
            {
                Height = 12,
                Margin = new Thickness(0, 3, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = new LinearGradientBrush(Colors.Red, Colors.Green, 0)
            };
            fuelSection.Children.Add(_fuelBar);
            Grid.SetRow(fuelSection, 0);
            contentGrid.Children.Add(fuelSection);

            // Current fuel
            var currentFuelPanel = CreateInfoRow("CURRENT:", out _currentFuelText, "25.5L");
            Grid.SetRow(currentFuelPanel, 1);
            contentGrid.Children.Add(currentFuelPanel);

            // Fuel per lap
            var perLapPanel = CreateInfoRow("PER LAP:", out _fuelPerLapText, "2.1L");
            Grid.SetRow(perLapPanel, 2);
            contentGrid.Children.Add(perLapPanel);

            // Fuel to finish
            var toFinishPanel = CreateInfoRow("TO FINISH:", out _fuelToFinishText, "42.0L");
            Grid.SetRow(toFinishPanel, 3);
            contentGrid.Children.Add(toFinishPanel);

            // Pit window
            var pitWindowPanel = CreateInfoRow("PIT WINDOW:", out _pitWindowText, "L8-L12");
            Grid.SetRow(pitWindowPanel, 4);
            contentGrid.Children.Add(pitWindowPanel);

            Grid.SetRow(contentGrid, 1);
            mainGrid.Children.Add(contentGrid);

            Content = mainGrid;
        }

        private StackPanel CreateInfoRow(string label, out TextBlock valueText, string defaultValue)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

            panel.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Width = 80,
                VerticalAlignment = VerticalAlignment.Center
            });

            valueText = new TextBlock
            {
                Text = defaultValue,
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Consolas, Courier New")
            };
            panel.Children.Add(valueText);

            return panel;
        }

        public override void UpdateData(OverlayDataContext dataContext)
        {
            var carCondition = dataContext.CarCondition;
            var session = dataContext.Session;

            // Update fuel bar
            _fuelBar.Value = carCondition.FuelPercentage;
            
            // Update fuel bar color based on level
            if (carCondition.FuelPercentage < 20)
                _fuelBar.Foreground = new SolidColorBrush(Colors.Red);
            else if (carCondition.FuelPercentage < 40)
                _fuelBar.Foreground = new SolidColorBrush(Colors.Orange);
            else
                _fuelBar.Foreground = new SolidColorBrush(Colors.Green);

            // Update text values
            _currentFuelText.Text = $"{carCondition.FuelRemaining:F1}L";
            
            // Calculate fuel per lap (simplified)
            var fuelPerLap = 2.1f; // Would calculate from actual consumption data
            _fuelPerLapText.Text = $"{fuelPerLap:F1}L";

            // Calculate fuel to finish
            var lapsRemaining = session.TotalLaps > 0 ? session.TotalLaps - session.CurrentLap : carCondition.EstimatedLapsRemaining;
            var fuelToFinish = lapsRemaining * fuelPerLap;
            _fuelToFinishText.Text = $"{fuelToFinish:F1}L";

            // Calculate pit window
            var fuelLaps = carCondition.FuelRemaining / fuelPerLap;
            var pitStart = session.CurrentLap + (int)(fuelLaps * 0.8f);
            var pitEnd = session.CurrentLap + (int)(fuelLaps * 0.95f);
            _pitWindowText.Text = $"L{pitStart}-L{pitEnd}";

            // Color code pit window based on urgency
            if (fuelLaps < 3)
                _pitWindowText.Foreground = new SolidColorBrush(Colors.Red);
            else if (fuelLaps < 5)
                _pitWindowText.Foreground = new SolidColorBrush(Colors.Orange);
            else
                _pitWindowText.Foreground = Brushes.White;
        }

        public override OverlayPanelSettings GetSettings()
        {
            return new FuelCalculatorPanelSettings
            {
                PanelId = PanelId,
                IsVisible = IsVisible,
                Position = Position,
                Size = new Size(Width, Height),
                Opacity = PanelOpacity
            };
        }

        public override void ApplySettings(OverlayPanelSettings settings)
        {
            if (settings is FuelCalculatorPanelSettings fuelSettings)
            {
                IsVisible = fuelSettings.IsVisible;
                Position = fuelSettings.Position;
                PanelSize = fuelSettings.Size;
                PanelOpacity = fuelSettings.Opacity;
            }
        }
    }

    public class FuelCalculatorPanelSettings : OverlayPanelSettings
    {
        public bool UseConservativeCalculation { get; set; } = true;
        public float SafetyMarginLaps { get; set; } = 1.0f;
        public bool ShowPitWindowCountdown { get; set; } = true;
    }

    #endregion

    #region Input Telemetry Panel

    public class InputTelemetryPanel : OverlayPanel
    {
        private readonly ProgressBar _throttleBar;
        private readonly ProgressBar _brakeBar;
        private readonly ProgressBar _clutchBar;
        private readonly Canvas _steeringIndicator;
        private readonly TextBlock _gearText;
        private readonly TextBlock _speedText;
        private readonly TextBlock _rpmText;
        private readonly Rectangle _steeringBar;

        public InputTelemetryPanel()
        {
            PanelId = "InputTelemetry";
            DisplayName = "Input Telemetry";
            Category = PanelCategory.Telemetry;

            Width = 200;
            Height = 300;

            var mainGrid = new Grid
            {
                Background = new SolidColorBrush(Color.FromArgb(180, 26, 26, 26))
            };
            mainGrid.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 8,
                ShadowDepth = 2
            };

            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(25) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Header
            var header = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(138, 43, 226)),
                Child = new TextBlock
                {
                    Text = "INPUT TELEMETRY",
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetRow(header, 0);
            mainGrid.Children.Add(header);

            // Content
            var contentGrid = new Grid { Margin = new Thickness(10, 5, 10, 5) };
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) }); // Speed/Gear/RPM
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(60) }); // Pedals
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Steering

            // Speed, Gear, RPM section
            var speedGearPanel = CreateSpeedGearPanel();
            Grid.SetRow(speedGearPanel, 0);
            contentGrid.Children.Add(speedGearPanel);

            // Pedal inputs
            var pedalPanel = CreatePedalPanel();
            Grid.SetRow(pedalPanel, 1);
            contentGrid.Children.Add(pedalPanel);

            // Steering input
            var steeringPanel = CreateSteeringPanel();
            Grid.SetRow(steeringPanel, 2);
            contentGrid.Children.Add(steeringPanel);

            Grid.SetRow(contentGrid, 1);
            mainGrid.Children.Add(contentGrid);

            Content = mainGrid;
        }

        private Grid CreateSpeedGearPanel()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            // Speed
            var speedPanel = new StackPanel();
            speedPanel.Children.Add(new TextBlock
            {
                Text = "SPEED",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            _speedText = new TextBlock
            {
                Text = "0",
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            speedPanel.Children.Add(_speedText);
            Grid.SetColumn(speedPanel, 0);
            grid.Children.Add(speedPanel);

            // Gear
            var gearPanel = new StackPanel();
            gearPanel.Children.Add(new TextBlock
            {
                Text = "GEAR",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            _gearText = new TextBlock
            {
                Text = "N",
                Foreground = Brushes.White,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            gearPanel.Children.Add(_gearText);
            Grid.SetColumn(gearPanel, 1);
            grid.Children.Add(gearPanel);

            // RPM
            var rpmPanel = new StackPanel();
            rpmPanel.Children.Add(new TextBlock
            {
                Text = "RPM",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            _rpmText = new TextBlock
            {
                Text = "0",
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            rpmPanel.Children.Add(_rpmText);
            Grid.SetColumn(rpmPanel, 2);
            grid.Children.Add(rpmPanel);

            return grid;
        }

        private Grid CreatePedalPanel()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            // Throttle
            var throttlePanel = new StackPanel { Margin = new Thickness(2, 0, 2, 0) };
            throttlePanel.Children.Add(new TextBlock
            {
                Text = "THR",
                Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 102)),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            _throttleBar = new ProgressBar
            {
                Orientation = Orientation.Vertical,
                Height = 40,
                Width = 15,
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 102)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            throttlePanel.Children.Add(_throttleBar);
            Grid.SetColumn(throttlePanel, 0);
            grid.Children.Add(throttlePanel);

            // Brake
            var brakePanel = new StackPanel { Margin = new Thickness(2, 0, 2, 0) };
            brakePanel.Children.Add(new TextBlock
            {
                Text = "BRK",
                Foreground = new SolidColorBrush(Color.FromRgb(227, 30, 36)),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            _brakeBar = new ProgressBar
            {
                Orientation = Orientation.Vertical,
                Height = 40,
                Width = 15,
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = new SolidColorBrush(Color.FromRgb(227, 30, 36)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            brakePanel.Children.Add(_brakeBar);
            Grid.SetColumn(brakePanel, 1);
            grid.Children.Add(brakePanel);

            // Clutch
            var clutchPanel = new StackPanel { Margin = new Thickness(2, 0, 2, 0) };
            clutchPanel.Children.Add(new TextBlock
            {
                Text = "CLU",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            _clutchBar = new ProgressBar
            {
                Orientation = Orientation.Vertical,
                Height = 40,
                Width = 15,
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Foreground = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            clutchPanel.Children.Add(_clutchBar);
            Grid.SetColumn(clutchPanel, 2);
            grid.Children.Add(clutchPanel);

            return grid;
        }

        private StackPanel CreateSteeringPanel()
        {
            var panel = new StackPanel();

            panel.Children.Add(new TextBlock
            {
                Text = "STEERING",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 5)
            });

            _steeringIndicator = new Canvas
            {
                Height = 60,
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Margin = new Thickness(10, 0, 10, 0)
            };

            // Center line
            var centerLine = new Line
            {
                X1 = 0, Y1 = 30, X2 = 200, Y2 = 30,
                Stroke = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                StrokeThickness = 1
            };
            _steeringIndicator.Children.Add(centerLine);

            // Steering bar
            _steeringBar = new Rectangle
            {
                Height = 20,
                Width = 4,
                Fill = new SolidColorBrush(Color.FromRgb(255, 102, 0))
            };
            Canvas.SetTop(_steeringBar, 20);
            _steeringIndicator.Children.Add(_steeringBar);

            panel.Children.Add(_steeringIndicator);

            return panel;
        }

        public override void UpdateData(OverlayDataContext dataContext)
        {
            var engine = dataContext.CarCondition.Engine;

            // Update speed, gear, RPM
            _speedText.Text = $"{engine.Speed:F0}";
            _gearText.Text = engine.Gear switch
            {
                0 => "N",
                -1 => "R",
                _ => engine.Gear.ToString()
            };
            _rpmText.Text = $"{engine.RPM:F0}";

            // Update pedal bars
            _throttleBar.Value = engine.Throttle * 100;
            _brakeBar.Value = engine.Brake * 100;
            _clutchBar.Value = 0; // Would need clutch data

            // Update steering indicator
            if (_steeringIndicator.ActualWidth > 0)
            {
                var centerX = _steeringIndicator.ActualWidth / 2;
                var steerInput = 0.0f; // Would need steering angle data
                var steerX = centerX + (steerInput * centerX);
                Canvas.SetLeft(_steeringBar, Math.Max(0, Math.Min(_steeringIndicator.ActualWidth - 4, steerX)));
            }
        }

        public override OverlayPanelSettings GetSettings()
        {
            return new InputTelemetryPanelSettings
            {
                PanelId = PanelId,
                IsVisible = IsVisible,
                Position = Position,
                Size = new Size(Width, Height),
                Opacity = PanelOpacity
            };
        }

        public override void ApplySettings(OverlayPanelSettings settings)
        {
            if (settings is InputTelemetryPanelSettings inputSettings)
            {
                IsVisible = inputSettings.IsVisible;
                Position = inputSettings.Position;
                PanelSize = inputSettings.Size;
                PanelOpacity = inputSettings.Opacity;
            }
        }
    }

    public class InputTelemetryPanelSettings : OverlayPanelSettings
    {
        public bool ShowThrottle { get; set; } = true;
        public bool ShowBrake { get; set; } = true;
        public bool ShowClutch { get; set; } = true;
        public bool ShowSteering { get; set; } = true;
        public bool ShowGear { get; set; } = true;
    }

    #endregion
}