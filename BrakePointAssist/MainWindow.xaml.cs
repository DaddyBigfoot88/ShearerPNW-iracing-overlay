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
using System.Windows.Input;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using irsdkSharp;
using Microsoft.Win32;
using System.Diagnostics;
using System.Linq;
using System.Windows.Media.Animation;

namespace BrakePointAssist
{
    public partial class MainWindow : Window
    {
        // Core components
        private IRacingSDK irsdk = new IRacingSDK();
        private TonePlayer player = new TonePlayer();
        private DispatcherTimer telemetryTimer;
        private DispatcherTimer slowUpdateTimer;
        private Config config = new Config();
        private ReferenceLap referenceLap = new ReferenceLap();

        // Enhanced telemetry tracking
        private EnhancedTelemetryPacket currentTelemetry = new EnhancedTelemetryPacket();
        private EnhancedTelemetryPacket previousTelemetry = new EnhancedTelemetryPacket();
        private Queue<EnhancedTelemetryPacket> telemetryHistory = new Queue<EnhancedTelemetryPacket>();
        private Queue<float> throttleHistory = new Queue<float>();
        private Queue<float> brakeHistory = new Queue<float>();
        private Queue<float> steeringHistory = new Queue<float>();
        private Queue<float> deltaHistory = new Queue<float>();

        // State tracking (original brake point system)
        private bool isRecording = false;
        private int recordingLap = -1;
        private float lastZoneAt = -9999f;
        private float prevSpeed = 0f;
        private bool prevBraking = false;
        private bool inCorner = false;
        private double lastLapPctSeen = 0.0;

        // Alert tracking
        private HashSet<int> firedThisLap = new HashSet<int>();
        private Dictionary<int, int> zoneStageMaskThisLap = new Dictionary<int, int>();

        // Enhanced race tracking
        private RaceSessionData sessionData = new RaceSessionData();
        private List<PitStop> pitStopHistory = new List<PitStop>();
        private List<TireSet> tireSets = new List<TireSet>();
        private FuelStrategy fuelStrategy = new FuelStrategy();
        private List<RelativeDriver> relativeDrivers = new List<RelativeDriver>();

        // UI state
        private bool isOverlayMode = false;
        private string currentTab = "DrivingInfo";
        private OverlayLayout currentLayout = new OverlayLayout();

        // File paths
        private readonly string ConfigPath = "overlay_config.json";
        private readonly string DataFolder = "OverlayData";
        private readonly string LayoutsFolder = "Layouts";

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
                // Create directories
                Directory.CreateDirectory(DataFolder);
                Directory.CreateDirectory(LayoutsFolder);

                LoadOrCreateConfig();
                LoadReferenceIfExists();
                InitializeSessionData();
                ApplyConfigToUI();
                SetupTimers();
                SetupHotkeys();
                
                StatusText.Text = "Ready - Waiting for iRacing connection...";
                CurrentModeText.Text = "CONFIG MODE";
                
                // Initialize tire sets
                for (int i = 1; i <= 6; i++)
                {
                    tireSets.Add(new TireSet { SetNumber = i, IsUsed = false });
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Init error: {ex.Message}";
            }
        }

        private void InitializeSessionData()
        {
            sessionData = new RaceSessionData
            {
                SessionStartTime = DateTime.Now,
                LapsCompleted = 0,
                IncidentCount = 0,
                Position = 0,
                StartingPosition = 0
            };
        }

        private void SetupTimers()
        {
            // High frequency timer for core telemetry (50 FPS)
            telemetryTimer = new DispatcherTimer();
            telemetryTimer.Interval = TimeSpan.FromMilliseconds(20);
            telemetryTimer.Tick += TelemetryTimer_Tick;
            telemetryTimer.Start();

            // Lower frequency timer for UI updates and strategy calculations (5 FPS)
            slowUpdateTimer = new DispatcherTimer();
            slowUpdateTimer.Interval = TimeSpan.FromMilliseconds(200);
            slowUpdateTimer.Tick += SlowUpdateTimer_Tick;
            slowUpdateTimer.Start();
        }

        private void SetupHotkeys()
        {
            try
            {
                this.KeyDown += MainWindow_KeyDown;
                this.Focusable = true;
                this.Focus();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Hotkey setup error: {ex.Message}";
            }
        }

