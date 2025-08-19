using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using irsdkSharp;
using Microsoft.Win32;
using System.Diagnostics;
using System.Linq;

namespace BrakePointAssist
{
    public partial class MainWindow : Window
    {
        // Core components
        private IRacingSDK irsdk = new IRacingSDK();
        private TonePlayer player = new TonePlayer();
        private DispatcherTimer updateTimer;
        private Config config = new Config();
        private ReferenceLap referenceLap = new ReferenceLap();

        // State tracking
        private bool isRecording = false;
        private int recordingLap = -1;
        private float lastZoneAt = -9999f;
        private float prevSpeed = 0f;
        private bool prevBraking = false;
        private bool inCorner = false;
        private double lastLapPctSeen = 0.0;
        private float lastLapTime = 0f;

        // Alert tracking
        private HashSet<int> firedThisLap = new HashSet<int>();
        private Dictionary<int, int> zoneStageMaskThisLap = new Dictionary<int, int>();

        // Enhanced tracking
        private Queue<TelemetryPacket> telemetryHistory = new Queue<TelemetryPacket>();
        private DateTime lastSaveTime = DateTime.MinValue;

        // Navigation state
        private string currentTab = "Main";

        // Track Maps
        private TrackDatabase trackDatabase = new TrackDatabase();
        private TrackLayout selectedTrack = null;
        private TrackLayout detectedTrack = null;
        private string lastDetectedTrackName = "";

        // File paths
        private readonly string ConfigPath = "settings.json";
        private readonly string DefaultRefPath = "reference_brakes.json";
        private readonly string DataFolder = "BrakePointData";

        public MainWindow()
        {
            try
            {
                InitializeComponent();
                InitializeApplication();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Startup error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InitializeApplication()
        {
            try
            {
                // Create data folder if it doesn't exist
                if (!Directory.Exists(DataFolder))
                    Directory.CreateDirectory(DataFolder);

                LoadOrCreateConfig();
                LoadReferenceIfExists();
                ApplyConfigToUI();
                SetupTimer();
                InitializeTrackMaps();
                
                StatusText.Text = "Ready - Waiting for iRacing connection...";
                TrackLengthText.Text = "";
                LastLapTimeText.Text = "Last Lap: --:--";
                
                // Set build info
                AppVersionText.Text = "v2.1 - Development Build";
                BuildDateText.Text = $"Built: {DateTime.Now:yyyy-MM-dd HH:mm}";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Init error: {ex.Message}";
            }
        }

        private void InitializeTrackMaps()
        {
            try
            {
                trackDatabase.LoadTrackDatabase();
                PopulateTrackList();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Track map init error: {ex.Message}";
            }
        }

        private void PopulateTrackList()
        {
            TrackList.Items.Clear();
            
            foreach (var track in trackDatabase.Tracks)
            {
                var item = new ListBoxItem
                {
                    Content = $"{track.DisplayName}",
                    Tag = track,
                    Foreground = Brushes.White
                };
                TrackList.Items.Add(item);
            }
        }

        #region Navigation Methods

        private void MainTab_Click(object sender, RoutedEventArgs e)
        {
            SwitchToTab("Main", "Main - Brake Point Assistant");
        }

        private void TracksTab_Click(object sender, RoutedEventArgs e)
        {
            SwitchToTab("Tracks", "Track Maps - Development Area");
        }

        private void SettingsTab_Click(object sender, RoutedEventArgs e)
        {
            SwitchToTab("Settings", "Settings - Advanced Configuration");
        }

        private void AboutTab_Click(object sender, RoutedEventArgs e)
        {
            SwitchToTab("About", "About - Help & Information");
        }

        private void SwitchToTab(string tabName, string tabDescription)
        {
            currentTab = tabName;
            CurrentTabText.Text = tabDescription;

            // Hide all tab contents
            MainTabContent.Visibility = Visibility.Collapsed;
            TracksTabContent.Visibility = Visibility.Collapsed;
            SettingsTabContent.Visibility = Visibility.Collapsed;
            AboutTabContent.Visibility = Visibility.Collapsed;

            // Reset all tab button styles
            MainTabButton.Style = (Style)FindResource("TabButton");
            TracksTabButton.Style = (Style)FindResource("TabButton");
            SettingsTabButton.Style = (Style)FindResource("TabButton");
            AboutTabButton.Style = (Style)FindResource("TabButton");

            // Show selected tab and highlight button
            switch (tabName)
            {
                case "Main":
                    MainTabContent.Visibility = Visibility.Visible;
                    MainTabButton.Style = (Style)FindResource("ActiveTabButton");
                    break;
                case "Tracks":
                    TracksTabContent.Visibility = Visibility.Visible;
                    TracksTabButton.Style = (Style)FindResource("ActiveTabButton");
                    break;
                case "Settings":
                    SettingsTabContent.Visibility = Visibility.Visible;
                    SettingsTabButton.Style = (Style)FindResource("ActiveTabButton");
                    break;
                case "About":
                    AboutTabContent.Visibility = Visibility.Visible;
                    AboutTabButton.Style = (Style)FindResource("ActiveTabButton");
                    break;
            }
        }

        #endregion

        #region Auto Track Detection

        private void AutoDetectCurrentTrack()
        {
            try
            {
                string currentTrackName = referenceLap.Track;
                if (string.IsNullOrEmpty(currentTrackName) || currentTrackName == lastDetectedTrackName) 
                    return;

                // Try to match the current track with our database
                var matchedTrack = FindMatchingTrack(currentTrackName);
                
                if (matchedTrack != null && matchedTrack != detectedTrack)
                {
                    detectedTrack = matchedTrack;
                    lastDetectedTrackName = currentTrackName;
                    
                    // Auto-select in Track Maps tab
                    AutoSelectTrackInDatabase(matchedTrack);
                    
                    // Update status with detection info
                    StatusText.Text = $"🎯 Auto-detected: {matchedTrack.DisplayName} ({matchedTrack.Corners.Count} corners)";
                    
                    // Update track info with database details
                    if (currentTab == "Tracks")
                    {
                        UpdateTrackDetails();
                        RenderTrackMap();
                    }
                }
            }
            catch (Exception ex)
            {
                // Don't let auto-detection errors break the main functionality
                System.Diagnostics.Debug.WriteLine($"Auto-detection error: {ex.Message}");
            }
        }

        private TrackLayout FindMatchingTrack(string iracingTrackName)
        {
            if (string.IsNullOrEmpty(iracingTrackName)) return null;
            
            string cleanName = iracingTrackName.ToLowerInvariant()
                .Replace("international", "")
                .Replace("speedway", "")
                .Replace("raceway", "")
                .Replace("motor", "")
                .Replace("circuit", "")
                .Replace("motorsports", "")
                .Replace("park", "")
                .Replace("-", " ")
                .Replace("_", " ")
                .Trim();

            // Direct name matching
            foreach (var track in trackDatabase.Tracks)
            {
                // Try exact internal name match first
                if (track.InternalName.Equals(cleanName.Replace(" ", "_"), StringComparison.OrdinalIgnoreCase))
                    return track;
                
                // Try display name match
                string trackDisplayLower = track.DisplayName.ToLowerInvariant();
                if (trackDisplayLower.Contains(cleanName) || cleanName.Contains(trackDisplayLower.Split(' ')[0]))
                    return track;
                
                // Try key word matching
                var trackKeywords = ExtractTrackKeywords(track.DisplayName);
                var iracingKeywords = ExtractTrackKeywords(iracingTrackName);
                
                if (trackKeywords.Intersect(iracingKeywords).Count() >= 2)
                    return track;
            }

            // Fuzzy matching for common track name variations
            var fuzzyMatches = new Dictionary<string, string>
            {
                { "laguna", "laguna_seca" },
                { "watkins", "watkins_glen" },
                { "spa", "spa" },
                { "silverstone", "silverstone" },
                { "brands", "brands_hatch" },
                { "monza", "monza" },
                { "sebring", "sebring" },
                { "road america", "road_america" },
                { "mid ohio", "mid_ohio" },
                { "lime rock", "lime_rock" },
                { "virginia", "virginia_international_raceway" },
                { "vir", "virginia_international_raceway" },
                { "daytona", "daytona" },
                { "talladega", "talladega" },
                { "indianapolis", "indianapolis" },
                { "indy", "indianapolis" },
                { "charlotte", "charlotte" },
                { "atlanta", "atlanta" },
                { "texas", "texas" },
                { "phoenix", "phoenix" },
                { "dover", "dover" },
                { "pocono", "pocono" },
                { "michigan", "michigan" },
                { "bristol", "bristol" },
                { "martinsville", "martinsville" },
                { "richmond", "richmond" },
                { "nurburgring", "nurburgring_gp" },
                { "hungaroring", "hungaroring" },
                { "suzuka", "suzuka" },
                { "interlagos", "interlagos" },
                { "imola", "imola" },
                { "barcelona", "barcelona" },
                { "zandvoort", "zandvoort" },
                { "paul ricard", "paul_ricard" },
                { "hockenheim", "hockenheim" },
                { "donington", "donington_park" },
                { "oulton", "oulton_park" },
                { "snetterton", "snetterton" },
                { "bathurst", "mount_panorama" },
                { "phillip island", "phillip_island" },
                { "road atlanta", "road_atlanta" },
                { "barber", "barber_motorsports_park" },
                { "sonoma", "sonoma_raceway" },
                { "gateway", "gateway_motorsports_park" },
                { "iowa", "iowa_speedway" },
                { "eldora", "eldora_speedway" },
                { "knoxville", "knoxville_raceway" },
                { "williams grove", "williams_grove_speedway" }
            };

            foreach (var fuzzyMatch in fuzzyMatches)
            {
                if (cleanName.Contains(fuzzyMatch.Key))
                {
                    return trackDatabase.Tracks.FirstOrDefault(t => 
                        t.InternalName.Equals(fuzzyMatch.Value, StringComparison.OrdinalIgnoreCase));
                }
            }

            return null;
        }

        private List<string> ExtractTrackKeywords(string trackName)
        {
            return trackName.ToLowerInvariant()
                .Split(new char[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(word => word.Length > 2 && 
                              !new[] { "international", "motor", "speedway", "raceway", "circuit", "park" }.Contains(word))
                .ToList();
        }

        private void AutoSelectTrackInDatabase(TrackLayout track)
        {
            try
            {
                // Update the selected track
                selectedTrack = track;
                
                // Find and select the track in the UI list
                foreach (ListBoxItem item in TrackList.Items)
                {
                    if (item.Tag is TrackLayout trackLayout && trackLayout.InternalName == track.InternalName)
                    {
                        TrackList.SelectedItem = item;
                        break;
                    }
                }
                
                // Update the track title
                if (SelectedTrackTitle != null)
                {
                    SelectedTrackTitle.Text = $"🎯 {track.DisplayName} (Auto-detected)";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Auto-select error: {ex.Message}");
            }
        }

        #endregion

        #region Track Maps Methods

        private void TrackSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string searchText = TrackSearchBox.Text.ToLower();
            
            TrackList.Items.Clear();
            
            var filteredTracks = trackDatabase.Tracks
                .Where(t => t.DisplayName.ToLower().Contains(searchText) || 
                           t.InternalName.ToLower().Contains(searchText))
                .ToList();

            foreach (var track in filteredTracks)
            {
                var item = new ListBoxItem
                {
                    Content = track.DisplayName,
                    Tag = track,
                    Foreground = Brushes.White
                };
                TrackList.Items.Add(item);
            }
        }

        private void TrackCategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TrackCategoryList.SelectedItem == null) return;
            
            var selectedCategory = ((ListBoxItem)TrackCategoryList.SelectedItem).Content.ToString();
            
            TrackList.Items.Clear();
            
            List<TrackLayout> filteredTracks;
            
            switch (selectedCategory)
            {
                case "🏎️ Road Courses":
                    filteredTracks = trackDatabase.Tracks.Where(t => t.Category == TrackCategory.RoadCourse).ToList();
                    break;
                case "🏁 Ovals":
                    filteredTracks = trackDatabase.Tracks.Where(t => t.Category == TrackCategory.Oval).ToList();
                    break;
                case "🏞️ Street Circuits":
                    filteredTracks = trackDatabase.Tracks.Where(t => t.Category == TrackCategory.StreetCircuit).ToList();
                    break;
                case "🌍 International":
                    filteredTracks = trackDatabase.Tracks.Where(t => t.Country != "USA").ToList();
                    break;
                case "🇺🇸 USA":
                    filteredTracks = trackDatabase.Tracks.Where(t => t.Country == "USA").ToList();
                    break;
                case "🏕️ Dirt Tracks":
                    filteredTracks = trackDatabase.Tracks.Where(t => t.Category == TrackCategory.DirtOval || t.Category == TrackCategory.DirtRoad).ToList();
                    break;
                default:
                    filteredTracks = trackDatabase.Tracks.ToList();
                    break;
            }

            foreach (var track in filteredTracks)
            {
                var item = new ListBoxItem
                {
                    Content = track.DisplayName,
                    Tag = track,
                    Foreground = Brushes.White
                };
                TrackList.Items.Add(item);
            }
        }

        private void TrackList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TrackList.SelectedItem == null) return;
            
            var selectedItem = (ListBoxItem)TrackList.SelectedItem;
            selectedTrack = (TrackLayout)selectedItem.Tag;
            
            // Check if this is the auto-detected track
            bool isAutoDetected = detectedTrack != null && selectedTrack.InternalName == detectedTrack.InternalName;
            
            if (isAutoDetected)
            {
                SelectedTrackTitle.Text = $"🎯 {selectedTrack.DisplayName} (Auto-detected from iRacing)";
            }
            else
            {
                SelectedTrackTitle.Text = selectedTrack.DisplayName;
            }
            
            UpdateTrackDetails();
            RenderTrackMap();
        }

        private void UpdateTrackDetails()
        {
            if (selectedTrack == null) 
            {
                TrackDetailsText.Text = "Select a track to view details";
                return;
            }
            
            bool isAutoDetected = detectedTrack != null && selectedTrack.InternalName == detectedTrack.InternalName;
            string autoDetectedPrefix = isAutoDetected ? "🎯 LIVE TRACK FROM iRACING\n\n" : "";
            
            var details = autoDetectedPrefix +
                         $"Track: {selectedTrack.DisplayName}\n" +
                         $"Length: {selectedTrack.LengthKm:F2} km ({selectedTrack.LengthKm * 0.621371f:F2} miles)\n" +
                         $"Corners: {selectedTrack.Corners.Count}\n" +
                         $"Category: {GetCategoryDisplayName(selectedTrack.Category)}\n" +
                         $"Country: {selectedTrack.Country}\n" +
                         $"Configuration: {selectedTrack.Configuration}\n";
            
            if (!string.IsNullOrEmpty(selectedTrack.Description))
            {
                details += $"\nDescription: {selectedTrack.Description}\n";
            }
            
            if (selectedTrack.Corners.Any())
            {
                details += "\nCorners:\n";
                foreach (var corner in selectedTrack.Corners.Take(10)) // Show first 10 corners
                {
                    string cornerType = GetCornerTypeIcon(corner.Type);
                    details += $"{cornerType} {corner.Name} ({corner.Type})\n";
                }
                
                if (selectedTrack.Corners.Count > 10)
                {
                    details += $"... and {selectedTrack.Corners.Count - 10} more corners\n";
                }
            }
            
            // Add brake zone info if available and this is the current track
            if (isAutoDetected && referenceLap.Zones.Count > 0)
            {
                details += $"\n🔴 RECORDED BRAKE ZONES: {referenceLap.Zones.Count}\n";
                details += "Ready for brake point alerts!\n";
            }
            
            TrackDetailsText.Text = details;
        }

        private string GetCategoryDisplayName(TrackCategory category)
        {
            return category switch
            {
                TrackCategory.RoadCourse => "🏎️ Road Course",
                TrackCategory.Oval => "🏁 Oval",
                TrackCategory.StreetCircuit => "🏞️ Street Circuit",
                TrackCategory.DirtOval => "🏕️ Dirt Oval",
                TrackCategory.DirtRoad => "🏕️ Dirt Road",
                _ => category.ToString()
            };
        }

        private string GetCornerTypeIcon(CornerType cornerType)
        {
            return cornerType switch
            {
                CornerType.Slow => "🔴",
                CornerType.Medium => "🟠", 
                CornerType.Fast => "🟢",
                CornerType.Chicane => "🟡",
                CornerType.Hairpin => "🔺",
                _ => "⚪"
            };
        }

        private void RenderTrackMap()
        {
            try
            {
                TrackMapCanvas.Children.Clear();
                
                if (selectedTrack == null) return;

                double canvasWidth = TrackMapCanvas.ActualWidth;
                double canvasHeight = TrackMapCanvas.ActualHeight;
                
                if (canvasWidth <= 10 || canvasHeight <= 10) return;

                // For now, render as enhanced circle with corner markers
                // TODO: Replace with actual track layout data
                RenderBasicTrackLayout(canvasWidth, canvasHeight);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Track render error: {ex.Message}";
            }
        }

        private void RenderBasicTrackLayout(double canvasWidth, double canvasHeight)
        {
            double centerX = canvasWidth / 2;
            double centerY = canvasHeight / 2;
            double radius = Math.Min(centerX, centerY) - 60;

            if (radius < 50) return;

            // Track surface
            var track = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                StrokeThickness = 12,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(track, centerX - radius);
            Canvas.SetTop(track, centerY - radius);
            TrackMapCanvas.Children.Add(track);

            // Track edges
            var innerEdge = new Ellipse
            {
                Width = (radius - 15) * 2,
                Height = (radius - 15) * 2,
                Stroke = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                StrokeThickness = 2,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(innerEdge, centerX - radius + 15);
            Canvas.SetTop(innerEdge, centerY - radius + 15);
            TrackMapCanvas.Children.Add(innerEdge);

            var outerEdge = new Ellipse
            {
                Width = (radius + 15) * 2,
                Height = (radius + 15) * 2,
                Stroke = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                StrokeThickness = 2,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(outerEdge, centerX - radius - 15);
            Canvas.SetTop(outerEdge, centerY - radius - 15);
            TrackMapCanvas.Children.Add(outerEdge);

            // Start/Finish line
            double startAngle = -Math.PI / 2;
            double startX = centerX + radius * Math.Cos(startAngle);
            double startY = centerY + radius * Math.Sin(startAngle);
            
            var startLine = new Rectangle
            {
                Width = 8,
                Height = 40,
                Fill = new SolidColorBrush(Colors.White)
            };
            Canvas.SetLeft(startLine, startX - 4);
            Canvas.SetTop(startLine, startY - 20);
            TrackMapCanvas.Children.Add(startLine);

            var sfLabel = new TextBlock
            {
                Text = "START/FINISH",
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold
            };
            Canvas.SetLeft(sfLabel, startX - 40);
            Canvas.SetTop(sfLabel, startY - 50);
            TrackMapCanvas.Children.Add(sfLabel);

            // Render corners from track data
            if (selectedTrack?.Corners != null)
            {
                for (int i = 0; i < selectedTrack.Corners.Count; i++)
                {
                    var corner = selectedTrack.Corners[i];
                    
                    // Distribute corners evenly around the circle for now
                    // TODO: Use actual corner positions from track data
                    double cornerAngle = startAngle + ((double)i / selectedTrack.Corners.Count) * 2 * Math.PI;
                    double cornerX = centerX + radius * Math.Cos(cornerAngle);
                    double cornerY = centerY + radius * Math.Sin(cornerAngle);

                    // Corner marker
                    var cornerMarker = new Ellipse
                    {
                        Width = 20,
                        Height = 20,
                        Fill = GetCornerColor(corner.Type)
                    };
                    cornerMarker.Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = ((SolidColorBrush)cornerMarker.Fill).Color,
                        BlurRadius = 6,
                        ShadowDepth = 0
                    };
                    Canvas.SetLeft(cornerMarker, cornerX - 10);
                    Canvas.SetTop(cornerMarker, cornerY - 10);
                    TrackMapCanvas.Children.Add(cornerMarker);

                    // Corner label
                    var cornerLabel = new TextBlock
                    {
                        Text = corner.Name,
                        Foreground = Brushes.White,
                        FontSize = 10,
                        FontWeight = FontWeights.Bold,
                        Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                        Padding = new Thickness(4, 2)
                    };
                    
                    // Position label outside the track
                    double labelRadius = radius + 35;
                    double labelX = centerX + labelRadius * Math.Cos(cornerAngle);
                    double labelY = centerY + labelRadius * Math.Sin(cornerAngle);
                    
                    Canvas.SetLeft(cornerLabel, labelX - 15);
                    Canvas.SetTop(cornerLabel, labelY - 8);
                    TrackMapCanvas.Children.Add(cornerLabel);
                    
                    // Corner number
                    var cornerNumber = new TextBlock
                    {
                        Text = (i + 1).ToString(),
                        Foreground = Brushes.White,
                        FontSize = 8,
                        FontWeight = FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Canvas.SetLeft(cornerNumber, cornerX - 6);
                    Canvas.SetTop(cornerNumber, cornerY - 6);
                    TrackMapCanvas.Children.Add(cornerNumber);
                }
            }

            // Add development info with auto-detection status
            bool isAutoDetected = detectedTrack != null && selectedTrack?.InternalName == detectedTrack.InternalName;
            
            string infoText = isAutoDetected ? 
                "🎯 LIVE TRACK - Auto-detected from iRacing!" : 
                "🚧 Development Preview - Real track layouts coming soon!";
            
            var devInfo = new TextBlock
            {
                Text = infoText,
                Foreground = new SolidColorBrush(isAutoDetected ? Color.FromRgb(0, 255, 102) : Color.FromRgb(255, 255, 0)),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                Padding = new Thickness(10, 5)
            };
            Canvas.SetLeft(devInfo, 20);
            Canvas.SetBottom(devInfo, 20);
            TrackMapCanvas.Children.Add(devInfo);
            
            // Add additional info for auto-detected tracks
            if (isAutoDetected && referenceLap.Zones.Count > 0)
            {
                var brakeZoneInfo = new TextBlock
                {
                    Text = $"🔴 {referenceLap.Zones.Count} brake zones recorded",
                    Foreground = new SolidColorBrush(Color.FromRgb(227, 30, 36)),
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                    Padding = new Thickness(10, 5)
                };
                Canvas.SetLeft(brakeZoneInfo, 20);
                Canvas.SetBottom(brakeZoneInfo, 50);
                TrackMapCanvas.Children.Add(brakeZoneInfo);
            }
        }

        private SolidColorBrush GetCornerColor(CornerType cornerType)
        {
            return cornerType switch
            {
                CornerType.Slow => new SolidColorBrush(Color.FromRgb(255, 0, 0)),      // Red
                CornerType.Medium => new SolidColorBrush(Color.FromRgb(255, 165, 0)),   // Orange
                CornerType.Fast => new SolidColorBrush(Color.FromRgb(0, 255, 0)),       // Green
                CornerType.Chicane => new SolidColorBrush(Color.FromRgb(255, 255, 0)),  // Yellow
                CornerType.Hairpin => new SolidColorBrush(Color.FromRgb(139, 0, 0)),    // Dark Red
                _ => new SolidColorBrush(Color.FromRgb(200, 200, 200))                  // Gray
            };
        }

        #endregion

        #region Original Brake Point Assistant Methods

        private void ApplyConfigToUI()
        {
            try
            {
                LeadDistanceSlider.Value = config.LeadMeters;
                LeadDistanceText.Text = $"{config.LeadMeters:F0}m";
                
                VolumeSlider.Value = config.ToneVolume;
                VolumeText.Text = $"{config.ToneVolume * 100:F0}%";
                
                // Always use countdown mode
                config.CountdownBeep = true;
                
                OnlyWhenNotBrakingCheck.IsChecked = config.OnlyAlertIfNotBraking;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"UI setup error: {ex.Message}";
            }
        }

        private void SetupTimer()
        {
            try
            {
                updateTimer = new DispatcherTimer();
                updateTimer.Interval = TimeSpan.FromMilliseconds(33); // ~30 FPS for smoother movement
                updateTimer.Tick += UpdateTimer_Tick;
                updateTimer.Start();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Timer error: {ex.Message}";
            }
        }

        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                // Only update telemetry when on Main tab
                if (currentTab == "Main")
                {
                    UpdateTelemetry();
                }
                else
                {
                    // Still check connection status on other tabs
                    UpdateConnectionStatus();
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Update error: {ex.Message}";
            }
        }

        private void UpdateConnectionStatus()
        {
            bool wasConnected = ConnectionStatus.Fill.ToString().Contains("00FF00");
            bool isConnected = false;
            
            try
            {
                isConnected = irsdk.IsConnected();
            }
            catch
            {
                isConnected = false;
            }

            if (isConnected != wasConnected)
            {
                var color = isConnected ? Colors.LimeGreen : Colors.Red;
                ConnectionStatus.Fill = new SolidColorBrush(color);
                ConnectionText.Text = isConnected ? "Connected" : "Disconnected";
                
                var effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = color,
                    BlurRadius = 8,
                    ShadowDepth = 0
                };
                ConnectionStatus.Effect = effect;
            }
        }

        private void UpdateTelemetry()
        {
            UpdateConnectionStatus();
            
            bool isConnected = ConnectionStatus.Fill.ToString().Contains("00FF00");
            if (!isConnected) return;

            try
            {
                var packet = ReadTelemetryPacket();
                
                // Add to history for better analysis
                telemetryHistory.Enqueue(packet);
                if (telemetryHistory.Count > 90) // Keep last 3 seconds at 30fps
                    telemetryHistory.Dequeue();

                // Update track info and auto-detect from database
                if (string.IsNullOrEmpty(referenceLap.Track) || referenceLap.TrackLength <= 0)
                {
                    referenceLap.Track = ReadTrackNameFromSession();
                    referenceLap.TrackLength = ReadTrackLengthFromSession();
                    if (referenceLap.TrackLength <= 0)
                        referenceLap.TrackLength = InferTrackLength(packet);
                    
                    if (referenceLap.TrackLength > 0)
                        TrackLengthText.Text = $"{referenceLap.TrackLength / 1000:F2}km";
                }

                // Auto-detect current track from database
                AutoDetectCurrentTrack();

                UpdateTelemetryDisplay(packet);
                
                bool lapWrapped = DetectLapWrap(packet);
                if (lapWrapped)
                {
                    HandleLapWrap(packet);
                }

                if (isRecording)
                {
                    HandleRecording(packet);
                }

                HandleAlerts(packet);
                UpdateEnhancedTrackVisualization(packet);

                lastLapPctSeen = packet.LapDistPct;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Telemetry error: {ex.Message}";
            }
        }

        // [Continue with all the original telemetry and brake point methods...]
        // The rest of the methods remain the same as the previous version