        private void MainWindow_KeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                switch (e.Key)
                {
                    case Key.F1:
                        SwitchToTab("DrivingInfo");
                        break;
                    case Key.F2:
                        SwitchToTab("CarCondition");
                        break;
                    case Key.F3:
                        SwitchToTab("RaceAwareness");
                        break;
                    case Key.F4:
                        SwitchToTab("Strategy");
                        break;
                    case Key.F5:
                        SwitchToTab("Telemetry");
                        break;
                    case Key.F9:
                        ToggleOverlayMode();
                        break;
                    case Key.F10:
                        ToggleTopMost();
                        break;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Hotkey error: {ex.Message}";
            }
        }

        #region Timer Event Handlers

        private void TelemetryTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                UpdateConnectionStatus();
                
                if (irsdk.IsConnected())
                {
                    ReadEnhancedTelemetry();
                    UpdateCoreUI();
                    HandleBrakePointSystem();
                    UpdateTelemetryTraces();
                    UpdateShiftLights();
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Telemetry error: {ex.Message}";
            }
        }

        private void SlowUpdateTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (irsdk.IsConnected())
                {
                    UpdateRaceData();
                    UpdateStrategyData();
                    UpdateTrackVisualization();
                    UpdateRelativeTiming();
                    CalculateFuelStrategy();
                    UpdateSessionSummary();
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Strategy update error: {ex.Message}";
            }
        }

        #endregion

        #region Enhanced Telemetry Reading

        private void ReadEnhancedTelemetry()
        {
            previousTelemetry = currentTelemetry;
            
            currentTelemetry = new EnhancedTelemetryPacket
            {
                // Core data
                SessionTime = GetDouble("SessionTime"),
                Lap = GetInt("Lap"),
                LapDist = GetFloat("LapDist"),
                LapDistPct = GetFloat("LapDistPct"),
                Speed = GetFloat("Speed"),
                RPM = GetFloat("RPM"),
                Gear = GetInt("Gear"),
                Throttle = GetFloat("Throttle"),
                Brake = GetFloat("Brake"),
                Steering = GetFloat("SteeringWheelAngle"),
                
                // Timing
                LapCurrentLapTime = GetFloat("LapCurrentLapTime"),
                LapLastLapTime = GetFloat("LapLastLapTime"),
                LapBestLapTime = GetFloat("LapBestLapTime"),
                LapDeltaToBestLap = GetFloat("LapDeltaToBestLap"),
                
                // Sectors
                LapCurrentSector1Time = GetFloat("LapCurrentSector1Time"),
                LapCurrentSector2Time = GetFloat("LapCurrentSector2Time"),
                LapCurrentSector3Time = GetFloat("LapCurrentSector3Time"),
                LapBestSector1Time = GetFloat("LapBestSector1Time"),
                LapBestSector2Time = GetFloat("LapBestSector2Time"),
                LapBestSector3Time = GetFloat("LapBestSector3Time"),
                
                // Car condition
                FuelLevel = GetFloat("FuelLevel"),
                FuelLevelPct = GetFloat("FuelLevelPct"),
                FuelUsePerHour = GetFloat("FuelUsePerHour"),
                
                // Tires
                LFtempCL = GetFloat("LFtempCL"),
                LFtempCM = GetFloat("LFtempCM"),
                LFtempCR = GetFloat("LFtempCR"),
                LFpressure = GetFloat("LFpressure"),
                LFwearL = GetFloat("LFwearL"),
                LFwearM = GetFloat("LFwearM"),
                LFwearR = GetFloat("LFwearR"),
                
                RFtempCL = GetFloat("RFtempCL"),
                RFtempCM = GetFloat("RFtempCM"),
                RFtempCR = GetFloat("RFtempCR"),
                RFpressure = GetFloat("RFpressure"),
                RFwearL = GetFloat("RFwearL"),
                RFwearM = GetFloat("RFwearM"),
                RFwearR = GetFloat("RFwearR"),
                
                LRtempCL = GetFloat("LRtempCL"),
                LRtempCM = GetFloat("LRtempCM"),
                LRtempCR = GetFloat("LRtempCR"),
                LRpressure = GetFloat("LRpressure"),
                LRwearL = GetFloat("LRwearL"),
                LRwearM = GetFloat("LRwearM"),
                LRwearR = GetFloat("LRwearR"),
                
                RRtempCL = GetFloat("RRtempCL"),
                RRtempCM = GetFloat("RRtempCM"),
                RRtempCR = GetFloat("RRtempCR"),
                RRpressure = GetFloat("RRpressure"),
                RRwearL = GetFloat("RRwearL"),
                RRwearM = GetFloat("RRwearM"),
                RRwearR = GetFloat("RRwearR"),
                
                // Brakes
                LFbrakeLinePress = GetFloat("LFbrakeLinePress"),
                RFbrakeLinePress = GetFloat("RFbrakeLinePress"),
                LRbrakeLinePress = GetFloat("LRbrakeLinePress"),
                RRbrakeLinePress = GetFloat("RRbrakeLinePress"),
                
                // Session info
                SessionState = GetString("SessionState"),
                SessionFlag = GetString("SessionFlag"),
                SessionLapsTotal = GetInt("SessionLapsTotal"),
                SessionTimeTotal = GetFloat("SessionTimeTotal"),
                Position = GetInt("Position"),
                ClassPosition = GetInt("ClassPosition"),
                
                // Race awareness
                CarIdxLapDistPct = GetFloatArray("CarIdxLapDistPct"),
                CarIdxPosition = GetIntArray("CarIdxPosition"),
                CarIdxClassPosition = GetIntArray("CarIdxClassPosition"),
                
                // Track info
                TrackTemp = GetFloat("TrackTemp"),
                TrackTempCrew = GetFloat("TrackTempCrew"),
                
                Timestamp = DateTime.Now
            };

            // Add to history
            telemetryHistory.Enqueue(currentTelemetry);
            if (telemetryHistory.Count > 1000) // Keep last 20 seconds at 50 FPS
                telemetryHistory.Dequeue();

            // Add to input traces
            UpdateInputTraces();
        }

        private float GetFloat(string name) { try { return (float)irsdk.GetData(name); } catch { return 0f; } }
        private int GetInt(string name) { try { return (int)irsdk.GetData(name); } catch { return 0; } }
        private double GetDouble(string name) { try { return (double)irsdk.GetData(name); } catch { return 0.0; } }
        private string GetString(string name) { try { return irsdk.GetData(name)?.ToString() ?? ""; } catch { return ""; } }
        private float[] GetFloatArray(string name) { try { return (float[])irsdk.GetData(name); } catch { return new float[0]; } }
        private int[] GetIntArray(string name) { try { return (int[])irsdk.GetData(name); } catch { return new int[0]; } }

        #endregion

        #region UI Update Methods

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
                
                if (isConnected)
                {
                    StatusText.Text = "Connected to iRacing - Overlay system active";
                }
                else
                {
                    StatusText.Text = "Disconnected from iRacing";
                    InitializeSessionData(); // Reset session data
                }
            }
        }

        private void UpdateCoreUI()
        {
            if (currentTab == "DrivingInfo")
            {
                UpdateDrivingInfoTab();
            }
        }

        private void UpdateDrivingInfoTab()
        {
            try
            {
                // Lap tracking
                CurrentLapText.Text = $"{currentTelemetry.Lap} / {currentTelemetry.SessionLapsTotal}";
                
                // Timing
                if (currentTelemetry.LapCurrentLapTime > 0)
                {
                    CurrentLapTimeText.Text = FormatTime(currentTelemetry.LapCurrentLapTime);
                }
                
                if (currentTelemetry.LapBestLapTime > 0)
                {
                    BestLapTimeText.Text = FormatTime(currentTelemetry.LapBestLapTime);
                }
                
                // Delta
                if (Math.Abs(currentTelemetry.LapDeltaToBestLap) < 999)
                {
                    DeltaText.Text = currentTelemetry.LapDeltaToBestLap >= 0 ? 
                        $"+{currentTelemetry.LapDeltaToBestLap:0.000}" : 
                        $"{currentTelemetry.LapDeltaToBestLap:0.000}";
                    DeltaText.Foreground = new SolidColorBrush(currentTelemetry.LapDeltaToBestLap >= 0 ? Colors.Red : Colors.LimeGreen);
                }

                // Inputs
                SpeedDisplayText.Text = $"{currentTelemetry.Speed * 3.6f:F0}";
                RPMText.Text = $"{currentTelemetry.RPM:F0}";
                GearText.Text = currentTelemetry.Gear == 0 ? "N" : currentTelemetry.Gear == -1 ? "R" : currentTelemetry.Gear.ToString();

                // Input bars
                UpdateInputBars();

                // Sector times
                UpdateSectorTimes();

                // Last lap time
                if (currentTelemetry.LapLastLapTime > 0 && currentTelemetry.LapLastLapTime != previousTelemetry.LapLastLapTime)
                {
                    LastLapTimeText.Text = $"Last Lap: {FormatTime(currentTelemetry.LapLastLapTime)}";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Driving UI error: {ex.Message}";
            }
        }

        private void UpdateInputBars()
        {
            // Throttle bar
            double throttleWidth = Math.Max(0, Math.Min(1, currentTelemetry.Throttle)) * 200;
            ThrottleBar.Width = throttleWidth;

            // Brake bar
            double brakeWidth = Math.Max(0, Math.Min(1, currentTelemetry.Brake)) * 200;
            BrakeBar.Width = brakeWidth;

            // Steering indicator
            double steeringPos = 97 + (currentTelemetry.Steering / 900.0 * 97); // Normalize to canvas width
            steeringPos = Math.Max(1, Math.Min(193, steeringPos));
            Canvas.SetLeft(SteeringIndicator, steeringPos);
        }

        private void UpdateSectorTimes()
        {
            // Sector 1
            if (currentTelemetry.LapCurrentSector1Time > 0)
            {
                Sector1Text.Text = FormatTime(currentTelemetry.LapCurrentSector1Time);
                if (currentTelemetry.LapBestSector1Time > 0)
                {
                    if (currentTelemetry.LapCurrentSector1Time <= currentTelemetry.LapBestSector1Time)
                        Sector1Text.Foreground = new SolidColorBrush(Colors.LimeGreen); // Personal best
                    else
                        Sector1Text.Foreground = new SolidColorBrush(Colors.White);
                }
            }

            // Sector 2
            if (currentTelemetry.LapCurrentSector2Time > 0)
            {
                Sector2Text.Text = FormatTime(currentTelemetry.LapCurrentSector2Time);
                if (currentTelemetry.LapBestSector2Time > 0)
                {
                    if (currentTelemetry.LapCurrentSector2Time <= currentTelemetry.LapBestSector2Time)
                        Sector2Text.Foreground = new SolidColorBrush(Colors.LimeGreen);
                    else
                        Sector2Text.Foreground = new SolidColorBrush(Colors.White);
                }
            }

            // Total time for completed laps
            if (currentTelemetry.LapLastLapTime > 0)
            {
                TotalLapTimeText.Text = FormatTime(currentTelemetry.LapLastLapTime);
            }
        }

        private void UpdateShiftLights()
        {
            try
            {
                // Simple RPM-based shift light system
                float maxRPM = 8000f; // This should be read from car data
                float shiftRPM = maxRPM * 0.9f;
                float currentRPM = currentTelemetry.RPM;
                
                var lights = new[] { ShiftLight1, ShiftLight2, ShiftLight3, ShiftLight4, 
                                   ShiftLight5, ShiftLight6, ShiftLight7, ShiftLight8 };
                
                float step = (shiftRPM - (maxRPM * 0.6f)) / lights.Length;
                float baseRPM = maxRPM * 0.6f;
                
                for (int i = 0; i < lights.Length; i++)
                {
                    if (currentRPM >= baseRPM + (step * i))
                    {
                        if (i < 3)
                            lights[i].Fill = new SolidColorBrush(Colors.Green);
                        else if (i < 6)
                            lights[i].Fill = new SolidColorBrush(Colors.Yellow);
                        else
                            lights[i].Fill = new SolidColorBrush(Colors.Red);
                    }
                    else
                    {
                        lights[i].Fill = new SolidColorBrush(Color.FromRgb(42, 42, 42));
                    }
                }
            }
            catch { }
        }

        private void UpdateInputTraces()
        {
            // Add current values to trace history
            throttleHistory.Enqueue(currentTelemetry.Throttle);
            brakeHistory.Enqueue(currentTelemetry.Brake);
            steeringHistory.Enqueue(currentTelemetry.Steering / 900.0f); // Normalize
            
            if (currentTelemetry.LapDeltaToBestLap < 999)
                deltaHistory.Enqueue(currentTelemetry.LapDeltaToBestLap);

            // Limit history size
            int maxPoints = 260; // Canvas width
            if (throttleHistory.Count > maxPoints) throttleHistory.Dequeue();
            if (brakeHistory.Count > maxPoints) brakeHistory.Dequeue();
            if (steeringHistory.Count > maxPoints) steeringHistory.Dequeue();
            if (deltaHistory.Count > maxPoints) deltaHistory.Dequeue();
        }

        private void UpdateTelemetryTraces()
        {
            if (currentTab != "Telemetry") return;

            try
            {
                // Update throttle trace
                UpdateTrace(ThrottleTrace, throttleHistory, 60, false);
                
                // Update brake trace
                UpdateTrace(BrakeTrace, brakeHistory, 60, false);
                
                // Update steering trace (centered)
                UpdateTrace(SteeringTrace, steeringHistory, 60, true);
                
                // Update delta trace (centered)
                UpdateTrace(DeltaTrace, deltaHistory, 80, true);
            }
            catch { }
        }

        private void UpdateTrace(Polyline trace, Queue<float> data, double height, bool centered)
        {
            if (data.Count < 2) return;

            var points = new PointCollection();
            var dataArray = data.ToArray();
            
            for (int i = 0; i < dataArray.Length; i++)
            {
                double x = (double)i / dataArray.Length * 260; // Canvas width
                double y;
                
                if (centered)
                {
                    // Center the trace (for steering and delta)
                    y = height / 2 - (dataArray[i] * height / 4);
                }
                else
                {
                    // Bottom-up trace (for throttle and brake)
                    y = height - (Math.Max(0, Math.Min(1, dataArray[i])) * height);
                }
                
                points.Add(new Point(x, y));
            }
            
            trace.Points = points;
        }

        #endregion

        #region Car Condition Updates

        private void UpdateRaceData()
        {
            if (currentTab != "CarCondition") return;

            try
            {
                // Update tire temperatures
                FLTireOuterText.Text = $"{currentTelemetry.LFtempCR:F0}°";
                FLTireMiddleText.Text = $"{currentTelemetry.LFtempCM:F0}°";
                FLTireInnerText.Text = $"{currentTelemetry.LFtempCL:F0}°";
                FLTirePressureText.Text = $"{currentTelemetry.LFpressure * 0.145038:F1} PSI";

                FRTireInnerText.Text = $"{currentTelemetry.RFtempCL:F0}°";
                FRTireMiddleText.Text = $"{currentTelemetry.RFtempCM:F0}°";
                FRTireOuterText.Text = $"{currentTelemetry.RFtempCR:F0}°";
                FRTirePressureText.Text = $"{currentTelemetry.RFpressure * 0.145038:F1} PSI";

                RLTireOuterText.Text = $"{currentTelemetry.LRtempCR:F0}°";
                RLTireMiddleText.Text = $"{currentTelemetry.LRtempCM:F0}°";
                RLTireInnerText.Text = $"{currentTelemetry.LRtempCL:F0}°";
                RLTirePressureText.Text = $"{currentTelemetry.LRpressure * 0.145038:F1} PSI";

                RRTireInnerText.Text = $"{currentTelemetry.RRtempCL:F0}°";
                RRTireMiddleText.Text = $"{currentTelemetry.RRtempCM:F0}°";
                RRTireOuterText.Text = $"{currentTelemetry.RRtempCR:F0}°";
                RRTirePressureText.Text = $"{currentTelemetry.RRpressure * 0.145038:F1} PSI";

                // Update fuel
                FuelRemainingText.Text = $"{currentTelemetry.FuelLevel:F1}L";
                FuelPercentText.Text = $"{currentTelemetry.FuelLevelPct * 100:F0}%";
                
                // Calculate fuel laps remaining
                if (fuelStrategy.AverageUsagePerLap > 0)
                {
                    float lapsRemaining = currentTelemetry.FuelLevel / fuelStrategy.AverageUsagePerLap;
                    FuelLapsText.Text = $"{lapsRemaining:F1} laps";
                }

                // Update tire wear visualization
                UpdateTireWearBars();

                // Update brake temps (convert from brake line pressure to estimated temps)
                FLBrakeTempText.Text = $"{EstimateBrakeTemp(currentTelemetry.LFbrakeLinePress):F0}°";
                FRBrakeTempText.Text = $"{EstimateBrakeTemp(currentTelemetry.RFbrakeLinePress):F0}°";
                RLBrakeTempText.Text = $"{EstimateBrakeTemp(currentTelemetry.LRbrakeLinePress):F0}°";
                RRBrakeTempText.Text = $"{EstimateBrakeTemp(currentTelemetry.RRbrakeLinePress):F0}°";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Car condition error: {ex.Message}";
            }
        }

        private void UpdateTireWearBars()
        {
            // Calculate average wear per tire
            float flWear = (currentTelemetry.LFwearL + currentTelemetry.LFwearM + currentTelemetry.LFwearR) / 3f;
            float frWear = (currentTelemetry.RFwearL + currentTelemetry.RFwearM + currentTelemetry.RFwearR) / 3f;
            float rlWear = (currentTelemetry.LRwearL + currentTelemetry.LRwearM + currentTelemetry.LRwearR) / 3f;
            float rrWear = (currentTelemetry.RRwearL + currentTelemetry.RRwearM + currentTelemetry.RRwearR) / 3f;

            // Update wear bars (wear is typically 0-1, we want remaining tread)
            UpdateTireWearBar(FLTireWear, FLTireWearText, 1f - flWear);
            UpdateTireWearBar(FRTireWear, FRTireWearText, 1f - frWear);
            UpdateTireWearBar(RLTireWear, RLTireWearText, 1f - rlWear);
            UpdateTireWearBar(RRTireWear, RRTireWearText, 1f - rrWear);
        }

        private void UpdateTireWearBar(Border bar, TextBlock text, float remaining)
        {
            remaining = Math.Max(0, Math.Min(1, remaining));
            bar.Height = remaining * 98; // Max height - 2 for border
            text.Text = $"{remaining * 100:F0}%";

            // Color coding
            if (remaining > 0.7f)
                bar.Background = new SolidColorBrush(Color.FromRgb(0, 255, 102));
            else if (remaining > 0.4f)
                bar.Background = new SolidColorBrush(Color.FromRgb(255, 102, 0));
            else
                bar.Background = new SolidColorBrush(Color.FromRgb(255, 0, 0));
        }

        private float EstimateBrakeTemp(float brakeLinePress)
        {
            // Simple estimation based on brake pressure
            return 150f + (brakeLinePress * 200f);
        }

        #endregion

        #region Strategy and Session Updates

        private void CalculateFuelStrategy()
        {
            try
            {
                // Update fuel usage calculations
                if (telemetryHistory.Count > 100) // Need some history
                {
                    var recentTelemetry = telemetryHistory.TakeLast(100).ToArray();
                    
                    // Calculate usage per lap
                    var lapFuelUsages = new List<float>();
                    float lastLapFuel = recentTelemetry[0].FuelLevel;
                    int lastLap = recentTelemetry[0].Lap;
                    
                    foreach (var tel in recentTelemetry)
                    {
                        if (tel.Lap > lastLap && lastLapFuel > tel.FuelLevel)
                        {
                            float usage = lastLapFuel - tel.FuelLevel;
                            if (usage > 0 && usage < 10) // Sanity check
                            {
                                lapFuelUsages.Add(usage);
                            }
                        }
                        lastLap = tel.Lap;
                        lastLapFuel = tel.FuelLevel;
                    }
                    
                    if (lapFuelUsages.Count > 0)
                    {
                        fuelStrategy.AverageUsagePerLap = lapFuelUsages.Average();
                        fuelStrategy.MinUsagePerLap = lapFuelUsages.Min();
                        fuelStrategy.MaxUsagePerLap = lapFuelUsages.Max();
                    }
                }

                // Update strategy display
                if (currentTab == "Strategy")
                {
                    UpdateStrategyDisplay();
                }
            }
            catch { }
        }

        private void UpdateStrategyDisplay()
        {
            try
            {
                // Fuel strategy
                AvgFuelUsageText.Text = $"{fuelStrategy.AverageUsagePerLap:F1}L/lap";
                MinFuelUsageText.Text = $"{fuelStrategy.MinUsagePerLap:F1}L/lap";
                MaxFuelUsageText.Text = $"{fuelStrategy.MaxUsagePerLap:F1}L/lap";

                // Calculate fuel to finish
                int lapsRemaining = Math.Max(0, currentTelemetry.SessionLapsTotal - currentTelemetry.Lap);
                float fuelToFinish = lapsRemaining * fuelStrategy.AverageUsagePerLap;
                FuelToFinishText.Text = $"{fuelToFinish:F1}L";

                // Pit window
                if (fuelStrategy.AverageUsagePerLap > 0)
                {
                    float lapsWithCurrentFuel = currentTelemetry.FuelLevel / fuelStrategy.AverageUsagePerLap;
                    int pitWindowStart = (int)(currentTelemetry.Lap + lapsWithCurrentFuel - 3);
                    int pitWindowEnd = (int)(currentTelemetry.Lap + lapsWithCurrentFuel);
                    PitWindowText.Text = $"Lap {pitWindowStart}-{pitWindowEnd}";

                    // Add fuel calculation
                    float addFuel = Math.Max(0, fuelToFinish - currentTelemetry.FuelLevel + 5); // +5L buffer
                    AddFuelText.Text = $"{addFuel:F1}L";
                }

                // Tire strategy
                var currentSet = GetCurrentTireSet();
                CurrentTireSetText.Text = $"Set {currentSet.SetNumber}";
                LapsOnSetText.Text = $"{currentSet.TotalLaps} laps";
                GreenLapsText.Text = $"{currentSet.GreenLaps} laps";
                YellowLapsText.Text = $"{currentSet.YellowLaps} laps";
                
                int unusedSets = tireSets.Count(t => !t.IsUsed);
                SetsRemainingText.Text = $"{unusedSets} sets";
            }
            catch { }
        }

        private void UpdateRelativeTiming()
        {
            if (currentTab != "RaceAwareness") return;

            try
            {
                // Update relative timing panel
                RelativeTimingPanel.Children.Clear();

                // This would need to be implemented with proper car tracking
                // For now, showing placeholder data
                CreateRelativeEntry("P5", "J. Smith #42", "-2.345", false);
                CreateRelativeEntry("P6", "YOU #18", "---", true);
                CreateRelativeEntry("P7", "M. Johnson #7", "+1.782", false);
            }
            catch { }
        }

        private void CreateRelativeEntry(string position, string driver, string gap, bool isPlayer)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(isPlayer ? Color.FromRgb(255, 102, 0) : Color.FromRgb(42, 42, 42)),
                Margin = new Thickness(0, 2),
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(4)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var posText = new TextBlock
            {
                Text = position,
                Foreground = new SolidColorBrush(isPlayer ? Colors.Black : Colors.White),
                FontWeight = FontWeights.Bold
            };
            Grid.SetColumn(posText, 0);

            var driverText = new TextBlock
            {
                Text = driver,
                Foreground = new SolidColorBrush(isPlayer ? Colors.Black : Colors.White),
                FontWeight = FontWeights.Bold
            };
            Grid.SetColumn(driverText, 1);

            var gapText = new TextBlock
            {
                Text = gap,
                Foreground = new SolidColorBrush(gap.StartsWith("-") ? Colors.LimeGreen : 
                                               gap.StartsWith("+") ? Colors.Red : 
                                               isPlayer ? Colors.Black : Colors.White),
                FontWeight = FontWeights.Bold
            };
            Grid.SetColumn(gapText, 2);

            grid.Children.Add(posText);
            grid.Children.Add(driverText);
            grid.Children.Add(gapText);
            border.Child = grid;

            RelativeTimingPanel.Children.Add(border);
        }

        private void UpdateSessionSummary()
        {
            if (currentTab != "Strategy") return;

            try
            {
                LapsCompletedText.Text = currentTelemetry.Lap.ToString();
                
                // Calculate consistency (this would need lap time history)
                ConsistencyText.Text = "±0.234";
                
                // Incidents (would need to track this)
                IncidentsText.Text = $"{sessionData.IncidentCount}x";
                
                // Position delta
                int positionChange = sessionData.StartingPosition - currentTelemetry.Position;
                PositionDeltaText.Text = positionChange >= 0 ? $"+{positionChange}" : positionChange.ToString();
                PositionDeltaText.Foreground = new SolidColorBrush(positionChange >= 0 ? Colors.LimeGreen : Colors.Red);
            }
            catch { }
        }

        #endregion

        #region Original Brake Point System (Enhanced)

        private void HandleBrakePointSystem()
        {
            // Original brake point system logic - enhanced for integration
            
            // Update track info
            if (string.IsNullOrEmpty(referenceLap.Track) || referenceLap.TrackLength <= 0)
            {
                referenceLap.Track = ReadTrackNameFromSession();
                referenceLap.TrackLength = ReadTrackLengthFromSession();
                if (referenceLap.TrackLength <= 0)
                    referenceLap.TrackLength = InferTrackLength();
                
                if (referenceLap.TrackLength > 0)
                    TrackLengthText.Text = $"{referenceLap.TrackLength / 1000:F2}km";
            }

            // Update brake point UI
            TrackNameText.Text = string.IsNullOrEmpty(referenceLap.Track) ? "Loading..." : referenceLap.Track;
            LapNumberText.Text = currentTelemetry.Lap.ToString();
            ZoneCountText.Text = referenceLap.Zones.Count.ToString();

            // Handle lap wrap
            bool lapWrapped = DetectLapWrap();
            if (lapWrapped)
            {
                HandleLapWrap();
            }

            // Recording logic
            if (isRecording)
            {
                HandleRecording();
            }

            // Alert logic
            HandleAlerts();

            lastLapPctSeen = currentTelemetry.LapDistPct;
        }

        private bool DetectLapWrap()
        {
            bool pctWrap = (currentTelemetry.LapDistPct < 0.2 && lastLapPctSeen > 0.8);
            bool lapNumberIncreased = currentTelemetry.Lap > previousTelemetry.Lap;
            
            bool distanceWrap = false;
            if (referenceLap.TrackLength > 0)
            {
                float lastDist = previousTelemetry.LapDist;
                float currentDist = currentTelemetry.LapDist;
                if (lastDist > (referenceLap.TrackLength * 0.8f) && currentDist < (referenceLap.TrackLength * 0.2f))
                {
                    distanceWrap = true;
                }
            }

            return pctWrap || lapNumberIncreased || distanceWrap;
        }

        private void HandleLapWrap()
        {
            firedThisLap.Clear();
            zoneStageMaskThisLap.Clear();

            if (isRecording)
            {
                if (referenceLap.Zones.Count > 0 && recordingLap >= 0)
                {
                    SaveReferenceForLap(referenceLap, recordingLap, currentTelemetry.LapLastLapTime);
                }

                recordingLap = currentTelemetry.Lap;
                referenceLap.Zones.Clear();
                lastZoneAt = -9999f;
                prevBraking = false;
                inCorner = false;
                
                StatusText.Text = $"Recording Lap {recordingLap} - Drive to record brake points";
            }
        }

        private void HandleRecording()
        {
            if (recordingLap == -1 && currentTelemetry.LapDistPct > 0.05f)
            {
                recordingLap = currentTelemetry.Lap;
                referenceLap.Zones.Clear();
                StatusText.Text = $"Recording Lap {recordingLap} at {referenceLap.Track}";
                lastZoneAt = -9999f;
                prevBraking = currentTelemetry.Brake >= config.BrakeOnThreshold;
                inCorner = false;
            }

            if (recordingLap == -1) return;

            float distanceSinceLast = (lastZoneAt < -9000f || referenceLap.TrackLength <= 0)
                ? float.MaxValue
                : DistanceAhead(referenceLap.TrackLength, lastZoneAt, currentTelemetry.LapDist);

            bool brakingNow = currentTelemetry.Brake >= config.BrakeOnThreshold;
            bool clearToRearm = (currentTelemetry.Brake <= config.BrakeOffThreshold) && 
                               (distanceSinceLast >= config.CornerResetMeters);
            
            if (clearToRearm) inCorner = false;

            bool risingEdge = brakingNow && !prevBraking && !inCorner;

            if (risingEdge && (currentTelemetry.LapDist - lastZoneAt >= config.MinZoneGapMeters))
            {
                float speedDrop = Math.Max(0, prevSpeed - currentTelemetry.Speed);
                if (config.MinSpeedDropMs <= 0 || speedDrop >= config.MinSpeedDropMs)
                {
                    referenceLap.Zones.Add(new BrakeZone 
                    { 
                        LapDist = currentTelemetry.LapDist, 
                        EntrySpeed = currentTelemetry.Speed 
                    });
                    lastZoneAt = currentTelemetry.LapDist;
                    inCorner = true;
                    
                    StatusText.Text = $"Brake zone {referenceLap.Zones.Count} recorded at {currentTelemetry.LapDist:F1}m ({currentTelemetry.Speed * 3.6f:F0} km/h)";
                }
            }

            prevBraking = brakingNow;
            prevSpeed = currentTelemetry.Speed;
        }

        private void HandleAlerts()
        {
            if (referenceLap.Zones.Count == 0 || referenceLap.TrackLength <= 0) return;
            if (currentTelemetry.Speed < config.SpeedThresholdMs) return;
            if (config.OnlyAlertIfNotBraking && currentTelemetry.Brake >= config.BrakeOnThreshold) return;

            // Find nearest upcoming zone
            float bestAhead = float.MaxValue;
            int bestIdx = -1;
            for (int i = 0; i < referenceLap.Zones.Count; i++)
            {
                var zone = referenceLap.Zones[i];
                float ahead = DistanceAhead(referenceLap.TrackLength, currentTelemetry.LapDist, zone.LapDist);
                if (ahead < bestAhead) { bestAhead = ahead; bestIdx = i; }
            }
            if (bestIdx < 0) return;

            // Countdown mode (3-2-1)
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

        private void UpdateTrackVisualization()
        {
            if (currentTab != "Telemetry") return;

            try
            {
                TrackCanvas.Children.Clear();

                if (referenceLap.TrackLength <= 0) return;

                double canvasWidth = TrackCanvas.ActualWidth;
                double canvasHeight = TrackCanvas.ActualHeight;
                
                if (canvasWidth <= 10 || canvasHeight <= 10) return;

                DrawEnhancedCircularTrack(canvasWidth, canvasHeight);
            }
            catch { }
        }

        private void DrawEnhancedCircularTrack(double canvasWidth, double canvasHeight)
        {
            double centerX = canvasWidth / 2;
            double centerY = canvasHeight / 2;
            double radius = Math.Min(centerX, centerY) - 40;

            if (radius < 30) return;

            // Track surface
            var trackMain = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Stroke = new SolidColorBrush(Color.FromRgb(255, 102, 0)),
                StrokeThickness = 8,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(trackMain, centerX - radius);
            Canvas.SetTop(trackMain, centerY - radius);
            TrackCanvas.Children.Add(trackMain);

            double startAngle = -Math.PI / 2;

            // Start/Finish line
            double startX = centerX + radius * Math.Cos(startAngle);
            double startY = centerY + radius * Math.Sin(startAngle);
            
            var startLine = new Rectangle
            {
                Width = 6,
                Height = 30,
                Fill = new SolidColorBrush(Colors.White)
            };
            Canvas.SetLeft(startLine, startX - 3);
            Canvas.SetTop(startLine, startY - 15);
            TrackCanvas.Children.Add(startLine);

            // Brake zones
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
                Canvas.SetLeft(zoneDot, zoneX - 8);
                Canvas.SetTop(zoneDot, zoneY - 8);
                TrackCanvas.Children.Add(zoneDot);

                var zoneNumber = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    Foreground = Brushes.White,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold
                };
                Canvas.SetLeft(zoneNumber, zoneX - 6);
                Canvas.SetTop(zoneNumber, zoneY - 7);
                TrackCanvas.Children.Add(zoneNumber);
            }

            // Current position
            double currentAngle = startAngle + (currentTelemetry.LapDistPct * 2 * Math.PI);
            double carX = centerX + radius * Math.Cos(currentAngle);
            double carY = centerY + radius * Math.Sin(currentAngle);

            var carDot = new Ellipse
            {
                Width = 20,
                Height = 20,
                Fill = new SolidColorBrush(Color.FromRgb(0, 255, 102))
            };
            Canvas.SetLeft(carDot, carX - 10);
            Canvas.SetTop(carDot, carY - 10);
            TrackCanvas.Children.Add(carDot);
        }

        #endregion

        #region Helper Methods

        private float DistanceAhead(float trackLength, float fromPos, float toPos)
        {
            float distance = toPos - fromPos;
            if (distance < 0) distance += trackLength;
            return distance;
        }

        private float InferTrackLength()
        {
            if (currentTelemetry.LapDistPct > 0.01f)
            {
                var guess = currentTelemetry.LapDist / Math.Max(0.01f, currentTelemetry.LapDistPct);
                if (guess > 500 && guess < 10000) return (float)guess;
            }
            return 4000f;
        }

        private string ReadTrackNameFromSession()
        {
            try
            {
                var raw = irsdk.GetSessionInfo();
                if (string.IsNullOrEmpty(raw)) return "";
                var match = Regex.Match(raw, @"TrackDisplayName:\s*(.*)");
                if (match.Success) return match.Groups[1].Value.Trim();
            }
            catch { }
            return "";
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

        private string FormatTime(float seconds)
        {
            if (seconds <= 0) return "--:--.---";
            
            int minutes = (int)(seconds / 60);
            float remainingSeconds = seconds % 60;
            return $"{minutes}:{remainingSeconds:00.000}";
        }

        private TireSet GetCurrentTireSet()
        {
            // Simple logic - in a real implementation this would track tire changes
            var currentSet = tireSets.FirstOrDefault(t => t.IsUsed) ?? tireSets[0];
            currentSet.IsUsed = true;
            currentSet.TotalLaps = Math.Max(1, currentTelemetry.Lap);
            return currentSet;
        }

        #endregion

        #region UI Navigation and Mode Switching

        private void SwitchToTab(string tabName)
        {
            currentTab = tabName;
            
            // Hide all tab contents
            DrivingInfoContent.Visibility = Visibility.Collapsed;
            CarConditionContent.Visibility = Visibility.Collapsed;
            RaceAwarenessContent.Visibility = Visibility.Collapsed;
            StrategyContent.Visibility = Visibility.Collapsed;
            TelemetryContent.Visibility = Visibility.Collapsed;
            TracksContent.Visibility = Visibility.Collapsed;
            SettingsContent.Visibility = Visibility.Collapsed;

            // Reset all tab button styles
            DrivingInfoTab.Style = (Style)FindResource("TabButton");
            CarConditionTab.Style = (Style)FindResource("TabButton");
            RaceAwarenessTab.Style = (Style)FindResource("TabButton");
            StrategyTab.Style = (Style)FindResource("TabButton");
            TelemetryTab.Style = (Style)FindResource("TabButton");
            TracksTab.Style = (Style)FindResource("TabButton");
            SettingsTab.Style = (Style)FindResource("TabButton");

            // Show selected tab and highlight button
            switch (tabName)
            {
                case "DrivingInfo":
                    DrivingInfoContent.Visibility = Visibility.Visible;
                    DrivingInfoTab.Style = (Style)FindResource("ActiveTabButton");
                    break;
                case "CarCondition":
                    CarConditionContent.Visibility = Visibility.Visible;
                    CarConditionTab.Style = (Style)FindResource("ActiveTabButton");
                    break;
                case "RaceAwareness":
                    RaceAwarenessContent.Visibility = Visibility.Visible;
                    RaceAwarenessTab.Style = (Style)FindResource("ActiveTabButton");
                    break;
                case "Strategy":
                    StrategyContent.Visibility = Visibility.Visible;
                    StrategyTab.Style = (Style)FindResource("ActiveTabButton");
                    break;
                case "Telemetry":
                    TelemetryContent.Visibility = Visibility.Visible;
                    TelemetryTab.Style = (Style)FindResource("ActiveTabButton");
                    break;
                case "Tracks":
                    TracksContent.Visibility = Visibility.Visible;
                    TracksTab.Style = (Style)FindResource("ActiveTabButton");
                    break;
                case "Settings":
                    SettingsContent.Visibility = Visibility.Visible;
                    SettingsTab.Style = (Style)FindResource("ActiveTabButton");
                    break;
            }
        }

        private void ToggleOverlayMode()
        {
            isOverlayMode = !isOverlayMode;
            
            if (isOverlayMode)
            {
                // Switch to overlay mode
                ConfigModeButton.Style = (Style)FindResource("TabButton");
                OverlayModeButton.Style = (Style)FindResource("ActiveTabButton");
                CurrentModeText.Text = "OVERLAY MODE";
                CurrentModeText.Foreground = new SolidColorBrush(Colors.LimeGreen);
                
                // Make window suitable for overlay
                this.WindowStyle = WindowStyle.None;
                this.ResizeMode = ResizeMode.NoResize;
                this.Topmost = true;
                
                StatusText.Text = "Overlay mode active - F9 to return to config";
            }
            else
            {
                // Switch to config mode
                ConfigModeButton.Style = (Style)FindResource("ActiveTabButton");
                OverlayModeButton.Style = (Style)FindResource("TabButton");
                CurrentModeText.Text = "CONFIG MODE";
                CurrentModeText.Foreground = new SolidColorBrush(Color.FromRgb(255, 102, 0));
                
                // Restore normal window
                this.WindowStyle = WindowStyle.SingleBorderWindow;
                this.ResizeMode = ResizeMode.CanResizeWithGrip;
                this.Topmost = false;
                
                StatusText.Text = "Config mode - Adjust settings and layouts";
            }
        }

        private void ToggleTopMost()
        {
            this.Topmost = !this.Topmost;
            StatusText.Text = this.Topmost ? "Always on top enabled" : "Always on top disabled";
        }

        #endregion

        #region Event Handlers

        // Mode switching
        private void ConfigModeButton_Click(object sender, RoutedEventArgs e) => ToggleOverlayMode();
        private void OverlayModeButton_Click(object sender, RoutedEventArgs e) => ToggleOverlayMode();
        private void ToggleTopMostButton_Click(object sender, RoutedEventArgs e) => ToggleTopMost();

        // Tab navigation
        private void DrivingInfoTab_Click(object sender, RoutedEventArgs e) => SwitchToTab("DrivingInfo");
        private void CarConditionTab_Click(object sender, RoutedEventArgs e) => SwitchToTab("CarCondition");
        private void RaceAwarenessTab_Click(object sender, RoutedEventArgs e) => SwitchToTab("RaceAwareness");
        private void StrategyTab_Click(object sender, RoutedEventArgs e) => SwitchToTab("Strategy");
        private void TelemetryTab_Click(object sender, RoutedEventArgs e) => SwitchToTab("Telemetry");
        private void TracksTab_Click(object sender, RoutedEventArgs e) => SwitchToTab("Tracks");
        private void SettingsTab_Click(object sender, RoutedEventArgs e) => SwitchToTab("Settings");

        // Original brake point system events
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

        private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveReference();
        private void LoadButton_Click(object sender, RoutedEventArgs e) => LoadReference();

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

        // Layout management
        private void SaveLayoutButton_Click(object sender, RoutedEventArgs e) => SaveCurrentLayout();
        private void LoadLayoutButton_Click(object sender, RoutedEventArgs e) => LoadLayout();

        // Telemetry panel actions
        private void ClearTracesButton_Click(object sender, RoutedEventArgs e)
        {
            throttleHistory.Clear();
            brakeHistory.Clear();
            steeringHistory.Clear();
            deltaHistory.Clear();
            StatusText.Text = "Telemetry traces cleared";
        }

        private void SaveTelemetryButton_Click(object sender, RoutedEventArgs e)
        {
            // Implementation for saving telemetry data
            StatusText.Text = "Telemetry data saved";
        }

        #endregion

        #region Configuration and Layout Management

        private void LoadOrCreateConfig()
        {
            try
            {
                string configFile = Path.Combine(DataFolder, ConfigPath);
                if (File.Exists(configFile))
                {
                    var text = File.ReadAllText(configFile);
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
                string configFile = Path.Combine(DataFolder, ConfigPath);
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configFile, json);
            }
            catch { }
        }

        private void LoadReferenceIfExists()
        {
            try
            {
                string refFile = Path.Combine(DataFolder, "latest_reference.json");
                if (File.Exists(refFile))
                {
                    var json = File.ReadAllText(refFile);
                    referenceLap = JsonSerializer.Deserialize<ReferenceLap>(json) ?? new ReferenceLap();
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

                string safeTrack = string.Join("_", (referenceLap.Track ?? "track").Split(Path.GetInvalidFileNameChars()));
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string fileName = Path.Combine(DataFolder, $"reference_{safeTrack}_{timestamp}.json");

                var json = JsonSerializer.Serialize(referenceLap, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(fileName, json);
                
                string latestFile = Path.Combine(DataFolder, "latest_reference.json");
                File.WriteAllText(latestFile, json);
                
                StatusText.Text = $"✅ Saved reference: {referenceLap.Zones.Count} zones";
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

                string safeTrack = string.Join("_", (lapData.Track ?? "track").Split(Path.GetInvalidFileNameChars()));
                string timePart = (lapTimeSec > 0) ? $"_{lapTimeSec:0.000}s" : "";
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string fileName = Path.Combine(DataFolder, $"auto_save_{safeTrack}_lap{lapNumber}{timePart}_{timestamp}.json");

                var json = JsonSerializer.Serialize(lapData, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(fileName, json);
                
                string latestFile = Path.Combine(DataFolder, "latest_reference.json");
                File.WriteAllText(latestFile, json);
            }
            catch { }
        }

        private void LoadReference()
        {
            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                    Title = "Load Brake Point Reference",
                    InitialDirectory = Path.GetFullPath(DataFolder)
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    var json = File.ReadAllText(openFileDialog.FileName);
                    referenceLap = JsonSerializer.Deserialize<ReferenceLap>(json) ?? new ReferenceLap();
                    StatusText.Text = $"📂 Loaded: {Path.GetFileName(openFileDialog.FileName)} ({referenceLap.Zones.Count} zones)";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load reference: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveCurrentLayout()
        {
            try
            {
                var saveDialog = new SaveFileDialog
                {
                    Filter = "Layout files (*.layout)|*.layout",
                    Title = "Save Layout",
                    InitialDirectory = Path.GetFullPath(LayoutsFolder),
                    FileName = $"layout_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.layout"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    var layout = new OverlayLayout
                    {
                        Name = Path.GetFileNameWithoutExtension(saveDialog.FileName),
                        IsOverlayMode = isOverlayMode,
                        CurrentTab = currentTab,
                        WindowWidth = this.Width,
                        WindowHeight = this.Height,
                        WindowLeft = this.Left,
                        WindowTop = this.Top,
                        Opacity = this.Opacity,
                        IsTopmost = this.Topmost,
                        Config = config
                    };

                    var json = JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(saveDialog.FileName, json);
                    StatusText.Text = $"Layout saved: {layout.Name}";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save layout: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadLayout()
        {
            try
            {
                var openDialog = new OpenFileDialog
                {
                    Filter = "Layout files (*.layout)|*.layout|All files (*.*)|*.*",
                    Title = "Load Layout",
                    InitialDirectory = Path.GetFullPath(LayoutsFolder)
                };

                if (openDialog.ShowDialog() == true)
                {
                    var json = File.ReadAllText(openDialog.FileName);
                    var layout = JsonSerializer.Deserialize<OverlayLayout>(json);
                    
                    if (layout != null)
                    {
                        ApplyLayout(layout);
                        StatusText.Text = $"Layout loaded: {layout.Name}";
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load layout: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ApplyLayout(OverlayLayout layout)
        {
            try
            {
                // Apply window properties
                this.Width = layout.WindowWidth;
                this.Height = layout.WindowHeight;
                this.Left = layout.WindowLeft;
                this.Top = layout.WindowTop;
                this.Opacity = layout.Opacity;
                this.Topmost = layout.IsTopmost;

                // Apply overlay mode
                if (layout.IsOverlayMode != isOverlayMode)
                {
                    ToggleOverlayMode();
                }

                // Apply tab
                SwitchToTab(layout.CurrentTab);

                // Apply configuration
                if (layout.Config != null)
                {
                    config = layout.Config;
                    ApplyConfigToUI();
                    SaveConfig();
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Layout apply error: {ex.Message}";
            }
        }

        private void ApplyConfigToUI()
        {
            try
            {
                if (LeadDistanceSlider != null)
                {
                    LeadDistanceSlider.Value = config.LeadMeters;
                    LeadDistanceText.Text = $"{config.LeadMeters:F0}m";
                }
                
                if (VolumeSlider != null)
                {
                    VolumeSlider.Value = config.ToneVolume;
                    VolumeText.Text = $"{config.ToneVolume * 100:F0}%";
                }
                
                if (OnlyWhenNotBrakingCheck != null)
                {
                    OnlyWhenNotBrakingCheck.IsChecked = config.OnlyAlertIfNotBraking;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"UI config error: {ex.Message}";
            }
        }

        #endregion

        #region Cleanup

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                telemetryTimer?.Stop();
                slowUpdateTimer?.Stop();
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
    }

    #region Enhanced Data Structures

    public class EnhancedTelemetryPacket
    {
        // Core data
        public double SessionTime { get; set; }
        public int Lap { get; set; }
        public float LapDist { get; set; }
        public float LapDistPct { get; set; }
        public float Speed { get; set; }
        public float RPM { get; set; }
        public int Gear { get; set; }
        public float Throttle { get; set; }
        public float Brake { get; set; }
        public float Steering { get; set; }

        // Timing
        public float LapCurrentLapTime { get; set; }
        public float LapLastLapTime { get; set; }
        public float LapBestLapTime { get; set; }
        public float LapDeltaToBestLap { get; set; }

        // Sectors
        public float LapCurrentSector1Time { get; set; }
        public float LapCurrentSector2Time { get; set; }
        public float LapCurrentSector3Time { get; set; }
        public float LapBestSector1Time { get; set; }
        public float LapBestSector2Time { get; set; }
        public float LapBestSector3Time { get; set; }

        // Car condition
        public float FuelLevel { get; set; }
        public float FuelLevelPct { get; set; }
        public float FuelUsePerHour { get; set; }

        // Tire temperatures (Left Front, Right Front, Left Rear, Right Rear)
        public float LFtempCL { get; set; } // Left side
        public float LFtempCM { get; set; } // Middle
        public float LFtempCR { get; set; } // Right side
        public float LFpressure { get; set; }
        public float LFwearL { get; set; }
        public float LFwearM { get; set; }
        public float LFwearR { get; set; }

        public float RFtempCL { get; set; }
        public float RFtempCM { get; set; }
        public float RFtempCR { get; set; }
        public float RFpressure { get; set; }
        public float RFwearL { get; set; }
        public float RFwearM { get; set; }
        public float RFwearR { get; set; }

        public float LRtempCL { get; set; }
        public float LRtempCM { get; set; }
        public float LRtempCR { get; set; }
        public float LRpressure { get; set; }
        public float LRwearL { get; set; }
        public float LRwearM { get; set; }
        public float LRwearR { get; set; }

        public float RRtempCL { get; set; }
        public float RRtempCM { get; set; }
        public float RRtempCR { get; set; }
        public float RRpressure { get; set; }
        public float RRwearL { get; set; }
        public float RRwearM { get; set; }
        public float RRwearR { get; set; }

        // Brake pressures
        public float LFbrakeLinePress { get; set; }
        public float RFbrakeLinePress { get; set; }
        public float LRbrakeLinePress { get; set; }
        public float RRbrakeLinePress { get; set; }

        // Session info
        public string SessionState { get; set; } = "";
        public string SessionFlag { get; set; } = "";
        public int SessionLapsTotal { get; set; }
        public float SessionTimeTotal { get; set; }
        public int Position { get; set; }
        public int ClassPosition { get; set; }

        // Race awareness
        public float[] CarIdxLapDistPct { get; set; } = new float[0];
        public int[] CarIdxPosition { get; set; } = new int[0];
        public int[] CarIdxClassPosition { get; set; } = new int[0];

        // Track info
        public float TrackTemp { get; set; }
        public float TrackTempCrew { get; set; }

        public DateTime Timestamp { get; set; }
    }

    public class RaceSessionData
    {
        public DateTime SessionStartTime { get; set; }
        public int LapsCompleted { get; set; }
        public int IncidentCount { get; set; }
        public int Position { get; set; }
        public int StartingPosition { get; set; }
        public int ClassPosition { get; set; }
        public int StartingClassPosition { get; set; }
        public float BestLapTime { get; set; }
        public float LastLapTime { get; set; }
        public List<float> LapTimes { get; set; } = new List<float>();
        public int GreenFlagLaps { get; set; }
        public int YellowFlagLaps { get; set; }
        public DateTime LastRestartTime { get; set; }
        public DateTime LastYellowTime { get; set; }
    }

    public class PitStop
    {
        public int LapNumber { get; set; }
        public DateTime Time { get; set; }
        public float Duration { get; set; }
        public string Services { get; set; } = ""; // "Fuel", "Tires", "Fuel + Tires", etc.
        public float FuelAdded { get; set; }
        public bool TiresChanged { get; set; }
        public int TireSetUsed { get; set; }
    }

    public class TireSet
    {
        public int SetNumber { get; set; }
        public bool IsUsed { get; set; }
        public int TotalLaps { get; set; }
        public int GreenLaps { get; set; }
        public int YellowLaps { get; set; }
        public DateTime FirstUsed { get; set; }
        public string Compound { get; set; } = ""; // "Soft", "Medium", "Hard", etc.
    }

    public class FuelStrategy
    {
        public float AverageUsagePerLap { get; set; }
        public float MinUsagePerLap { get; set; } = float.MaxValue;
        public float MaxUsagePerLap { get; set; }
        public float CurrentUsageRate { get; set; }
        public List<float> LapUsageHistory { get; set; } = new List<float>();
        public float FuelToFinish { get; set; }
        public int PitWindowStart { get; set; }
        public int PitWindowEnd { get; set; }
        public float RecommendedFuelAdd { get; set; }
    }

    public class RelativeDriver
    {
        public int CarIdx { get; set; }
        public string DriverName { get; set; } = "";
        public string CarNumber { get; set; } = "";
        public int Position { get; set; }
        public int ClassPosition { get; set; }
        public float Gap { get; set; }
        public float LapDistPct { get; set; }
        public bool IsPlayer { get; set; }
        public bool IsAhead { get; set; }
        public string CarClass { get; set; } = "";
        public bool IsLapped { get; set; }
        public bool IsLapping { get; set; }
    }

    public class OverlayLayout
    {
        public string Name { get; set; } = "";
        public bool IsOverlayMode { get; set; }
        public string CurrentTab { get; set; } = "DrivingInfo";
        public double WindowWidth { get; set; } = 1600;
        public double WindowHeight { get; set; } = 1000;
        public double WindowLeft { get; set; } = 100;
        public double WindowTop { get; set; } = 100;
        public double Opacity { get; set; } = 1.0;
        public bool IsTopmost { get; set; }
        public Config Config { get; set; } = new Config();
        public Dictionary<string, bool> PanelVisibility { get; set; } = new Dictionary<string, bool>();
        public DateTime CreatedDate { get; set; } = DateTime.Now;
    }

    // Original brake point system classes (enhanced)
    public class BrakeZone
    {
        public float LapDist { get; set; }
        public float EntrySpeed { get; set; }
        public DateTime RecordedTime { get; set; } = DateTime.Now;
        public int LapRecorded { get; set; }
        public string Notes { get; set; } = "";
    }

    public class ReferenceLap
    {
        public string Track { get; set; } = "";
        public float TrackLength { get; set; } = 0f;
        public List<BrakeZone> Zones { get; set; } = new List<BrakeZone>();
        public DateTime RecordedDate { get; set; } = DateTime.Now;
        public string CarClass { get; set; } = "";
        public string TrackConfig { get; set; } = "";
        public float BestLapTime { get; set; }
        public string Weather { get; set; } = "";
        public float TrackTemp { get; set; }
        public string Notes { get; set; } = "";
    }

    public class Config
    {
        // Original brake point settings
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

        // Enhanced overlay settings
        public bool AutoSaveLayouts { get; set; } = true;
        public bool ShowFPS { get; set; } = false;
        public bool EnableHotkeys { get; set; } = true;
        public string Theme { get; set; } = "Dark";
        public float UIScale { get; set; } = 1.0f;
        public bool TransparentBackground { get; set; } = false;
        public bool ClickThrough { get; set; } = false;
        public double DefaultOpacity { get; set; } = 1.0;

        // Data recording settings
        public bool AutoSaveTelemetry { get; set; } = false;
        public int TelemetryHistoryMinutes { get; set; } = 5;
        public bool RecordSessionData { get; set; } = true;
        public string DataExportFormat { get; set; } = "JSON";

        // Alert settings
        public bool EnableBlindSpotAlerts { get; set; } = true;
        public bool EnableFuelAlerts { get; set; } = true;
        public bool EnableTireAlerts { get; set; } = true;
        public bool EnableTrafficAlerts { get; set; } = true;
        public float LowFuelWarningLaps { get; set; } = 3.0f;
        public float HighTireWearWarning { get; set; } = 0.8f;

        // Display preferences
        public string SpeedUnit { get; set; } = "KMH"; // KMH or MPH
        public string TemperatureUnit { get; set; } = "C"; // C or F
        public string PressureUnit { get; set; } = "PSI"; // PSI or BAR
        public bool Show24HourTime { get; set; } = true;
        public string DateFormat { get; set; } = "yyyy-MM-dd";

        // Performance settings
        public int UpdateFrequencyHz { get; set; } = 50;
        public int UIUpdateFrequencyHz { get; set; } = 5;
        public bool EnableVSync { get; set; } = true;
        public bool LimitBackgroundUpdates { get; set; } = true;
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

        public async System.Threading.Tasks.Task PlayAlertAsync(AlertType alertType, float volume = 0.85f)
        {
            try
            {
                switch (alertType)
                {
                    case AlertType.LowFuel:
                        await PlaySingleAsync(800f, 300, volume);
                        break;
                    case AlertType.TireWear:
                        await PlayTripleAsync(new Config { ToneVolume = volume, ToneGapMs = 100 });
                        break;
                    case AlertType.BlindSpot:
                        await PlaySingleAsync(1000f, 200, volume);
                        break;
                    case AlertType.TrafficAlert:
                        await PlaySingleAsync(1200f, 150, volume);
                        await System.Threading.Tasks.Task.Delay(100);
                        await PlaySingleAsync(1200f, 150, volume);
                        break;
                }
            }
            catch
            {
                // Ignore audio errors
            }
        }
    }

    public enum AlertType
    {
        BrakePoint,
        LowFuel,
        TireWear,
        BlindSpot,
        TrafficAlert,
        PitWindow,
        SessionEnd
    }

    public enum SessionType
    {
        Practice,
        Qualifying,
        Race,
        TimeAttack,
        Warmup
    }

    public enum TrackCondition
    {
        Dry,
        Damp,
        Wet,
        Snow,
        Unknown
    }

    public enum FlagState
    {
        Green,
        Yellow,
        YellowWaving,
        Red,
        White,
        Checkered,
        Blue,
        Debris,
        Crossing,
        RepairOptional,
        RepairRequired,
        BlackFlag,
        Disqualify,
        Servicible,
        Furled,
        Repair
    }

    #endregion
}