        private bool DetectLapWrap(TelemetryPacket packet)
        {
            bool pctWrap = (packet.LapDistPct < 0.2 && lastLapPctSeen > 0.8);
            
            bool lapNumberIncreased = false;
            if (telemetryHistory.Count > 0)
            {
                var lastPacket = telemetryHistory.Last();
                lapNumberIncreased = packet.Lap > lastPacket.Lap;
            }

            bool distanceWrap = false;
            if (referenceLap.TrackLength > 0 && telemetryHistory.Count > 0)
            {
                var lastPacket = telemetryHistory.Last();
                float lastDist = lastPacket.LapDist;
                float currentDist = packet.LapDist;
                
                if (lastDist > (referenceLap.TrackLength * 0.8f) && currentDist < (referenceLap.TrackLength * 0.2f))
                {
                    distanceWrap = true;
                }
            }

            return pctWrap || lapNumberIncreased || distanceWrap;
        }

        private TelemetryPacket ReadTelemetryPacket()
        {
            float GetFloat(string name) { try { return (float)irsdk.GetData(name); } catch { return 0f; } }
            int GetInt(string name) { try { return (int)irsdk.GetData(name); } catch { return 0; } }
            double GetDouble(string name) { try { return (double)irsdk.GetData(name); } catch { return 0.0; } }

            return new TelemetryPacket
            {
                SessionTime = GetDouble("SessionTime"),
                Lap = GetInt("Lap"),
                LapDist = GetFloat("LapDist"),
                LapDistPct = GetFloat("LapDistPct"),
                Speed = GetFloat("Speed"),
                Throttle = GetFloat("Throttle"),
                Brake = GetFloat("Brake"),
            };
        }

        private void UpdateTelemetryDisplay(TelemetryPacket packet)
        {
            // Show track name with auto-detection indicator
            string trackDisplayName = string.IsNullOrEmpty(referenceLap.Track) ? "Loading..." : referenceLap.Track;
            if (detectedTrack != null)
            {
                trackDisplayName = $"🎯 {detectedTrack.DisplayName}";
            }
            TrackNameText.Text = trackDisplayName;
            
            LapNumberText.Text = packet.Lap.ToString();
            SpeedText.Text = $"{packet.Speed * 3.6f:F0} km/h";
            ZoneCountText.Text = referenceLap.Zones.Count.ToString();
            BrakeText.Text = $"{packet.Brake * 100:F0}%";
            LapDistText.Text = $"{packet.LapDist:F0}m";

            try
            {
                float currentLapTime = (float)irsdk.GetData("LapLastLapTime");
                if (currentLapTime > 0 && Math.Abs(currentLapTime - lastLapTime) > 0.1f)
                {
                    lastLapTime = currentLapTime;
                    var minutes = (int)(currentLapTime / 60);
                    var seconds = currentLapTime % 60;
                    LastLapTimeText.Text = $"Last Lap: {minutes:D2}:{seconds:05.2f}";
                }
            }
            catch { }
        }

        private void HandleLapWrap(TelemetryPacket packet)
        {
            firedThisLap.Clear();
            zoneStageMaskThisLap.Clear();

            if (isRecording)
            {
                if (referenceLap.Zones.Count > 0 && recordingLap >= 0)
                {
                    try
                    {
                        float lapTime = (float)irsdk.GetData("LapLastLapTime");
                        SaveReferenceForLap(referenceLap, recordingLap, lapTime);
                        lastSaveTime = DateTime.Now;
                    }
                    catch { }
                }

                recordingLap = packet.Lap;
                referenceLap.Zones.Clear();
                lastZoneAt = -9999f;
                prevBraking = false;
                inCorner = false;
                
                StatusText.Text = $"Recording Lap {recordingLap} - Drive to record brake points";
            }
        }

        private void HandleRecording(TelemetryPacket packet)
        {
            if (recordingLap == -1 && packet.LapDistPct > 0.05f)
            {
                recordingLap = packet.Lap;
                referenceLap.Zones.Clear();
                StatusText.Text = $"Recording Lap {recordingLap} at {referenceLap.Track}";
                lastZoneAt = -9999f;
                prevBraking = packet.Brake >= config.BrakeOnThreshold;
                inCorner = false;
            }

            if (recordingLap == -1) return;

            float distanceSinceLast = (lastZoneAt < -9000f || referenceLap.TrackLength <= 0)
                ? float.MaxValue
                : DistanceAhead(referenceLap.TrackLength, lastZoneAt, packet.LapDist);

            bool brakingNow = packet.Brake >= config.BrakeOnThreshold;
            bool clearToRearm = (packet.Brake <= config.BrakeOffThreshold) && 
                               (distanceSinceLast >= config.CornerResetMeters);
            
            if (clearToRearm) inCorner = false;

            bool risingEdge = brakingNow && !prevBraking && !inCorner;

            if (risingEdge && (packet.LapDist - lastZoneAt >= config.MinZoneGapMeters))
            {
                float speedDrop = Math.Max(0, prevSpeed - packet.Speed);
                if (config.MinSpeedDropMs <= 0 || speedDrop >= config.MinSpeedDropMs)
                {
                    referenceLap.Zones.Add(new BrakeZone 
                    { 
                        LapDist = packet.LapDist, 
                        EntrySpeed = packet.Speed 
                    });
                    lastZoneAt = packet.LapDist;
                    inCorner = true;
                    
                    StatusText.Text = $"Brake zone {referenceLap.Zones.Count} recorded at {packet.LapDist:F1}m ({packet.Speed * 3.6f:F0} km/h)";
                }
            }

            prevBraking = brakingNow;
            prevSpeed = packet.Speed;
        }

        private void HandleAlerts(TelemetryPacket packet)
        {
            if (referenceLap.Zones.Count == 0 || referenceLap.TrackLength <= 0) return;
            if (packet.Speed < config.SpeedThresholdMs) return;
            if (config.OnlyAlertIfNotBraking && packet.Brake >= config.BrakeOnThreshold) return;

            float bestAhead = float.MaxValue;
            int bestIdx = -1;
            for (int i = 0; i < referenceLap.Zones.Count; i++)
            {
                var zone = referenceLap.Zones[i];
                float ahead = DistanceAhead(referenceLap.TrackLength, packet.LapDist, zone.LapDist);
                if (ahead < bestAhead) { bestAhead = ahead; bestIdx = i; }
            }
            if (bestIdx < 0) return;

            float d1 = Math.Max(1f, config.LeadMeters);
            float d2 = Math.Max(1f, config.LeadMeters * config.CountdownRatio2);
            float d3 = Math.Max(1f, config.LeadMeters * config.CountdownRatio3);

            float[] thresholds = { d1, d2, d3 };
            float[] frequencies = { 600f, 900f, 1200f };
            string[] labels = { "3", "2", "1" };

            int mask = zoneStageMaskThisLap.TryGetValue(bestIdx, out var m) ? m : 0;

            for (int stage = 0; stage < thresholds.Length; stage++)
            {
                float threshold = thresholds[stage];
                int bit = 1 << stage;

                if (bestAhead <= threshold && (mask & bit) == 0)
                {
                    zoneStageMaskThisLap[bestIdx] = mask | bit;
                    _ = player.PlaySingleAsync(frequencies[stage], 120, config.ToneVolume);
                    StatusText.Text = $"🟡 Countdown {labels[stage]} - Zone {bestIdx + 1} at {bestAhead:F0}m";
                    mask |= bit;
                }
            }

            if (zoneStageMaskThisLap.TryGetValue(bestIdx, out var finalMask) && finalMask == 0b111)
                firedThisLap.Add(bestIdx);
        }

        private void UpdateEnhancedTrackVisualization(TelemetryPacket packet)
        {
            try
            {
                if (currentTab != "Main") return; // Only update on main tab
                
                TrackCanvas.Children.Clear();

                if (referenceLap.TrackLength <= 0) return;

                double canvasWidth = TrackCanvas.ActualWidth;
                double canvasHeight = TrackCanvas.ActualHeight;
                
                if (canvasWidth <= 10 || canvasHeight <= 10) return;

                DrawEnhancedCircularTrack(packet, canvasWidth, canvasHeight);
            }
            catch
            {
                // Ignore visualization errors
            }
        }

        private void DrawEnhancedCircularTrack(TelemetryPacket packet, double canvasWidth, double canvasHeight)
        {
            double centerX = canvasWidth / 2;
            double centerY = canvasHeight / 2;
            double radius = Math.Min(centerX, centerY) - 40;

            if (radius < 30) return;

            // Track surface with multiple layers for depth
            var trackOuter = new Ellipse
            {
                Width = (radius + 8) * 2,
                Height = (radius + 8) * 2,
                Stroke = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
                StrokeThickness = 4,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(trackOuter, centerX - radius - 8);
            Canvas.SetTop(trackOuter, centerY - radius - 8);
            TrackCanvas.Children.Add(trackOuter);

            var trackMain = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                StrokeThickness = 8,
                Fill = Brushes.Transparent
            };
            trackMain.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(255, 102, 0),
                BlurRadius = 4,
                ShadowDepth = 0,
                Opacity = 0.3
            };
            Canvas.SetLeft(trackMain, centerX - radius);
            Canvas.SetTop(trackMain, centerY - radius);
            TrackCanvas.Children.Add(trackMain);

            var trackInner = new Ellipse
            {
                Width = (radius - 8) * 2,
                Height = (radius - 8) * 2,
                Stroke = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                StrokeThickness = 2,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(trackInner, centerX - radius + 8);
            Canvas.SetTop(trackInner, centerY - radius + 8);
            TrackCanvas.Children.Add(trackInner);

            double startAngle = -Math.PI / 2;
            double startX = centerX + radius * Math.Cos(startAngle);
            double startY = centerY + radius * Math.Sin(startAngle);
            
            var startLine = new Rectangle
            {
                Width = 6,
                Height = 30,
                Fill = new SolidColorBrush(Colors.White),
                RenderTransform = new RotateTransform(0, 3, 15)
            };
            Canvas.SetLeft(startLine, startX - 3);
            Canvas.SetTop(startLine, startY - 15);
            TrackCanvas.Children.Add(startLine);

            var sfText = new TextBlock
            {
                Text = "S/F",
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold
            };
            Canvas.SetLeft(sfText, startX - 12);
            Canvas.SetTop(sfText, startY - 35);
            TrackCanvas.Children.Add(sfText);

            // Brake zones with enhanced graphics
            for (int i = 0; i < referenceLap.Zones.Count; i++)
            {
                var zone = referenceLap.Zones[i];
                double zonePct = zone.LapDist / referenceLap.TrackLength;
                double zoneAngle = startAngle + (zonePct * 2 * Math.PI);
                double zoneX = centerX + radius * Math.Cos(zoneAngle);
                double zoneY = centerY + radius * Math.Sin(zoneAngle);

                var zoneDot = new Ellipse
                {
                    Width = 16,
                    Height = 16,
                    Fill = new SolidColorBrush(Color.FromRgb(227, 30, 36))
                };
                zoneDot.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Color.FromRgb(227, 30, 36),
                    BlurRadius = 8,
                    ShadowDepth = 0
                };
                Canvas.SetLeft(zoneDot, zoneX - 8);
                Canvas.SetTop(zoneDot, zoneY - 8);
                TrackCanvas.Children.Add(zoneDot);

                var zoneNumber = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Canvas.SetLeft(zoneNumber, zoneX - 6);
                Canvas.SetTop(zoneNumber, zoneY - 7);
                TrackCanvas.Children.Add(zoneNumber);
            }

            // Current position
            double currentAngle = startAngle + (packet.LapDistPct * 2 * Math.PI);
            double carX = centerX + radius * Math.Cos(currentAngle);
            double carY = centerY + radius * Math.Sin(currentAngle);

            var carDot = new Ellipse
            {
                Width = 20,
                Height = 20,
                Fill = new SolidColorBrush(Color.FromRgb(0, 255, 102))
            };
            carDot.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(0, 255, 102),
                BlurRadius = 12,
                ShadowDepth = 0
            };
            Canvas.SetLeft(carDot, carX - 10);
            Canvas.SetTop(carDot, carY - 10);
            TrackCanvas.Children.Add(carDot);
        }

        private float DistanceAhead(float trackLength, float fromPos, float toPos)
        {
            float distance = toPos - fromPos;
            if (distance < 0) distance += trackLength;
            return distance;
        }

        private float InferTrackLength(TelemetryPacket packet)
        {
            if (packet.LapDistPct > 0.01f)
            {
                var guess = packet.LapDist / Math.Max(0.01f, packet.LapDistPct);
                if (guess > 500 && guess < 10000) return (float)guess;
            }
            float parsed = ReadTrackLengthFromSession();
            return parsed > 0 ? parsed : 4000f;
        }

        private string ReadTrackNameFromSession()
        {
            try
            {
                var raw = irsdk.GetSessionInfo();
                if (string.IsNullOrEmpty(raw)) return "";
                var match = Regex.Match(raw, @"TrackDisplayName:\s*(.*)");
                if (match.Success)
                {
                    string trackName = match.Groups[1].Value.Trim();
                    return CleanTrackName(trackName);
                }
            }
            catch { }
            return "";
        }

        private string CleanTrackName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return rawName;
            
            var cleaned = rawName
                .Replace(" - ", " ")
                .Replace("_", " ")
                .Trim();
            
            var knownTracks = new Dictionary<string, string>
            {
                { "laguna", "Laguna Seca" },
                { "watkins", "Watkins Glen" },
                { "sebring", "Sebring International Raceway" },
                { "spa", "Spa-Francorchamps" },
                { "nurburgring", "Nürburgring" },
                { "silverstone", "Silverstone Circuit" },
                { "monza", "Autodromo Nazionale Monza" },
                { "brands", "Brands Hatch" },
                { "suzuka", "Suzuka Circuit" },
                { "road america", "Road America" },
                { "mid ohio", "Mid-Ohio Sports Car Course" },
                { "lime rock", "Lime Rock Park" },
                { "virginia", "Virginia International Raceway" },
                { "charlotte", "Charlotte Motor Speedway" },
                { "atlanta", "Atlanta Motor Speedway" },
                { "phoenix", "Phoenix Raceway" },
                { "sonoma", "Sonoma Raceway" },
                { "daytona", "Daytona International Speedway" },
                { "talladega", "Talladega Superspeedway" },
                { "indianapolis", "Indianapolis Motor Speedway" },
                { "michigan", "Michigan International Speedway" },
                { "texas", "Texas Motor Speedway" },
                { "dover", "Dover Motor Speedway" },
                { "pocono", "Pocono Raceway" },
                { "interlagos", "Autódromo José Carlos Pace" },
                { "hungaroring", "Hungaroring" },
                { "imola", "Autodromo Enzo e Dino Ferrari" },
                { "barcelona", "Circuit de Barcelona-Catalunya" },
                { "zandvoort", "Circuit Zandvoort" },
                { "hockenheim", "Hockenheimring" },
                { "paul ricard", "Circuit Paul Ricard" },
                { "eldora", "Eldora Speedway" },
                { "knoxville", "Knoxville Raceway" },
                { "williams grove", "Williams Grove Speedway" },
                { "long beach", "Streets of Long Beach" },
                { "belle isle", "Detroit Belle Isle" },
                { "toronto", "Streets of Toronto" }
            };
            
            var lowerCleaned = cleaned.ToLowerInvariant();
            foreach (var kvp in knownTracks)
            {
                if (lowerCleaned.Contains(kvp.Key))
                {
                    return kvp.Value;
                }
            }
            
            return cleaned;
        }

        private float ReadTrackLengthFromSession()
        {
            try
            {
                var raw = irsdk.GetSessionInfo();
                if (string.IsNullOrEmpty(raw)) return 0f;

                var match = Regex.Match(raw, @"TrackLength:\s*([0-9.]+)\s*(km|mi)", RegexOptions.IgnoreCase);
                if (!match.Success) return 0f;

                float val = float.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                string unit = match.Groups[2].Value.ToLowerInvariant();
                if (unit == "km") return val * 1000f;
                if (unit == "mi") return val * 1609.344f;
            }
            catch { }
            return 0f;
        }

        private void LoadOrCreateConfig()
        {
            try
            {
                string configFile = System.IO.Path.Combine(DataFolder, "config.json");
                if (File.Exists(configFile))
                {
                    var text = File.ReadAllText(configFile);
                    config = JsonSerializer.Deserialize<Config>(text) ?? new Config();
                }
                else if (File.Exists(ConfigPath))
                {
                    var text = File.ReadAllText(ConfigPath);
                    config = JsonSerializer.Deserialize<Config>(text) ?? new Config();
                }
                else
                {
                    SaveConfig();
                }
            }
            catch
            {
                config = new Config();
            }
        }

        private void SaveConfig()
        {
            try
            {
                string configFile = System.IO.Path.Combine(DataFolder, "config.json");
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configFile, json);
            }
            catch { }
        }

        private void LoadReferenceIfExists()
        {
            try
            {
                string refFile = System.IO.Path.Combine(DataFolder, "latest_reference.json");
                if (File.Exists(refFile))
                {
                    var json = File.ReadAllText(refFile);
                    referenceLap = JsonSerializer.Deserialize<ReferenceLap>(json) ?? new ReferenceLap();
                    StatusText.Text = $"Loaded reference: {referenceLap.Track} ({referenceLap.Zones.Count} zones)";
                }
                else if (File.Exists(DefaultRefPath))
                {
                    var json = File.ReadAllText(DefaultRefPath);
                    referenceLap = JsonSerializer.Deserialize<ReferenceLap>(json) ?? new ReferenceLap();
                    StatusText.Text = $"Loaded reference: {referenceLap.Track} ({referenceLap.Zones.Count} zones)";
                }
            }
            catch { }
        }

        private void SaveReference()
        {
            try
            {
                if (referenceLap.Zones.Count == 0)
                {
                    MessageBox.Show("No brake zones to save. Record a lap first.", "No Data", 
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                float lapTime = 0f;
                try { lapTime = (float)irsdk.GetData("LapLastLapTime"); } catch { }

                string safeTrack = string.Join("_", (referenceLap.Track ?? "track").Split(System.IO.Path.GetInvalidFileNameChars()));
                string lapPart = (recordingLap >= 0) ? $"lap{recordingLap}" : "manual_save";
                string timePart = (lapTime > 0) ? $"_{lapTime:0.000}s" : "";
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string fileName = System.IO.Path.Combine(DataFolder, $"reference_{safeTrack}_{lapPart}{timePart}_{timestamp}.json");

                var json = JsonSerializer.Serialize(referenceLap, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(fileName, json);
                
                string latestFile = System.IO.Path.Combine(DataFolder, "latest_reference.json");
                File.WriteAllText(latestFile, json);
                
                StatusText.Text = $"✅ Saved reference: {referenceLap.Zones.Count} zones to {System.IO.Path.GetFileName(fileName)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save reference: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveReferenceForLap(ReferenceLap lapData, int lapNumber, float lapTimeSec)
        {
            try
            {
                if (lapData.Zones.Count == 0) return;

                string safeTrack = string.Join("_", (lapData.Track ?? "track").Split(System.IO.Path.GetInvalidFileNameChars()));
                string timePart = (lapTimeSec > 0) ? $"_{lapTimeSec:0.000}s" : "";
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string fileName = System.IO.Path.Combine(DataFolder, $"auto_save_{safeTrack}_lap{lapNumber}{timePart}_{timestamp}.json");

                var json = JsonSerializer.Serialize(lapData, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(fileName, json);
                
                string latestFile = System.IO.Path.Combine(DataFolder, "latest_reference.json");
                File.WriteAllText(latestFile, json);
                
                StatusText.Text = $"💾 Auto-saved: Lap {lapNumber} ({lapData.Zones.Count} zones) - {lapTimeSec:0.000}s";
            }
            catch { }
        }

        #endregion

        #region Event Handlers

        private void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!isRecording)
                {
                    isRecording = true;
                    recordingLap = -1;
                    referenceLap = new ReferenceLap
                    {
                        Track = ReadTrackNameFromSession(),
                        TrackLength = ReadTrackLengthFromSession()
                    };
                    lastZoneAt = -9999f;
                    prevBraking = false;
                    inCorner = false;
                    
                    RecordButton.Content = "⏹ STOP RECORDING";
                    RecordButton.Background = new SolidColorBrush(Color.FromRgb(255, 102, 0));
                    StatusText.Text = "🔴 Recording started - Drive your reference lap";
                }
                else
                {
                    isRecording = false;
                    
                    if (referenceLap.Zones.Count > 0)
                    {
                        SaveReference();
                    }
                    
                    RecordButton.Content = "▶ START RECORDING";
                    RecordButton.Background = new SolidColorBrush(Color.FromRgb(227, 30, 36));
                    StatusText.Text = "⏹ Recording stopped";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Record error: {ex.Message}";
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            SaveReference();
        }

        private void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                    Title = "Load Brake Point Reference",
                    InitialDirectory = System.IO.Path.GetFullPath(DataFolder)
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    var json = File.ReadAllText(openFileDialog.FileName);
                    referenceLap = JsonSerializer.Deserialize<ReferenceLap>(json) ?? new ReferenceLap();
                    StatusText.Text = $"📂 Loaded: {System.IO.Path.GetFileName(openFileDialog.FileName)} ({referenceLap.Zones.Count} zones)";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load reference: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearZonesButton_Click(object sender, RoutedEventArgs e)
        {
            if (referenceLap.Zones.Count > 0)
            {
                var result = MessageBox.Show("Are you sure you want to clear all brake zones?", 
                    "Clear Zones", MessageBoxButton.YesNo, MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    referenceLap.Zones.Clear();
                    StatusText.Text = "🗑️ All brake zones cleared";
                }
            }
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!Directory.Exists(DataFolder))
                    Directory.CreateDirectory(DataFolder);
                
                Process.Start("explorer.exe", System.IO.Path.GetFullPath(DataFolder));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LeadDistanceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (config != null && LeadDistanceText != null)
            {
                config.LeadMeters = (float)e.NewValue;
                LeadDistanceText.Text = $"{config.LeadMeters:F0}m";
                SaveConfig();
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (config != null && VolumeText != null)
            {
                config.ToneVolume = (float)e.NewValue;
                VolumeText.Text = $"{config.ToneVolume * 100:F0}%";
                SaveConfig();
            }
        }

        private void OnlyWhenNotBrakingCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (config != null && OnlyWhenNotBrakingCheck != null)
            {
                config.OnlyAlertIfNotBraking = OnlyWhenNotBrakingCheck.IsChecked == true;
                SaveConfig();
            }
        }

        private void TestBeepButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _ = player.PlaySingleAsync(900f, 120, config.ToneVolume);
                StatusText.Text = "🔊 Test beep played (Countdown tone)";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Test beep error: {ex.Message}";
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                updateTimer?.Stop();
                SaveConfig();
                
                if (isRecording && referenceLap.Zones.Count > 0)
                {
                    SaveReference();
                }
            }
            catch { }
            base.OnClosed(e);
        }

        #endregion

        #region Helper Methods - Include all remaining methods from previous implementation

        // ... All the remaining helper methods go here ...
        // (SaveConfig, LoadConfig, SaveReference, etc.)

        #endregion
    }

    #region Track Map Data Structures

    public enum TrackCategory
    {
        RoadCourse,
        Oval,
        StreetCircuit,
        DirtOval,
        DirtRoad
    }

    public enum CornerType
    {
        Slow,
        Medium,
        Fast,
        Chicane,
        Hairpin
    }

    public class Corner
    {
        public string Name { get; set; } = "";
        public CornerType Type { get; set; } = CornerType.Medium;
        public float DistanceMeters { get; set; }
        public float RecommendedBrakingPoint { get; set; }
        public float EntrySpeed { get; set; }
        public float ExitSpeed { get; set; }
    }

    public class TrackLayout
    {
        public string InternalName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Configuration { get; set; } = "";
        public TrackCategory Category { get; set; }
        public string Country { get; set; } = "";
        public float LengthKm { get; set; }
        public List<Corner> Corners { get; set; } = new();
        public string Description { get; set; } = "";
    }

    public class TrackDatabase
    {
        public List<TrackLayout> Tracks { get; set; } = new();

        public void LoadTrackDatabase()
        {
            // Comprehensive iRacing track database
            Tracks.AddRange(new[]
            {
                #region NASCAR Cup Series Ovals
                
                new TrackLayout
                {
                    InternalName = "atlanta",
                    DisplayName = "Atlanta Motor Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 2.414f,
                    Description = "1.5-mile quad-oval with progressive banking",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 600 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 1200 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 1800 },
                        new Corner { Name = "Turn 4", Type = CornerType.Fast, DistanceMeters = 2400 }
                    }
                },
                
                new TrackLayout
                {
                    InternalName = "auto_club_speedway",
                    DisplayName = "Auto Club Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 3.218f,
                    Description = "2-mile D-shaped oval with wide racing surface",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 800 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 1600 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 2400 },
                        new Corner { Name = "Turn 4", Type = CornerType.Fast, DistanceMeters = 3200 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "bristol",
                    DisplayName = "Bristol Motor Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 0.854f,
                    Description = "0.533-mile high-banked concrete oval",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 210 },
                        new Corner { Name = "Turn 2", Type = CornerType.Medium, DistanceMeters = 420 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 630 },
                        new Corner { Name = "Turn 4", Type = CornerType.Medium, DistanceMeters = 840 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "charlotte",
                    DisplayName = "Charlotte Motor Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 2.414f,
                    Description = "1.5-mile quad-oval, home of NASCAR All-Star Race",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 600 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 1200 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 1800 },
                        new Corner { Name = "Turn 4", Type = CornerType.Fast, DistanceMeters = 2400 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "chicagoland",
                    DisplayName = "Chicagoland Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 2.414f,
                    Description = "1.5-mile D-shaped oval",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 600 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 1200 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 1800 },
                        new Corner { Name = "Turn 4", Type = CornerType.Fast, DistanceMeters = 2400 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "darlington",
                    DisplayName = "Darlington Raceway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 2.173f,
                    Description = "1.366-mile egg-shaped oval, 'Too Tough to Tame'",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 540 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 1080 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 1620 },
                        new Corner { Name = "Turn 4", Type = CornerType.Fast, DistanceMeters = 2160 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "daytona",
                    DisplayName = "Daytona International Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 4.024f,
                    Description = "2.5-mile superspeedway, home of the Daytona 500",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 1000 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 2000 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 3000 },
                        new Corner { Name = "Turn 4", Type = CornerType.Fast, DistanceMeters = 4000 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "dover",
                    DisplayName = "Dover Motor Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 1.609f,
                    Description = "1-mile concrete oval, 'Monster Mile'",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 400 },
                        new Corner { Name = "Turn 2", Type = CornerType.Medium, DistanceMeters = 800 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 1200 },
                        new Corner { Name = "Turn 4", Type = CornerType.Medium, DistanceMeters = 1600 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "homestead",
                    DisplayName = "Homestead-Miami Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 2.414f,
                    Description = "1.5-mile oval with variable banking",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 600 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 1200 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 1800 },
                        new Corner { Name = "Turn 4", Type = CornerType.Fast, DistanceMeters = 2400 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "indianapolis",
                    DisplayName = "Indianapolis Motor Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 4.023f,
                    Description = "2.5-mile rectangular oval, 'The Brickyard'",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 1000 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 2000 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 3000 },
                        new Corner { Name = "Turn 4", Type = CornerType.Fast, DistanceMeters = 4000 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "kansas",
                    DisplayName = "Kansas Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 2.414f,
                    Description = "1.5-mile D-shaped oval",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 600 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 1200 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 1800 },
                        new Corner { Name = "Turn 4", Type = CornerType.Fast, DistanceMeters = 2400 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "kentucky",
                    DisplayName = "Kentucky Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 2.414f,
                    Description = "1.5-mile tri-oval",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 600 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 1200 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 1800 },
                        new Corner { Name = "Turn 4", Type = CornerType.Fast, DistanceMeters = 2400 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "las_vegas",
                    DisplayName = "Las Vegas Motor Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 2.414f,
                    Description = "1.5-mile D-shaped oval",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 600 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 1200 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 1800 },
                        new Corner { Name = "Turn 4", Type = CornerType.Fast, DistanceMeters = 2400 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "martinsville",
                    DisplayName = "Martinsville Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 0.846f,
                    Description = "0.526-mile paperclip-shaped oval",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Slow, DistanceMeters = 210 },
                        new Corner { Name = "Turn 2", Type = CornerType.Slow, DistanceMeters = 420 },
                        new Corner { Name = "Turn 3", Type = CornerType.Slow, DistanceMeters = 630 },
                        new Corner { Name = "Turn 4", Type = CornerType.Slow, DistanceMeters = 840 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "michigan",
                    DisplayName = "Michigan International Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 3.218f,
                    Description = "2-mile D-shaped superspeedway",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 800 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 1600 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 2400 },
                        new Corner { Name = "Turn 4", Type = CornerType.Fast, DistanceMeters = 3200 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "nashville_superspeedway",
                    DisplayName = "Nashville Superspeedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 2.28f,
                    Description = "1.33-mile concrete D-shaped oval",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 570 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 1140 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 1710 },
                        new Corner { Name = "Turn 4", Type = CornerType.Fast, DistanceMeters = 2280 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "new_hampshire",
                    DisplayName = "New Hampshire Motor Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 1.748f,
                    Description = "1.058-mile oval with low banking",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 440 },
                        new Corner { Name = "Turn 2", Type = CornerType.Medium, DistanceMeters = 880 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 1320 },
                        new Corner { Name = "Turn 4", Type = CornerType.Medium, DistanceMeters = 1760 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "phoenix",
                    DisplayName = "Phoenix Raceway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 1.609f,
                    Description = "1-mile D-shaped oval with unique dogleg",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 400 },
                        new Corner { Name = "Turn 2", Type = CornerType.Medium, DistanceMeters = 800 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 1200 },
                        new Corner { Name = "Turn 4", Type = CornerType.Medium, DistanceMeters = 1600 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "pocono",
                    DisplayName = "Pocono Raceway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 4.023f,
                    Description = "2.5-mile triangular superspeedway, 'Tricky Triangle'",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 1340 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 2680 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 4020 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "richmond",
                    DisplayName = "Richmond Raceway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 1.207f,
                    Description = "0.75-mile D-shaped oval",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 300 },
                        new Corner { Name = "Turn 2", Type = CornerType.Medium, DistanceMeters = 600 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 900 },
                        new Corner { Name = "Turn 4", Type = CornerType.Medium, DistanceMeters = 1200 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "talladega",
                    DisplayName = "Talladega Superspeedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 4.281f,
                    Description = "2.66-mile superspeedway, fastest NASCAR track",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 1070 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 2140 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 3210 },
                        new Corner { Name = "Turn 4", Type = CornerType.Fast, DistanceMeters = 4280 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "texas",
                    DisplayName = "Texas Motor Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 2.414f,
                    Description = "1.5-mile quad-oval with high banking",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 600 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 1200 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 1800 },
                        new Corner { Name = "Turn 4", Type = CornerType.Fast, DistanceMeters = 2400 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "watkins_glen",
                    DisplayName = "Watkins Glen International",
                    Configuration = "Cup",
                    Category = TrackCategory.RoadCourse,
                    Country = "USA",
                    LengthKm = 3.515f,
                    Description = "Historic road course in upstate New York",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 450 },
                        new Corner { Name = "The Esses", Type = CornerType.Chicane, DistanceMeters = 900 },
                        new Corner { Name = "The 90", Type = CornerType.Slow, DistanceMeters = 1400 },
                        new Corner { Name = "Turn 6", Type = CornerType.Fast, DistanceMeters = 2100 },
                        new Corner { Name = "Turn 7", Type = CornerType.Fast, DistanceMeters = 2800 },
                        new Corner { Name = "The Bus Stop", Type = CornerType.Chicane, DistanceMeters = 3200 },
                        new Corner { Name = "Turn 11", Type = CornerType.Fast, DistanceMeters = 3500 }
                    }
                },

                // Additional NASCAR Cup Series Ovals

                new TrackLayout
                {
                    InternalName = "gateway_motorsports_park",
                    DisplayName = "World Wide Technology Raceway (Gateway)",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 2.012f,
                    Description = "1.25-mile flat oval near St. Louis",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 500 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 1000 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 1500 },
                        new Corner { Name = "Turn 4", Type = CornerType.Fast, DistanceMeters = 2000 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "north_wilkesboro",
                    DisplayName = "North Wilkesboro Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 1.003f,
                    Description = "Historic 0.625-mile oval, recently returned to NASCAR",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 250 },
                        new Corner { Name = "Turn 2", Type = CornerType.Medium, DistanceMeters = 500 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 750 },
                        new Corner { Name = "Turn 4", Type = CornerType.Medium, DistanceMeters = 1000 }
                    }
                },

                // Short Track Ovals

                new TrackLayout
                {
                    InternalName = "lucas_oil_raceway",
                    DisplayName = "Lucas Oil Raceway (IRP)",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 1.448f,
                    Description = "0.686-mile oval in Indianapolis",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 360 },
                        new Corner { Name = "Turn 2", Type = CornerType.Medium, DistanceMeters = 720 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 1080 },
                        new Corner { Name = "Turn 4", Type = CornerType.Medium, DistanceMeters = 1440 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "concord_speedway",
                    DisplayName = "Concord Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 0.644f,
                    Description = "0.4-mile short track oval",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Slow, DistanceMeters = 160 },
                        new Corner { Name = "Turn 2", Type = CornerType.Slow, DistanceMeters = 320 },
                        new Corner { Name = "Turn 3", Type = CornerType.Slow, DistanceMeters = 480 },
                        new Corner { Name = "Turn 4", Type = CornerType.Slow, DistanceMeters = 640 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "five_flags_speedway",
                    DisplayName = "Five Flags Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 0.805f,
                    Description = "0.5-mile high-banked oval in Pensacola, FL",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 200 },
                        new Corner { Name = "Turn 2", Type = CornerType.Medium, DistanceMeters = 400 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 600 },
                        new Corner { Name = "Turn 4", Type = CornerType.Medium, DistanceMeters = 800 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "irwindale_speedway",
                    DisplayName = "Irwindale Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 0.805f,
                    Description = "0.5-mile paved oval in California",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 200 },
                        new Corner { Name = "Turn 2", Type = CornerType.Medium, DistanceMeters = 400 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 600 },
                        new Corner { Name = "Turn 4", Type = CornerType.Medium, DistanceMeters = 800 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "kern_county_raceway",
                    DisplayName = "Kern County Raceway Park",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 0.563f,
                    Description = "0.35-mile asphalt oval in California",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Slow, DistanceMeters = 140 },
                        new Corner { Name = "Turn 2", Type = CornerType.Slow, DistanceMeters = 280 },
                        new Corner { Name = "Turn 3", Type = CornerType.Slow, DistanceMeters = 420 },
                        new Corner { Name = "Turn 4", Type = CornerType.Slow, DistanceMeters = 560 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "langley_speedway",
                    DisplayName = "Langley Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 0.644f,
                    Description = "0.4-mile asphalt oval in Virginia",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Slow, DistanceMeters = 160 },
                        new Corner { Name = "Turn 2", Type = CornerType.Slow, DistanceMeters = 320 },
                        new Corner { Name = "Turn 3", Type = CornerType.Slow, DistanceMeters = 480 },
                        new Corner { Name = "Turn 4", Type = CornerType.Slow, DistanceMeters = 640 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "lanier_national_speedway",
                    DisplayName = "Lanier National Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 0.563f,
                    Description = "3/8-mile asphalt oval in Georgia",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Slow, DistanceMeters = 140 },
                        new Corner { Name = "Turn 2", Type = CornerType.Slow, DistanceMeters = 280 },
                        new Corner { Name = "Turn 3", Type = CornerType.Slow, DistanceMeters = 420 },
                        new Corner { Name = "Turn 4", Type = CornerType.Slow, DistanceMeters = 560 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "myrtle_beach_speedway",
                    DisplayName = "Myrtle Beach Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 0.805f,
                    Description = "0.538-mile semi-banked asphalt oval",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 200 },
                        new Corner { Name = "Turn 2", Type = CornerType.Medium, DistanceMeters = 400 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 600 },
                        new Corner { Name = "Turn 4", Type = CornerType.Medium, DistanceMeters = 800 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "nashville_fairgrounds",
                    DisplayName = "Nashville Fairgrounds Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 0.896f,
                    Description = "0.596-mile concrete oval, America's fastest short track",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 220 },
                        new Corner { Name = "Turn 2", Type = CornerType.Medium, DistanceMeters = 440 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 660 },
                        new Corner { Name = "Turn 4", Type = CornerType.Medium, DistanceMeters = 880 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "new_smyrna_speedway",
                    DisplayName = "New Smyrna Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 0.805f,
                    Description = "0.5-mile high-banked asphalt oval, home of World Series",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 200 },
                        new Corner { Name = "Turn 2", Type = CornerType.Medium, DistanceMeters = 400 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 600 },
                        new Corner { Name = "Turn 4", Type = CornerType.Medium, DistanceMeters = 800 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "oxford_plains_speedway",
                    DisplayName = "Oxford Plains Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 0.563f,
                    Description = "3/8-mile semi-banked asphalt oval in Maine",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Slow, DistanceMeters = 140 },
                        new Corner { Name = "Turn 2", Type = CornerType.Slow, DistanceMeters = 280 },
                        new Corner { Name = "Turn 3", Type = CornerType.Slow, DistanceMeters = 420 },
                        new Corner { Name = "Turn 4", Type = CornerType.Slow, DistanceMeters = 560 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "south_boston_speedway",
                    DisplayName = "South Boston Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 0.644f,
                    Description = "0.4-mile asphalt oval in Virginia",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Slow, DistanceMeters = 160 },
                        new Corner { Name = "Turn 2", Type = CornerType.Slow, DistanceMeters = 320 },
                        new Corner { Name = "Turn 3", Type = CornerType.Slow, DistanceMeters = 480 },
                        new Corner { Name = "Turn 4", Type = CornerType.Slow, DistanceMeters = 640 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "southern_national_motorsports_park",
                    DisplayName = "Southern National Motorsports Park",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 0.644f,
                    Description = "0.4-mile asphalt oval in North Carolina",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Slow, DistanceMeters = 160 },
                        new Corner { Name = "Turn 2", Type = CornerType.Slow, DistanceMeters = 320 },
                        new Corner { Name = "Turn 3", Type = CornerType.Slow, DistanceMeters = 480 },
                        new Corner { Name = "Turn 4", Type = CornerType.Slow, DistanceMeters = 640 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "stafford_motor_speedway",
                    DisplayName = "Stafford Motor Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 0.805f,
                    Description = "0.5-mile high-banked asphalt oval in Connecticut",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 200 },
                        new Corner { Name = "Turn 2", Type = CornerType.Medium, DistanceMeters = 400 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 600 },
                        new Corner { Name = "Turn 4", Type = CornerType.Medium, DistanceMeters = 800 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "the_bullring",
                    DisplayName = "The Bullring at Las Vegas Motor Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 0.563f,
                    Description = "3/8-mile high-banked paved oval",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Slow, DistanceMeters = 140 },
                        new Corner { Name = "Turn 2", Type = CornerType.Slow, DistanceMeters = 280 },
                        new Corner { Name = "Turn 3", Type = CornerType.Slow, DistanceMeters = 420 },
                        new Corner { Name = "Turn 4", Type = CornerType.Slow, DistanceMeters = 560 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "indianapolis_road_course",
                    DisplayName = "Indianapolis Motor Speedway Road Course",
                    Configuration = "Road Course",
                    Category = TrackCategory.RoadCourse,
                    Country = "USA",
                    LengthKm = 4.025f,
                    Description = "Road course configuration using oval and infield",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 400 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 800 },
                        new Corner { Name = "Turn 5", Type = CornerType.Slow, DistanceMeters = 1600 },
                        new Corner { Name = "Turn 7", Type = CornerType.Medium, DistanceMeters = 2200 },
                        new Corner { Name = "Turn 9", Type = CornerType.Fast, DistanceMeters = 2800 },
                        new Corner { Name = "Turn 10", Type = CornerType.Slow, DistanceMeters = 3200 },
                        new Corner { Name = "Turn 12", Type = CornerType.Fast, DistanceMeters = 3600 },
                        new Corner { Name = "Turn 14", Type = CornerType.Medium, DistanceMeters = 4000 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "road_atlanta",
                    DisplayName = "Road Atlanta",
                    Configuration = "Full Course",
                    Category = TrackCategory.RoadCourse,
                    Country = "USA",
                    LengthKm = 4.088f,
                    Description = "Fast and challenging road course in Georgia",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 300 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 800 },
                        new Corner { Name = "Turn 5", Type = CornerType.Fast, DistanceMeters = 1400 },
                        new Corner { Name = "Turn 6", Type = CornerType.Slow, DistanceMeters = 1800 },
                        new Corner { Name = "Turn 7", Type = CornerType.Medium, DistanceMeters = 2200 },
                        new Corner { Name = "Turn 10a", Type = CornerType.Chicane, DistanceMeters = 3200 },
                        new Corner { Name = "Turn 12", Type = CornerType.Fast, DistanceMeters = 3800 }
                    }
                },

                // Additional Road Course Configurations

                new TrackLayout
                {
                    InternalName = "charlotte_roval",
                    DisplayName = "Charlotte Motor Speedway Road Course",
                    Configuration = "ROVAL",
                    Category = TrackCategory.RoadCourse,
                    Country = "USA",
                    LengthKm = 3.61f,
                    Description = "Unique oval/road course hybrid (ROVAL)",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 400 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 800 },
                        new Corner { Name = "Chicane", Type = CornerType.Chicane, DistanceMeters = 1800 },
                        new Corner { Name = "Turn 6", Type = CornerType.Medium, DistanceMeters = 2200 },
                        new Corner { Name = "Turn 9", Type = CornerType.Slow, DistanceMeters = 2800 },
                        new Corner { Name = "Turn 11", Type = CornerType.Fast, DistanceMeters = 3200 },
                        new Corner { Name = "Turn 17", Type = CornerType.Slow, DistanceMeters = 3500 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "daytona_road_course",
                    DisplayName = "Daytona International Speedway Road Course",
                    Configuration = "Road Course",
                    Category = TrackCategory.RoadCourse,
                    Country = "USA",
                    LengthKm = 5.73f,
                    Description = "Road course using oval and infield sections",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 500 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 1000 },
                        new Corner { Name = "International Horseshoe", Type = CornerType.Slow, DistanceMeters = 2500 },
                        new Corner { Name = "Turn 6", Type = CornerType.Medium, DistanceMeters = 3500 },
                        new Corner { Name = "The Kink", Type = CornerType.Fast, DistanceMeters = 4200 },
                        new Corner { Name = "West Horseshoe", Type = CornerType.Medium, DistanceMeters = 5000 },
                        new Corner { Name = "East Chicane", Type = CornerType.Chicane, DistanceMeters = 5500 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "phoenix_road_course",
                    DisplayName = "Phoenix Raceway Road Course",
                    Configuration = "Road Course",
                    Category = TrackCategory.RoadCourse,
                    Country = "USA",
                    LengthKm = 2.28f,
                    Description = "Infield road course configuration",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 300 },
                        new Corner { Name = "Turn 3", Type = CornerType.Slow, DistanceMeters = 700 },
                        new Corner { Name = "Turn 5", Type = CornerType.Fast, DistanceMeters = 1200 },
                        new Corner { Name = "Turn 7", Type = CornerType.Medium, DistanceMeters = 1600 },
                        new Corner { Name = "Turn 9", Type = CornerType.Slow, DistanceMeters = 2000 }
                    }
                },

                // Classic American Road Courses

                new TrackLayout
                {
                    InternalName = "summit_point_raceway",
                    DisplayName = "Summit Point Raceway",
                    Configuration = "Main Circuit",
                    Category = TrackCategory.RoadCourse,
                    Country = "USA",
                    LengthKm = 3.219f,
                    Description = "Historic road course in West Virginia",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 400 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 1000 },
                        new Corner { Name = "Turn 5", Type = CornerType.Slow, DistanceMeters = 1600 },
                        new Corner { Name = "Turn 7", Type = CornerType.Fast, DistanceMeters = 2200 },
                        new Corner { Name = "Turn 9", Type = CornerType.Medium, DistanceMeters = 2800 },
                        new Corner { Name = "Turn 10", Type = CornerType.Fast, DistanceMeters = 3100 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "thompson_speedway",
                    DisplayName = "Thompson Speedway Motorsports Park",
                    Configuration = "Road Course",
                    Category = TrackCategory.RoadCourse,
                    Country = "USA",
                    LengthKm = 2.414f,
                    Description = "Multi-configuration facility in Connecticut",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 300 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 800 },
                        new Corner { Name = "Turn 5", Type = CornerType.Slow, DistanceMeters = 1400 },
                        new Corner { Name = "Turn 7", Type = CornerType.Medium, DistanceMeters = 1900 },
                        new Corner { Name = "Turn 9", Type = CornerType.Fast, DistanceMeters = 2300 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "canadian_tire_motorsport_park",
                    DisplayName = "Canadian Tire Motorsport Park",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.RoadCourse,
                    Country = "Canada",
                    LengthKm = 3.957f,
                    Description = "Fast road course north of Toronto",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 350 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 700 },
                        new Corner { Name = "Turn 5", Type = CornerType.Slow, DistanceMeters = 1500 },
                        new Corner { Name = "Turn 8", Type = CornerType.Fast, DistanceMeters = 2400 },
                        new Corner { Name = "Turn 10", Type = CornerType.Medium, DistanceMeters = 3200 },
                        new Corner { Name = "Turn 12", Type = CornerType.Fast, DistanceMeters = 3700 }
                    }
                },

                #region Additional International Road Courses

                new TrackLayout
                {
                    InternalName = "imola",
                    DisplayName = "Autodromo Enzo e Dino Ferrari",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.RoadCourse,
                    Country = "Italy",
                    LengthKm = 4.909f,
                    Description = "Historic F1 circuit, site of Senna's fatal accident",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Variante del Tamburello", Type = CornerType.Chicane, DistanceMeters = 600 },
                        new Corner { Name = "Villeneuve", Type = CornerType.Fast, DistanceMeters = 1200 },
                        new Corner { Name = "Tosa", Type = CornerType.Slow, DistanceMeters = 1800 },
                        new Corner { Name = "Piratella", Type = CornerType.Fast, DistanceMeters = 2400 },
                        new Corner { Name = "Acque Minerali", Type = CornerType.Medium, DistanceMeters = 3000 },
                        new Corner { Name = "Variante Alta", Type = CornerType.Chicane, DistanceMeters = 3600 },
                        new Corner { Name = "Rivazza", Type = CornerType.Medium, DistanceMeters = 4400 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "barcelona",
                    DisplayName = "Circuit de Barcelona-Catalunya",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.RoadCourse,
                    Country = "Spain",
                    LengthKm = 4.655f,
                    Description = "Spanish Grand Prix venue and F1 testing circuit",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 400 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 700 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 1100 },
                        new Corner { Name = "Turn 5", Type = CornerType.Slow, DistanceMeters = 1800 },
                        new Corner { Name = "Turn 7", Type = CornerType.Fast, DistanceMeters = 2400 },
                        new Corner { Name = "Turn 9", Type = CornerType.Medium, DistanceMeters = 3200 },
                        new Corner { Name = "Turn 10", Type = CornerType.Chicane, DistanceMeters = 3800 },
                        new Corner { Name = "Turn 12", Type = CornerType.Fast, DistanceMeters = 4200 },
                        new Corner { Name = "Turn 14", Type = CornerType.Medium, DistanceMeters = 4500 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "zandvoort",
                    DisplayName = "Circuit Zandvoort",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.RoadCourse,
                    Country = "Netherlands",
                    LengthKm = 4.259f,
                    Description = "Returning to F1 calendar with banked corners",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Tarzan", Type = CornerType.Hairpin, DistanceMeters = 400 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 1000 },
                        new Corner { Name = "Hugenholtz", Type = CornerType.Fast, DistanceMeters = 1600 },
                        new Corner { Name = "Turn 7", Type = CornerType.Medium, DistanceMeters = 2200 },
                        new Corner { Name = "Scheivlak", Type = CornerType.Fast, DistanceMeters = 2800 },
                        new Corner { Name = "Turn 11", Type = CornerType.Fast, DistanceMeters = 3400 },
                        new Corner { Name = "Arie Luyendyk", Type = CornerType.Medium, DistanceMeters = 4000 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "hockenheim",
                    DisplayName = "Hockenheimring",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.RoadCourse,
                    Country = "Germany",
                    LengthKm = 4.574f,
                    Description = "German Grand Prix venue with stadium section",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Nordkurve", Type = CornerType.Hairpin, DistanceMeters = 400 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 800 },
                        new Corner { Name = "Turn 6", Type = CornerType.Medium, DistanceMeters = 2000 },
                        new Corner { Name = "Turn 8", Type = CornerType.Slow, DistanceMeters = 2600 },
                        new Corner { Name = "Turn 12", Type = CornerType.Medium, DistanceMeters = 3600 },
                        new Corner { Name = "Turn 17", Type = CornerType.Hairpin, DistanceMeters = 4400 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "paul_ricard",
                    DisplayName = "Circuit Paul Ricard",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.RoadCourse,
                    Country = "France",
                    LengthKm = 5.842f,
                    Description = "French Grand Prix venue with distinctive blue and red runoff",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 500 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 1200 },
                        new Corner { Name = "Turn 6", Type = CornerType.Medium, DistanceMeters = 2200 },
                        new Corner { Name = "Turn 8", Type = CornerType.Chicane, DistanceMeters = 3000 },
                        new Corner { Name = "Turn 10", Type = CornerType.Fast, DistanceMeters = 4000 },
                        new Corner { Name = "Turn 11", Type = CornerType.Slow, DistanceMeters = 4800 },
                        new Corner { Name = "Turn 15", Type = CornerType.Medium, DistanceMeters = 5600 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "donington_park",
                    DisplayName = "Donington Park",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.RoadCourse,
                    Country = "United Kingdom",
                    LengthKm = 4.020f,
                    Description = "Historic English circuit with elevation changes",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Redgate", Type = CornerType.Hairpin, DistanceMeters = 300 },
                        new Corner { Name = "Hollywood", Type = CornerType.Fast, DistanceMeters = 800 },
                        new Corner { Name = "Craner Curves", Type = CornerType.Fast, DistanceMeters = 1400 },
                        new Corner { Name = "Old Hairpin", Type = CornerType.Hairpin, DistanceMeters = 2000 },
                        new Corner { Name = "McLeans", Type = CornerType.Fast, DistanceMeters = 2600 },
                        new Corner { Name = "Coppice", Type = CornerType.Medium, DistanceMeters = 3200 },
                        new Corner { Name = "Starkeys Bridge", Type = CornerType.Fast, DistanceMeters = 3800 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "oulton_park",
                    DisplayName = "Oulton Park",
                    Configuration = "International",
                    Category = TrackCategory.RoadCourse,
                    Country = "United Kingdom",
                    LengthKm = 4.307f,
                    Description = "Challenging parkland circuit with elevation changes",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Old Hall", Type = CornerType.Medium, DistanceMeters = 400 },
                        new Corner { Name = "Cascades", Type = CornerType.Fast, DistanceMeters = 1000 },
                        new Corner { Name = "Island Bend", Type = CornerType.Medium, DistanceMeters = 1600 },
                        new Corner { Name = "Shell Oils", Type = CornerType.Fast, DistanceMeters = 2200 },
                        new Corner { Name = "Brittens", Type = CornerType.Slow, DistanceMeters = 2800 },
                        new Corner { Name = "Druids", Type = CornerType.Hairpin, DistanceMeters = 3400 },
                        new Corner { Name = "Lodge", Type = CornerType.Fast, DistanceMeters = 4000 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "snetterton",
                    DisplayName = "Snetterton Circuit",
                    Configuration = "300",
                    Category = TrackCategory.RoadCourse,
                    Country = "United Kingdom",
                    LengthKm = 4.778f,
                    Description = "Former airfield circuit in Norfolk",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Riches", Type = CornerType.Fast, DistanceMeters = 600 },
                        new Corner { Name = "Norwich", Type = CornerType.Medium, DistanceMeters = 1200 },
                        new Corner { Name = "Coram", Type = CornerType.Fast, DistanceMeters = 1800 },
                        new Corner { Name = "Murrays", Type = CornerType.Slow, DistanceMeters = 2400 },
                        new Corner { Name = "Palmer", Type = CornerType.Fast, DistanceMeters = 3200 },
                        new Corner { Name = "Bentley", Type = CornerType.Medium, DistanceMeters = 4000 },
                        new Corner { Name = "Bombhole", Type = CornerType.Fast, DistanceMeters = 4600 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "mount_panorama",
                    DisplayName = "Mount Panorama Circuit",
                    Configuration = "Bathurst",
                    Category = TrackCategory.RoadCourse,
                    Country = "Australia",
                    LengthKm = 6.213f,
                    Description = "Iconic Australian circuit, home of Bathurst 1000",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Hell Corner", Type = CornerType.Medium, DistanceMeters = 200 },
                        new Corner { Name = "Mountain Straight", Type = CornerType.Fast, DistanceMeters = 1500 },
                        new Corner { Name = "Griffin's Bend", Type = CornerType.Fast, DistanceMeters = 2000 },
                        new Corner { Name = "The Cutting", Type = CornerType.Medium, DistanceMeters = 2500 },
                        new Corner { Name = "Reid Park", Type = CornerType.Fast, DistanceMeters = 3200 },
                        new Corner { Name = "The Chase", Type = CornerType.Chicane, DistanceMeters = 5500 },
                        new Corner { Name = "Murray's Corner", Type = CornerType.Fast, DistanceMeters = 6000 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "phillip_island",
                    DisplayName = "Phillip Island Circuit",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.RoadCourse,
                    Country = "Australia",
                    LengthKm = 4.445f,
                    Description = "Scenic coastal circuit, home of Australian MotoGP",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Doohan Corner", Type = CornerType.Medium, DistanceMeters = 400 },
                        new Corner { Name = "Southern Loop", Type = CornerType.Fast, DistanceMeters = 1000 },
                        new Corner { Name = "MG Corner", Type = CornerType.Hairpin, DistanceMeters = 1800 },
                        new Corner { Name = "Siberia", Type = CornerType.Fast, DistanceMeters = 2600 },
                        new Corner { Name = "Stoner Corner", Type = CornerType.Fast, DistanceMeters = 3400 },
                        new Corner { Name = "Lukey Heights", Type = CornerType.Fast, DistanceMeters = 4000 }
                    }
                },

                #endregion

                #region Additional USA Tracks

                new TrackLayout
                {
                    InternalName = "belle_isle",
                    DisplayName = "Detroit Grand Prix at Belle Isle",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.StreetCircuit,
                    Country = "USA",
                    LengthKm = 3.621f,
                    Description = "IndyCar street circuit on Detroit's Belle Isle",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 400 },
                        new Corner { Name = "Turn 3", Type = CornerType.Slow, DistanceMeters = 900 },
                        new Corner { Name = "Turn 5", Type = CornerType.Fast, DistanceMeters = 1500 },
                        new Corner { Name = "Turn 7", Type = CornerType.Chicane, DistanceMeters = 2100 },
                        new Corner { Name = "Turn 9", Type = CornerType.Slow, DistanceMeters = 2700 },
                        new Corner { Name = "Turn 13", Type = CornerType.Fast, DistanceMeters = 3400 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "circuit_gilles_villeneuve",
                    DisplayName = "Circuit Gilles Villeneuve",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.StreetCircuit,
                    Country = "Canada",
                    LengthKm = 4.361f,
                    Description = "Canadian Grand Prix venue on Île Notre-Dame",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 350 },
                        new Corner { Name = "Turn 2", Type = CornerType.Slow, DistanceMeters = 600 },
                        new Corner { Name = "Turn 6", Type = CornerType.Fast, DistanceMeters = 1800 },
                        new Corner { Name = "Turn 8", Type = CornerType.Hairpin, DistanceMeters = 2600 },
                        new Corner { Name = "Turn 10", Type = CornerType.Chicane, DistanceMeters = 3200 },
                        new Corner { Name = "Turn 13", Type = CornerType.Chicane, DistanceMeters = 4000 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "st_petersburg",
                    DisplayName = "Streets of St. Petersburg",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.StreetCircuit,
                    Country = "USA",
                    LengthKm = 2.895f,
                    Description = "IndyCar season opener street circuit",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 300 },
                        new Corner { Name = "Turn 3", Type = CornerType.Slow, DistanceMeters = 700 },
                        new Corner { Name = "Turn 4", Type = CornerType.Hairpin, DistanceMeters = 1000 },
                        new Corner { Name = "Turn 9", Type = CornerType.Fast, DistanceMeters = 1800 },
                        new Corner { Name = "Turn 10", Type = CornerType.Slow, DistanceMeters = 2200 },
                        new Corner { Name = "Turn 14", Type = CornerType.Fast, DistanceMeters = 2700 }
                    }
                }

                #endregion

                #region IndyCar Ovals

                new TrackLayout
                {
                    InternalName = "iowa_speedway",
                    DisplayName = "Iowa Speedway",
                    Configuration = "Oval",
                    Category = TrackCategory.Oval,
                    Country = "USA",
                    LengthKm = 1.448f,
                    Description = "0.894-mile concrete oval",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 360 },
                        new Corner { Name = "Turn 2", Type = CornerType.Medium, DistanceMeters = 720 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 1080 },
                        new Corner { Name = "Turn 4", Type = CornerType.Medium, DistanceMeters = 1440 }
                    }
                },

                #endregion

                #region USA Road Courses

                new TrackLayout
                {
                    InternalName = "laguna_seca",
                    DisplayName = "WeatherTech Raceway Laguna Seca",
                    Configuration = "Full Course",
                    Category = TrackCategory.RoadCourse,
                    Country = "USA",
                    LengthKm = 3.602f,
                    Description = "Famous for the Corkscrew turn",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 150 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 520 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 980 },
                        new Corner { Name = "Turn 4", Type = CornerType.Fast, DistanceMeters = 1350 },
                        new Corner { Name = "Turn 5", Type = CornerType.Medium, DistanceMeters = 1680 },
                        new Corner { Name = "Turn 6", Type = CornerType.Slow, DistanceMeters = 1950 },
                        new Corner { Name = "Corkscrew", Type = CornerType.Slow, DistanceMeters = 2150 },
                        new Corner { Name = "Turn 9", Type = CornerType.Medium, DistanceMeters = 2580 },
                        new Corner { Name = "Turn 10", Type = CornerType.Fast, DistanceMeters = 2950 },
                        new Corner { Name = "Turn 11", Type = CornerType.Slow, DistanceMeters = 3450 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "road_america",
                    DisplayName = "Road America",
                    Configuration = "Full Course",
                    Category = TrackCategory.RoadCourse,
                    Country = "USA",
                    LengthKm = 6.515f,
                    Description = "High-speed road course in Wisconsin",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 400 },
                        new Corner { Name = "Turn 3", Type = CornerType.Fast, DistanceMeters = 1200 },
                        new Corner { Name = "Turn 5", Type = CornerType.Slow, DistanceMeters = 2100 },
                        new Corner { Name = "Turn 6", Type = CornerType.Fast, DistanceMeters = 2800 },
                        new Corner { Name = "Turn 8", Type = CornerType.Fast, DistanceMeters = 3600 },
                        new Corner { Name = "Turn 11", Type = CornerType.Slow, DistanceMeters = 4800 },
                        new Corner { Name = "Turn 12", Type = CornerType.Fast, DistanceMeters = 5200 },
                        new Corner { Name = "Turn 14", Type = CornerType.Slow, DistanceMeters = 6200 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "sebring",
                    DisplayName = "Sebring International Raceway",
                    Configuration = "Full Course",
                    Category = TrackCategory.RoadCourse,
                    Country = "USA",
                    LengthKm = 6.019f,
                    Description = "Historic endurance racing venue",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Hairpin, DistanceMeters = 200 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 800 },
                        new Corner { Name = "Turn 5", Type = CornerType.Fast, DistanceMeters = 1500 },
                        new Corner { Name = "Turn 7", Type = CornerType.Medium, DistanceMeters = 2200 },
                        new Corner { Name = "Turn 10", Type = CornerType.Slow, DistanceMeters = 3500 },
                        new Corner { Name = "Turn 13", Type = CornerType.Fast, DistanceMeters = 4800 },
                        new Corner { Name = "Turn 17", Type = CornerType.Slow, DistanceMeters = 5700 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "mid_ohio",
                    DisplayName = "Mid-Ohio Sports Car Course",
                    Configuration = "Full Course",
                    Category = TrackCategory.RoadCourse,
                    Country = "USA",
                    LengthKm = 3.634f,
                    Description = "Rolling hills road course",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 200 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 500 },
                        new Corner { Name = "Turn 4", Type = CornerType.Slow, DistanceMeters = 1100 },
                        new Corner { Name = "Turn 5", Type = CornerType.Medium, DistanceMeters = 1400 },
                        new Corner { Name = "Turn 6", Type = CornerType.Fast, DistanceMeters = 1800 },
                        new Corner { Name = "Turn 9", Type = CornerType.Medium, DistanceMeters = 2600 },
                        new Corner { Name = "Turn 11", Type = CornerType.Slow, DistanceMeters = 3200 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "lime_rock",
                    DisplayName = "Lime Rock Park",
                    Configuration = "Full Course",
                    Category = TrackCategory.RoadCourse,
                    Country = "USA",
                    LengthKm = 2.414f,
                    Description = "Compact road course in Connecticut",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Big Bend", Type = CornerType.Fast, DistanceMeters = 400 },
                        new Corner { Name = "Left Hander", Type = CornerType.Medium, DistanceMeters = 800 },
                        new Corner { Name = "Uphill", Type = CornerType.Fast, DistanceMeters = 1200 },
                        new Corner { Name = "West Bend", Type = CornerType.Fast, DistanceMeters = 1600 },
                        new Corner { Name = "Diving Turn", Type = CornerType.Medium, DistanceMeters = 2000 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "virginia_international_raceway",
                    DisplayName = "Virginia International Raceway",
                    Configuration = "Full Course",
                    Category = TrackCategory.RoadCourse,
                    Country = "USA",
                    LengthKm = 5.263f,
                    Description = "Classic American road course",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Fast, DistanceMeters = 500 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 1200 },
                        new Corner { Name = "Spiral", Type = CornerType.Fast, DistanceMeters = 2000 },
                        new Corner { Name = "Oak Tree", Type = CornerType.Fast, DistanceMeters = 2800 },
                        new Corner { Name = "Roller Coaster", Type = CornerType.Fast, DistanceMeters = 3600 },
                        new Corner { Name = "NASCAR Bend", Type = CornerType.Fast, DistanceMeters = 4400 },
                        new Corner { Name = "Hog Pen", Type = CornerType.Slow, DistanceMeters = 5000 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "barber_motorsports_park",
                    DisplayName = "Barber Motorsports Park",
                    Configuration = "Full Course",
                    Category = TrackCategory.RoadCourse,
                    Country = "USA",
                    LengthKm = 3.769f,
                    Description = "Picturesque road course in Alabama",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 300 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 600 },
                        new Corner { Name = "Turn 5", Type = CornerType.Slow, DistanceMeters = 1400 },
                        new Corner { Name = "Turn 8", Type = CornerType.Medium, DistanceMeters = 2200 },
                        new Corner { Name = "Turn 12", Type = CornerType.Fast, DistanceMeters = 3000 },
                        new Corner { Name = "Turn 15", Type = CornerType.Slow, DistanceMeters = 3500 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "sonoma_raceway",
                    DisplayName = "Sonoma Raceway",
                    Configuration = "Cup",
                    Category = TrackCategory.RoadCourse,
                    Country = "USA",
                    LengthKm = 3.218f,
                    Description = "Hilly road course in California wine country",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 250 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 600 },
                        new Corner { Name = "Turn 4", Type = CornerType.Slow, DistanceMeters = 1200 },
                        new Corner { Name = "Turn 6", Type = CornerType.Fast, DistanceMeters = 1800 },
                        new Corner { Name = "Turn 7", Type = CornerType.Hairpin, DistanceMeters = 2200 },
                        new Corner { Name = "Turn 9", Type = CornerType.Fast, DistanceMeters = 2800 },
                        new Corner { Name = "Turn 11", Type = CornerType.Medium, DistanceMeters = 3100 }
                    }
                },

                #endregion

                #region Street Circuits

                new TrackLayout
                {
                    InternalName = "long_beach",
                    DisplayName = "Streets of Long Beach",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.StreetCircuit,
                    Country = "USA",
                    LengthKm = 3.167f,
                    Description = "Famous IndyCar street circuit",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Slow, DistanceMeters = 300 },
                        new Corner { Name = "Turn 2", Type = CornerType.Medium, DistanceMeters = 600 },
                        new Corner { Name = "Turn 6", Type = CornerType.Slow, DistanceMeters = 1400 },
                        new Corner { Name = "Turn 8", Type = CornerType.Fast, DistanceMeters = 1900 },
                        new Corner { Name = "Turn 9", Type = CornerType.Hairpin, DistanceMeters = 2200 },
                        new Corner { Name = "Turn 11", Type = CornerType.Slow, DistanceMeters = 2800 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "detroit_belle_isle",
                    DisplayName = "Detroit Belle Isle",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.StreetCircuit,
                    Country = "USA",
                    LengthKm = 3.621f,
                    Description = "Island park street circuit",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 400 },
                        new Corner { Name = "Turn 3", Type = CornerType.Slow, DistanceMeters = 900 },
                        new Corner { Name = "Turn 5", Type = CornerType.Fast, DistanceMeters = 1500 },
                        new Corner { Name = "Turn 7", Type = CornerType.Chicane, DistanceMeters = 2100 },
                        new Corner { Name = "Turn 9", Type = CornerType.Slow, DistanceMeters = 2700 },
                        new Corner { Name = "Turn 13", Type = CornerType.Fast, DistanceMeters = 3400 }
                    }
                },

                #endregion

                #region International Road Courses

                new TrackLayout
                {
                    InternalName = "spa",
                    DisplayName = "Circuit de Spa-Francorchamps",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.RoadCourse,
                    Country = "Belgium",
                    LengthKm = 7.004f,
                    Description = "Legendary F1 circuit with Eau Rouge",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "La Source", Type = CornerType.Hairpin, DistanceMeters = 180 },
                        new Corner { Name = "Eau Rouge", Type = CornerType.Fast, DistanceMeters = 950 },
                        new Corner { Name = "Raidillon", Type = CornerType.Fast, DistanceMeters = 1100 },
                        new Corner { Name = "Les Combes", Type = CornerType.Medium, DistanceMeters = 2800 },
                        new Corner { Name = "Malmedy", Type = CornerType.Fast, DistanceMeters = 3200 },
                        new Corner { Name = "Rivage", Type = CornerType.Medium, DistanceMeters = 3850 },
                        new Corner { Name = "Pouhon", Type = CornerType.Fast, DistanceMeters = 4200 },
                        new Corner { Name = "Campus", Type = CornerType.Chicane, DistanceMeters = 5800 },
                        new Corner { Name = "Stavelot", Type = CornerType.Fast, DistanceMeters = 6200 },
                        new Corner { Name = "Blanchimont", Type = CornerType.Fast, DistanceMeters = 6800 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "silverstone",
                    DisplayName = "Silverstone Circuit",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.RoadCourse,
                    Country = "United Kingdom",
                    LengthKm = 5.891f,
                    Description = "Home of British Grand Prix",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Abbey", Type = CornerType.Fast, DistanceMeters = 300 },
                        new Corner { Name = "Farm Curve", Type = CornerType.Fast, DistanceMeters = 800 },
                        new Corner { Name = "Village", Type = CornerType.Medium, DistanceMeters = 1200 },
                        new Corner { Name = "The Loop", Type = CornerType.Slow, DistanceMeters = 1600 },
                        new Corner { Name = "Aintree", Type = CornerType.Medium, DistanceMeters = 2000 },
                        new Corner { Name = "Wellington Straight", Type = CornerType.Fast, DistanceMeters = 2800 },
                        new Corner { Name = "Brooklands", Type = CornerType.Medium, DistanceMeters = 3400 },
                        new Corner { Name = "Luffield", Type = CornerType.Slow, DistanceMeters = 4200 },
                        new Corner { Name = "Woodcote", Type = CornerType.Fast, DistanceMeters = 4800 },
                        new Corner { Name = "Copse", Type = CornerType.Fast, DistanceMeters = 5400 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "monza",
                    DisplayName = "Autodromo Nazionale Monza",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.RoadCourse,
                    Country = "Italy",
                    LengthKm = 5.793f,
                    Description = "The Temple of Speed",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Variante del Rettifilo", Type = CornerType.Chicane, DistanceMeters = 600 },
                        new Corner { Name = "Curva Biassono", Type = CornerType.Fast, DistanceMeters = 1500 },
                        new Corner { Name = "Variante della Roggia", Type = CornerType.Chicane, DistanceMeters = 2200 },
                        new Corner { Name = "Lesmo 1", Type = CornerType.Medium, DistanceMeters = 2800 },
                        new Corner { Name = "Lesmo 2", Type = CornerType.Medium, DistanceMeters = 3000 },
                        new Corner { Name = "Variante Ascari", Type = CornerType.Chicane, DistanceMeters = 4200 },
                        new Corner { Name = "Curva Parabolica", Type = CornerType.Fast, DistanceMeters = 5200 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "nurburgring_gp",
                    DisplayName = "Nürburgring Grand-Prix-Strecke",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.RoadCourse,
                    Country = "Germany",
                    LengthKm = 5.148f,
                    Description = "Modern F1 circuit adjacent to the Nordschleife",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 280 },
                        new Corner { Name = "Yokohama-S", Type = CornerType.Chicane, DistanceMeters = 650 },
                        new Corner { Name = "Turn 6", Type = CornerType.Fast, DistanceMeters = 1200 },
                        new Corner { Name = "Schumacher-S", Type = CornerType.Chicane, DistanceMeters = 2100 },
                        new Corner { Name = "Turn 10", Type = CornerType.Medium, DistanceMeters = 2850 },
                        new Corner { Name = "NGK Chicane", Type = CornerType.Chicane, DistanceMeters = 4200 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "brands_hatch",
                    DisplayName = "Brands Hatch Circuit",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.RoadCourse,
                    Country = "United Kingdom",
                    LengthKm = 3.916f,
                    Description = "Natural amphitheater circuit",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Paddock Hill Bend", Type = CornerType.Fast, DistanceMeters = 200 },
                        new Corner { Name = "Druids", Type = CornerType.Hairpin, DistanceMeters = 600 },
                        new Corner { Name = "Graham Hill Bend", Type = CornerType.Medium, DistanceMeters = 1200 },
                        new Corner { Name = "Surtees", Type = CornerType.Fast, DistanceMeters = 1800 },
                        new Corner { Name = "Hawthorn Hill", Type = CornerType.Fast, DistanceMeters = 2400 },
                        new Corner { Name = "Westfield Bend", Type = CornerType.Fast, DistanceMeters = 3000 },
                        new Corner { Name = "Sheene Curve", Type = CornerType.Fast, DistanceMeters = 3600 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "suzuka",
                    DisplayName = "Suzuka Circuit",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.RoadCourse,
                    Country = "Japan",
                    LengthKm = 5.807f,
                    Description = "Figure-8 circuit with crossover bridge",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 400 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 800 },
                        new Corner { Name = "S-Curves", Type = CornerType.Chicane, DistanceMeters = 1200 },
                        new Corner { Name = "Degner Curve", Type = CornerType.Medium, DistanceMeters = 1800 },
                        new Corner { Name = "Hairpin", Type = CornerType.Hairpin, DistanceMeters = 2400 },
                        new Corner { Name = "Spoon Curve", Type = CornerType.Medium, DistanceMeters = 3600 },
                        new Corner { Name = "130R", Type = CornerType.Fast, DistanceMeters = 4800 },
                        new Corner { Name = "Casio Triangle", Type = CornerType.Chicane, DistanceMeters = 5400 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "interlagos",
                    DisplayName = "Autódromo José Carlos Pace",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.RoadCourse,
                    Country = "Brazil",
                    LengthKm = 4.309f,
                    Description = "Brazilian Grand Prix venue",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Senna S", Type = CornerType.Chicane, DistanceMeters = 280 },
                        new Corner { Name = "Curva do Sol", Type = CornerType.Fast, DistanceMeters = 800 },
                        new Corner { Name = "Descida do Lago", Type = CornerType.Fast, DistanceMeters = 1400 },
                        new Corner { Name = "Ferradura", Type = CornerType.Slow, DistanceMeters = 2000 },
                        new Corner { Name = "Laranja", Type = CornerType.Medium, DistanceMeters = 2600 },
                        new Corner { Name = "Pinheirinho", Type = CornerType.Medium, DistanceMeters = 3200 },
                        new Corner { Name = "Bico de Pato", Type = CornerType.Fast, DistanceMeters = 3800 },
                        new Corner { Name = "Juncão", Type = CornerType.Fast, DistanceMeters = 4200 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "hungaroring",
                    DisplayName = "Hungaroring",
                    Configuration = "Grand Prix",
                    Category = TrackCategory.RoadCourse,
                    Country = "Hungary",
                    LengthKm = 4.381f,
                    Description = "Twisty circuit near Budapest",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 300 },
                        new Corner { Name = "Turn 2", Type = CornerType.Fast, DistanceMeters = 600 },
                        new Corner { Name = "Turn 4", Type = CornerType.Slow, DistanceMeters = 1200 },
                        new Corner { Name = "Turn 6", Type = CornerType.Medium, DistanceMeters = 1800 },
                        new Corner { Name = "Turn 8", Type = CornerType.Slow, DistanceMeters = 2400 },
                        new Corner { Name = "Turn 11", Type = CornerType.Medium, DistanceMeters = 3200 },
                        new Corner { Name = "Turn 12", Type = CornerType.Fast, DistanceMeters = 3600 },
                        new Corner { Name = "Turn 14", Type = CornerType.Medium, DistanceMeters = 4200 }
                    }
                },

                #endregion

                #region Dirt Tracks

                new TrackLayout
                {
                    InternalName = "eldora_speedway",
                    DisplayName = "Eldora Speedway",
                    Configuration = "Half Mile",
                    Category = TrackCategory.DirtOval,
                    Country = "USA",
                    LengthKm = 0.805f,
                    Description = "World's Greatest Dirt Track",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 200 },
                        new Corner { Name = "Turn 2", Type = CornerType.Medium, DistanceMeters = 400 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 600 },
                        new Corner { Name = "Turn 4", Type = CornerType.Medium, DistanceMeters = 800 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "knoxville_raceway",
                    DisplayName = "Knoxville Raceway",
                    Configuration = "Half Mile",
                    Category = TrackCategory.DirtOval,
                    Country = "USA",
                    LengthKm = 0.805f,
                    Description = "Sprint Car Capital of the World",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 200 },
                        new Corner { Name = "Turn 2", Type = CornerType.Medium, DistanceMeters = 400 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 600 },
                        new Corner { Name = "Turn 4", Type = CornerType.Medium, DistanceMeters = 800 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "williams_grove_speedway",
                    DisplayName = "Williams Grove Speedway",
                    Configuration = "Half Mile",
                    Category = TrackCategory.DirtOval,
                    Country = "USA",
                    LengthKm = 0.805f,
                    Description = "Historic Pennsylvania dirt track",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 200 },
                        new Corner { Name = "Turn 2", Type = CornerType.Medium, DistanceMeters = 400 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 600 },
                        new Corner { Name = "Turn 4", Type = CornerType.Medium, DistanceMeters = 800 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "cedarlake_speedway",
                    DisplayName = "Cedar Lake Speedway",
                    Configuration = "3/8 Mile",
                    Category = TrackCategory.DirtOval,
                    Country = "USA",
                    LengthKm = 0.603f,
                    Description = "High-banked dirt oval",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 150 },
                        new Corner { Name = "Turn 2", Type = CornerType.Medium, DistanceMeters = 300 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 450 },
                        new Corner { Name = "Turn 4", Type = CornerType.Medium, DistanceMeters = 600 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "fairbury_speedway",
                    DisplayName = "Fairbury Speedway",
                    Configuration = "1/4 Mile",
                    Category = TrackCategory.DirtOval,
                    Country = "USA",
                    LengthKm = 0.402f,
                    Description = "Illinois dirt track",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Slow, DistanceMeters = 100 },
                        new Corner { Name = "Turn 2", Type = CornerType.Slow, DistanceMeters = 200 },
                        new Corner { Name = "Turn 3", Type = CornerType.Slow, DistanceMeters = 300 },
                        new Corner { Name = "Turn 4", Type = CornerType.Slow, DistanceMeters = 400 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "dirt_track_at_charlotte",
                    DisplayName = "The Dirt Track at Charlotte",
                    Configuration = "1/4 Mile",
                    Category = TrackCategory.DirtOval,
                    Country = "USA",
                    LengthKm = 0.402f,
                    Description = "Clay surface inside Charlotte Motor Speedway",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Slow, DistanceMeters = 100 },
                        new Corner { Name = "Turn 2", Type = CornerType.Slow, DistanceMeters = 200 },
                        new Corner { Name = "Turn 3", Type = CornerType.Slow, DistanceMeters = 300 },
                        new Corner { Name = "Turn 4", Type = CornerType.Slow, DistanceMeters = 400 }
                    }
                },

                new TrackLayout
                {
                    InternalName = "lernerville_speedway",
                    DisplayName = "Lernerville Speedway",
                    Configuration = "4/10 Mile",
                    Category = TrackCategory.DirtOval,
                    Country = "USA",
                    LengthKm = 0.644f,
                    Description = "Pennsylvania's Action Track",
                    Corners = new List<Corner>
                    {
                        new Corner { Name = "Turn 1", Type = CornerType.Medium, DistanceMeters = 160 },
                        new Corner { Name = "Turn 2", Type = CornerType.Medium, DistanceMeters = 320 },
                        new Corner { Name = "Turn 3", Type = CornerType.Medium, DistanceMeters = 480 },
                        new Corner { Name = "Turn 4", Type = CornerType.Medium, DistanceMeters = 640 }
                    }
                }

                #endregion
            });
        }
    }

    #endregion

    // [Include all the remaining supporting classes from the previous implementation]
    // TelemetryPacket, BrakeZone, ReferenceLap, Config, TonePlayer

    public class TelemetryPacket
    {
        public double SessionTime { get; set; }
        public int Lap { get; set; }
        public float LapDist { get; set; }
        public float LapDistPct { get; set; }
        public float Speed { get; set; }
        public float Throttle { get; set; }
        public float Brake { get; set; }
    }

    public class BrakeZone
    {
        public float LapDist { get; set; }
        public float EntrySpeed { get; set; }
    }

    public class ReferenceLap
    {
        public string Track { get; set; } = "";
        public float TrackLength { get; set; } = 0f;
        public List<BrakeZone> Zones { get; set; } = new();
    }

    public class Config
    {
        public float BrakeOnThreshold { get; set; } = 0.10f;
        public float BrakeOffThreshold { get; set; } = 0.05f;
        public float MinZoneGapMeters { get; set; } = 20f;
        public float CornerResetMeters { get; set; } = 80f;
        public float MinSpeedDropMs { get; set; } = 0f;
        public float LeadMeters { get; set; } = 75f;
        public int ToneGapMs { get; set; } = 120;
        public bool OnlyAlertIfNotBraking { get; set; } = true;
        public float SpeedThresholdMs { get; set; } = 5f;
        public float ToneVolume { get; set; } = 0.85f;
        public bool CountdownBeep { get; set; } = true;
        public float CountdownRatio2 { get; set; } = 0.66f;
        public float CountdownRatio3 { get; set; } = 0.33f;
    }

    public class TonePlayer
    {
        public async System.Threading.Tasks.Task PlaySingleAsync(float frequencyHz, int durationMs = 120, float volume = 0.85f)
        {
            try
            {
                var tone = new SignalGenerator(44100, 1)
                {
                    Gain = volume,
                    Type = SignalGeneratorType.Sin,
                    Frequency = frequencyHz
                };
                var take = tone.Take(TimeSpan.FromMilliseconds(durationMs));
                using var waveOut = new WaveOutEvent();
                waveOut.Init(take);
                waveOut.Play();
                while (waveOut.PlaybackState == PlaybackState.Playing)
                    await System.Threading.Tasks.Task.Delay(5);
            }
            catch
            {
                // Ignore audio errors
            }
        }

        public async System.Threading.Tasks.Task PlayTripleAsync(Config config)
        {
            try
            {
                var frequencies = new[] { 600f, 900f, 1200f };
                foreach (var freq in frequencies)
                {
                    await PlaySingleAsync(freq, 120, config.ToneVolume);
                    await System.Threading.Tasks.Task.Delay(config.ToneGapMs);
                }
            }
            catch
            {
                // Ignore audio errors
            }
        }
    }
}
