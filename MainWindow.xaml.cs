using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GameOverlay
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen,
            bool sort, int ipVersion, int tblClass, uint reserved);

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public byte localPort1;
            public byte localPort2;
            public byte localPort3;
            public byte localPort4;
            public uint remoteAddr;
            public byte remotePort1;
            public byte remotePort2;
            public byte remotePort3;
            public byte remotePort4;
            public uint owningPid;
        }

        private const int AF_INET = 2;
        private const int TCP_TABLE_OWNER_PID_ALL = 5;
        private const uint ERROR_INSUFFICIENT_BUFFER = 122;

        private const int SW_RESTORE = 9;
        private const int HOTKEY_ID = 9000;

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        private const double SlideLeftFrom = -80;
        private const double SlideTopFrom = -120;
        private const double SlideRightFrom = 80;
        private const double SlideBottomFrom = 120;

        private uint _hotkeyModifiers = MOD_CONTROL | MOD_ALT;
        private Key _hotkeyKey = Key.O;
        private bool _capturingHotkey = false;
        private bool _hotkeyRegistered = false;

        private bool _isDraggingSettings = false;
        private Point _settingsStartScreen;
        private double _settingsStartX, _settingsStartY;

        private bool _isDraggingBrowser = false;
        private Point _browserStartScreen;
        private double _browserStartX, _browserStartY;

        private bool _browserInitialized = false;

        private readonly Dictionary<int, string> _quickSlots = new Dictionary<int, string>();
        private int _nextSlotId = 1;

        private readonly List<string> _allowedPrograms = new List<string>();
        private DispatcherTimer? _programCheckTimer;

        private Process? _presentMon;
        private int _activePid = 0;

        private string[]? _csvHeader;
        private int _pidCol = -1;
        private int _msCol = -1;

        private readonly Dictionary<int, Queue<double>> _frameTimesByPid = new Dictionary<int, Queue<double>>();

        private Ping? _ping;
        private DispatcherTimer? _pingTimer;
        private bool _pingInProgress = false;

        private double _graphMaxPing = 200.0;
        private int _pingIntervalMs = 1000;
        private bool _pingActiveProcess = true;

        private string _pingTarget = "8.8.8.8";
        private int _pingTargetPid = 0;
        private DateTime _lastTargetUpdate = DateTime.MinValue;

        private readonly List<double> _pingHistory = new List<double>();
        private const int MaxGraphPoints = 60;

        private bool _overlayAnimating = false;
        private bool _animationsEnabled = true;

        public MainWindow()
        {
            InitializeComponent();

            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;

            PreviewKeyDown += MainWindow_PreviewKeyDown;

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && !_capturingHotkey)
                {
                    if (SettingsPanel.Visibility == Visibility.Visible)
                        SettingsPanel.Visibility = Visibility.Collapsed;
                    else if (BrowserPanel.Visibility == Visibility.Visible)
                        BrowserPanel.Visibility = Visibility.Collapsed;
                    else
                        HideWithAnimation();
                }
            };

            ContentRendered += (s, e) =>
            {
                ResetOverlayAnimationForShow();
                ShowWithAnimation();
            };

            for (int i = 0; i < 4; i++)
                AddSlot();

            _programCheckTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _programCheckTimer.Tick += (s, e) => CheckForegroundProgram();
            _programCheckTimer.Start();

            _ping = new Ping();
            _pingTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(_pingIntervalMs)
            };
            _pingTimer.Tick += (s, e) => PingTick();
            _pingTimer.Start();

            PingTick();

            UpdateHotkeyDisplay();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var helper = new WindowInteropHelper(this);
            var source = HwndSource.FromHwnd(helper.Handle);
            source.AddHook(HwndHook);

            _hotkeyRegistered = RegisterHotKey(helper.Handle, HOTKEY_ID,
                _hotkeyModifiers | MOD_NOREPEAT,
                (uint)KeyInterop.VirtualKeyFromKey(_hotkeyKey));

            StartPresentMon();
        }

        // ===== Анимации =====

        private void ShowWithAnimation()
        {
            if (_overlayAnimating) return;
            _overlayAnimating = true;

            if (!_animationsEnabled)
            {
                LeftPanelTransform.BeginAnimation(TranslateTransform.XProperty, null);
                LeftPanelTransform.X = 0;

                InfoPanelTransform.BeginAnimation(TranslateTransform.YProperty, null);
                InfoPanelTransform.Y = 0;

                SettingsButtonTransform.BeginAnimation(TranslateTransform.XProperty, null);
                SettingsButtonTransform.X = 0;

                BottomPanelTransform.BeginAnimation(TranslateTransform.YProperty, null);
                BottomPanelTransform.Y = 0;

                _overlayAnimating = false;
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(220);

            var a1 = new DoubleAnimation(SlideTopFrom, 0, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(a1, 60);
            InfoPanelTransform.BeginAnimation(TranslateTransform.YProperty, a1);

            var a2 = new DoubleAnimation(SlideLeftFrom, 0, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(a2, 60);
            LeftPanelTransform.BeginAnimation(TranslateTransform.XProperty, a2);

            var a3 = new DoubleAnimation(SlideRightFrom, 0, dur)
            {
                EasingFunction = ease,
                BeginTime = TimeSpan.FromMilliseconds(40)
            };
            Timeline.SetDesiredFrameRate(a3, 60);
            SettingsButtonTransform.BeginAnimation(TranslateTransform.XProperty, a3);

            var a4 = new DoubleAnimation(SlideBottomFrom, 0, dur)
            {
                EasingFunction = ease,
                BeginTime = TimeSpan.FromMilliseconds(40)
            };
            Timeline.SetDesiredFrameRate(a4, 60);
            a4.Completed += (s, e) => _overlayAnimating = false;
            BottomPanelTransform.BeginAnimation(TranslateTransform.YProperty, a4);
        }

        private void HideWithAnimation()
        {
            if (_overlayAnimating) return;
            _overlayAnimating = true;

            if (!_animationsEnabled)
            {
                Hide();
                ResetOverlayAnimation();
                _overlayAnimating = false;
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var dur = TimeSpan.FromMilliseconds(180);

            var a1 = new DoubleAnimation(0, SlideTopFrom, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(a1, 60);
            InfoPanelTransform.BeginAnimation(TranslateTransform.YProperty, a1);

            var a2 = new DoubleAnimation(0, SlideLeftFrom, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(a2, 60);
            LeftPanelTransform.BeginAnimation(TranslateTransform.XProperty, a2);

            var a3 = new DoubleAnimation(0, SlideRightFrom, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(a3, 60);
            SettingsButtonTransform.BeginAnimation(TranslateTransform.XProperty, a3);

            var a4 = new DoubleAnimation(0, SlideBottomFrom, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(a4, 60);
            a4.Completed += (s, e) =>
            {
                Hide();
                ResetOverlayAnimation();
                _overlayAnimating = false;
            };
            BottomPanelTransform.BeginAnimation(TranslateTransform.YProperty, a4);
        }

        private void ResetOverlayAnimationForShow()
        {
            LeftPanelTransform.BeginAnimation(TranslateTransform.XProperty, null);
            LeftPanelTransform.X = SlideLeftFrom;

            InfoPanelTransform.BeginAnimation(TranslateTransform.YProperty, null);
            InfoPanelTransform.Y = SlideTopFrom;

            SettingsButtonTransform.BeginAnimation(TranslateTransform.XProperty, null);
            SettingsButtonTransform.X = SlideRightFrom;

            BottomPanelTransform.BeginAnimation(TranslateTransform.YProperty, null);
            BottomPanelTransform.Y = SlideBottomFrom;
        }

        private void ResetOverlayAnimation()
        {
            LeftPanelTransform.BeginAnimation(TranslateTransform.XProperty, null);
            LeftPanelTransform.X = SlideLeftFrom;

            InfoPanelTransform.BeginAnimation(TranslateTransform.YProperty, null);
            InfoPanelTransform.Y = SlideTopFrom;

            SettingsButtonTransform.BeginAnimation(TranslateTransform.XProperty, null);
            SettingsButtonTransform.X = SlideRightFrom;

            BottomPanelTransform.BeginAnimation(TranslateTransform.YProperty, null);
            BottomPanelTransform.Y = SlideBottomFrom;
        }

        private void AnimationsCheck_Changed(object sender, RoutedEventArgs e)
        {
            _animationsEnabled = AnimationsCheck.IsChecked == true;
        }

        // ===== Поиск IP активного процесса =====

        private string? GetProcessRemoteIp(int pid)
        {
            IntPtr buffer = IntPtr.Zero;
            int bufferSize = 0;

            try
            {
                uint result = GetExtendedTcpTable(IntPtr.Zero, ref bufferSize,
                    true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);

                if (result != ERROR_INSUFFICIENT_BUFFER && result != 0)
                    return null;

                if (bufferSize == 0)
                    return null;

                buffer = Marshal.AllocHGlobal(bufferSize);

                result = GetExtendedTcpTable(buffer, ref bufferSize,
                    true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);

                if (result != 0)
                    return null;

                int numEntries = Marshal.ReadInt32(buffer);
                IntPtr rowPtr = (IntPtr)((long)buffer + 4);
                int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

                string? fallbackIp = null;

                for (int i = 0; i < numEntries; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    rowPtr = (IntPtr)((long)rowPtr + rowSize);

                    if (row.owningPid != (uint)pid) continue;
                    if (row.remoteAddr == 0) continue;

                    uint remoteAddr = row.remoteAddr;
                    if ((remoteAddr & 0xFF) == 127) continue;

                    byte[] ipBytes = new byte[4];
                    ipBytes[0] = (byte)(remoteAddr & 0xFF);
                    ipBytes[1] = (byte)((remoteAddr >> 8) & 0xFF);
                    ipBytes[2] = (byte)((remoteAddr >> 16) & 0xFF);
                    ipBytes[3] = (byte)((remoteAddr >> 24) & 0xFF);

                    string ip = $"{ipBytes[0]}.{ipBytes[1]}.{ipBytes[2]}.{ipBytes[3]}";

                    if (ipBytes[0] == 10) { if (fallbackIp == null) fallbackIp = ip; continue; }
                    if (ipBytes[0] == 192 && ipBytes[1] == 168) { if (fallbackIp == null) fallbackIp = ip; continue; }
                    if (ipBytes[0] == 172 && ipBytes[1] >= 16 && ipBytes[1] <= 31) { if (fallbackIp == null) fallbackIp = ip; continue; }

                    return ip;
                }

                return fallbackIp;
            }
            catch { return null; }
            finally
            {
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
            }
        }

        // ===== Пинг =====

        private async void PingTick()
        {
            if (_pingInProgress) return;
            _pingInProgress = true;

            try
            {
                if (_pingActiveProcess && _activePid != 0)
                {
                    if (_pingTargetPid != _activePid ||
                        (DateTime.Now - _lastTargetUpdate).TotalSeconds > 5)
                    {
                        string? newIp = GetProcessRemoteIp(_activePid);
                        if (!string.IsNullOrEmpty(newIp))
                        {
                            _pingTarget = newIp;
                            _pingTargetPid = _activePid;
                        }
                        else
                        {
                            _pingTarget = "8.8.8.8";
                            _pingTargetPid = _activePid;
                        }
                        _lastTargetUpdate = DateTime.Now;
                    }
                }
                else
                {
                    _pingTarget = "8.8.8.8";
                }

                if (_ping == null) _ping = new Ping();

                var reply = await _ping.SendPingAsync(_pingTarget, 1500);

                double value;
                if (reply.Status == IPStatus.Success)
                {
                    value = reply.RoundtripTime;
                    PingValue.Text = ((long)value).ToString();

                    if (value < 50)
                        PingValue.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
                    else if (value < 150)
                        PingValue.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
                    else
                        PingValue.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                }
                else
                {
                    value = _graphMaxPing;
                    PingValue.Text = "N/A";
                    PingValue.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
                }

                _pingHistory.Add(value);
                while (_pingHistory.Count > MaxGraphPoints)
                    _pingHistory.RemoveAt(0);

                UpdateGraph();
            }
            catch
            {
                PingValue.Text = "N/A";
                PingValue.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));

                _pingHistory.Add(_graphMaxPing);
                while (_pingHistory.Count > MaxGraphPoints)
                    _pingHistory.RemoveAt(0);

                UpdateGraph();
            }
            finally
            {
                _pingInProgress = false;
            }
        }

        // ===== График =====

        private void UpdateGraph()
        {
            PingGraph.Children.Clear();

            double w = PingGraph.ActualWidth;
            double h = PingGraph.ActualHeight;
            if (w <= 0 || h <= 0) return;
            if (_pingHistory.Count < 2) return;

            double maxVal = _graphMaxPing;
            if (maxVal <= 0) maxVal = 200;

            for (int i = 1; i <= 3; i++)
            {
                var line = new System.Windows.Shapes.Line
                {
                    X1 = 0,
                    X2 = w,
                    Y1 = h * i / 4,
                    Y2 = h * i / 4,
                    Stroke = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                    StrokeThickness = 1
                };
                PingGraph.Children.Add(line);
            }

            var polyline = new System.Windows.Shapes.Polyline
            {
                StrokeThickness = 1.8,
                StrokeLineJoin = PenLineJoin.Round,
                Stroke = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80))
            };

            if (_pingHistory.Count >= 5)
            {
                double sum = 0;
                int cnt = 0;
                for (int i = _pingHistory.Count - 5; i < _pingHistory.Count; i++)
                {
                    sum += _pingHistory[i];
                    cnt++;
                }
                double avg = sum / cnt;

                if (avg < 50)
                    polyline.Stroke = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
                else if (avg < 150)
                    polyline.Stroke = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
                else
                    polyline.Stroke = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            }

            var points = new PointCollection();
            int n = _pingHistory.Count;

            for (int i = 0; i < n; i++)
            {
                double x = i * (w / (MaxGraphPoints - 1));

                double v = _pingHistory[i];
                if (v < 0) v = 0;
                if (v > maxVal) v = maxVal;

                double y = h - (v / maxVal) * (h - 4) - 2;
                points.Add(new Point(x, y));
            }

            polyline.Points = points;
            PingGraph.Children.Add(polyline);
        }

        private void PingGraph_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateGraph();
        }

        // ===== PresentMon =====

        private void StartPresentMon()
        {
            try
            {
                string basePath = AppDomain.CurrentDomain.BaseDirectory;
                string exePath = System.IO.Path.Combine(basePath, "PresentMon.exe");

                if (!File.Exists(exePath))
                {
                    string projectRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(basePath, @"..\..\..\"));
                    string altPath = System.IO.Path.Combine(projectRoot, "PresentMon.exe");
                    if (File.Exists(altPath))
                        exePath = altPath;
                }

                if (!File.Exists(exePath))
                {
                    FpsValue.Text = "N/A";
                    return;
                }

                _presentMon = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = "--output_stdout --v1_metrics --stop_existing_session --no_console_stats",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    },
                    EnableRaisingEvents = true
                };

                _presentMon.OutputDataReceived += PresentMon_OutputDataReceived;
                _presentMon.Start();
                _presentMon.BeginOutputReadLine();
            }
            catch
            {
                FpsValue.Text = "N/A";
            }
        }

        private void PresentMon_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;

            if (_csvHeader == null)
            {
                _csvHeader = e.Data.Split(',');

                for (int i = 0; i < _csvHeader.Length; i++)
                {
                    var name = _csvHeader[i].Trim().Trim('"');

                    if (name.Equals("ProcessID", StringComparison.OrdinalIgnoreCase))
                        _pidCol = i;
                    else if (name.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase))
                        _msCol = i;
                }

                return;
            }

            if (_pidCol < 0 || _msCol < 0) return;

            var parts = e.Data.Split(',');
            if (parts.Length <= Math.Max(_pidCol, _msCol)) return;

            if (!int.TryParse(parts[_pidCol].Trim().Trim('"'), out int pid)) return;
            if (!double.TryParse(parts[_msCol].Trim().Trim('"'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double ms)) return;

            if (ms <= 0) return;

            if (!_frameTimesByPid.TryGetValue(pid, out var queue))
            {
                queue = new Queue<double>();
                _frameTimesByPid[pid] = queue;
            }

            queue.Enqueue(ms);

            while (queue.Count > 120)
                queue.Dequeue();
        }

        // ===== Активное окно / FPS =====

        private void CheckForegroundProgram()
        {
            var myHwnd = new WindowInteropHelper(this).Handle;
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == myHwnd) return;

            GetWindowThreadProcessId(fg, out uint pid);
            if (pid == 0) return;

            try
            {
                var proc = Process.GetProcessById((int)pid);
                string exeName = proc.ProcessName + ".exe";
                _activePid = (int)pid;

                string title = GetWindowTitle(fg);
                if (string.IsNullOrWhiteSpace(title))
                    title = proc.ProcessName;

                ActiveAppText.Text = title;

                try
                {
                    string? path = null;
                    try { path = proc.MainModule?.FileName; } catch { }

                    if (!string.IsNullOrEmpty(path))
                    {
                        var icon = GetIconFromExe(path!);
                        ActiveAppIcon.Source = icon;
                    }
                    else
                    {
                        ActiveAppIcon.Source = null;
                    }
                }
                catch
                {
                    ActiveAppIcon.Source = null;
                }

                double fps = 0;
                if (_frameTimesByPid.TryGetValue(_activePid, out var frameQueue) && frameQueue.Count > 3)
                {
                    double sum = 0;
                    foreach (var m in frameQueue) sum += m;
                    double avgMs = sum / frameQueue.Count;

                    if (avgMs > 0)
                        fps = 1000.0 / avgMs;
                }

                if (fps > 0.5)
                {
                    FpsValue.Text = ((int)Math.Round(fps)).ToString();

                    if (fps >= 60) FpsValue.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
                    else if (fps >= 30) FpsValue.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
                    else FpsValue.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                }
                else
                {
                    FpsValue.Text = "N/A";
                    FpsValue.Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
                }

                if (LimitProgramsCheck.IsChecked != true) return;
                if (_allowedPrograms.Count == 0) return;
                if (!IsVisible) return;

                string? fullPath = null;
                try { fullPath = proc.MainModule?.FileName; } catch { }

                bool allowed = false;
                foreach (var p in _allowedPrograms)
                {
                    if (string.Equals(System.IO.Path.GetFileName(p), exeName, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(fullPath) &&
                         string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        allowed = true;
                        break;
                    }
                }

                if (!allowed) HideWithAnimation();
            }
            catch { }
        }

        private string GetWindowTitle(IntPtr hwnd)
        {
            int length = GetWindowTextLength(hwnd);
            if (length == 0) return "";

            var sb = new System.Text.StringBuilder(length + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        // ===== Хоткей =====

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_capturingHotkey) return;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LWin || key == Key.RWin)
            {
                e.Handled = true;
                return;
            }

            if (key == Key.Escape)
            {
                _capturingHotkey = false;
                UpdateHotkeyDisplay();
                HotkeyHintText.Text = "Изменение отменено.";
                ChangeHotkeyButton.IsEnabled = true;
                e.Handled = true;
                return;
            }

            uint mods = 0;
            var modKeys = Keyboard.Modifiers;
            if ((modKeys & ModifierKeys.Control) != 0) mods |= MOD_CONTROL;
            if ((modKeys & ModifierKeys.Alt) != 0) mods |= MOD_ALT;
            if ((modKeys & ModifierKeys.Shift) != 0) mods |= MOD_SHIFT;
            if ((modKeys & ModifierKeys.Windows) != 0) mods |= MOD_WIN;

            if (mods == 0)
            {
                HotkeyHintText.Text = "Нужен хотя бы один модификатор (Ctrl/Alt/Shift/Win).";
                e.Handled = true;
                return;
            }

            var helper = new WindowInteropHelper(this);
            uint oldMods = _hotkeyModifiers;
            Key oldKey = _hotkeyKey;

            if (_hotkeyRegistered)
            {
                UnregisterHotKey(helper.Handle, HOTKEY_ID);
                _hotkeyRegistered = false;
            }

            bool ok = RegisterHotKey(helper.Handle, HOTKEY_ID,
                mods | MOD_NOREPEAT,
                (uint)KeyInterop.VirtualKeyFromKey(key));

            if (ok)
            {
                _hotkeyModifiers = mods;
                _hotkeyKey = key;
                _hotkeyRegistered = true;

                _capturingHotkey = false;
                UpdateHotkeyDisplay();
                HotkeyHintText.Text = "Горячая клавиша изменена.";
                ChangeHotkeyButton.IsEnabled = true;
            }
            else
            {
                RegisterHotKey(helper.Handle, HOTKEY_ID,
                    oldMods | MOD_NOREPEAT,
                    (uint)KeyInterop.VirtualKeyFromKey(oldKey));
                _hotkeyRegistered = true;

                HotkeyHintText.Text = "Комбинация уже занята. Попробуй другую.";
            }

            e.Handled = true;
        }

        private void UpdateHotkeyDisplay()
        {
            var parts = new List<string>();
            if ((_hotkeyModifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((_hotkeyModifiers & MOD_ALT) != 0) parts.Add("Alt");
            if ((_hotkeyModifiers & MOD_SHIFT) != 0) parts.Add("Shift");
            if ((_hotkeyModifiers & MOD_WIN) != 0) parts.Add("Win");

            string keyName = _hotkeyKey == Key.Oem3 ? "`" : _hotkeyKey.ToString();
            parts.Add(keyName);

            HotkeyDisplay.Text = string.Join("+", parts);
        }

        private void ChangeHotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            _capturingHotkey = true;
            HotkeyDisplay.Text = "Нажми комбинацию...";
            HotkeyHintText.Text = "Нажми новую комбинацию (Esc — отмена).";
            ChangeHotkeyButton.IsEnabled = false;
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                ToggleOverlay();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void ToggleOverlay()
        {
            if (IsVisible)
            {
                HideWithAnimation();
            }
            else
            {
                ResetOverlayAnimationForShow();
                Show();
                Activate();
                Topmost = true;
                Focus();
                ShowWithAnimation();
            }
        }

        // ===== Вкладки настроек =====

        private void NavTab_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;

            NavGeneral.Tag = null;
            NavHotkeys.Tag = null;
            NavPing.Tag = null;
            NavPrograms.Tag = null;
            NavAbout.Tag = null;
            btn.Tag = "active";

            TabGeneral.Visibility = Visibility.Collapsed;
            TabHotkeys.Visibility = Visibility.Collapsed;
            TabPing.Visibility = Visibility.Collapsed;
            TabPrograms.Visibility = Visibility.Collapsed;
            TabAbout.Visibility = Visibility.Collapsed;

            if (btn == NavGeneral) TabGeneral.Visibility = Visibility.Visible;
            else if (btn == NavHotkeys) TabHotkeys.Visibility = Visibility.Visible;
            else if (btn == NavPing) TabPing.Visibility = Visibility.Visible;
            else if (btn == NavPrograms) TabPrograms.Visibility = Visibility.Visible;
            else if (btn == NavAbout) TabAbout.Visibility = Visibility.Visible;
        }

        // ===== Настройки пинга =====

        private void RangePreset_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            if (double.TryParse((string)btn.Tag, out double val) && val > 0)
            {
                _graphMaxPing = val;
                RangeTextBox.Text = val.ToString();
                UpdateGraph();
            }
        }

        private void ApplyRangeButton_Click(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(RangeTextBox.Text.Trim(), out double val) && val > 0)
            {
                _graphMaxPing = val;
                UpdateGraph();
            }
            else
            {
                RangeTextBox.Text = _graphMaxPing.ToString();
            }
        }

        private void RatePreset_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            if (int.TryParse((string)btn.Tag, out int val) && val >= 100)
            {
                _pingIntervalMs = val;
                RateTextBox.Text = val.ToString();

                if (_pingTimer != null)
                    _pingTimer.Interval = TimeSpan.FromMilliseconds(_pingIntervalMs);
            }
        }

        private void ApplyRateButton_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(RateTextBox.Text.Trim(), out int val) && val >= 100)
            {
                _pingIntervalMs = val;

                if (_pingTimer != null)
                    _pingTimer.Interval = TimeSpan.FromMilliseconds(_pingIntervalMs);
            }
            else
            {
                RateTextBox.Text = _pingIntervalMs.ToString();
            }
        }

        // ===== Программы =====

        private void LimitPrograms_Changed(object sender, RoutedEventArgs e) => UpdateProgramsList();

        private void AddProgramButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Программы (*.exe)|*.exe|Все файлы (*.*)|*.*",
                Title = "Выбери программу"
            };

            if (dlg.ShowDialog() == true)
            {
                if (!_allowedPrograms.Contains(dlg.FileName))
                {
                    _allowedPrograms.Add(dlg.FileName);
                    UpdateProgramsList();
                }
            }
        }

        private void RemoveProgram_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            var path = (string)btn.Tag;
            _allowedPrograms.Remove(path);
            UpdateProgramsList();
        }

        private void UpdateProgramsList()
        {
            ProgramsList.Children.Clear();

            bool limit = LimitProgramsCheck.IsChecked == true;
            ProgramsList.Opacity = limit ? 1.0 : 0.4;
            AddProgramButton.IsEnabled = limit;

            if (_allowedPrograms.Count == 0)
            {
                ProgramsList.Children.Add(new TextBlock
                {
                    Text = "Список пуст. Нажми «Добавить программу».",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                    FontSize = 12,
                    Margin = new Thickness(10)
                });
                return;
            }

            foreach (var path in _allowedPrograms)
            {
                var row = new Grid { Height = 40, Margin = new Thickness(8, 2, 8, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var icon = GetIconFromExe(path);
                var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                if (icon != null)
                {
                    stack.Children.Add(new Image
                    {
                        Source = icon,
                        Width = 20,
                        Height = 20,
                        Margin = new Thickness(0, 0, 10, 0)
                    });
                }

                stack.Children.Add(new TextBlock
                {
                    Text = System.IO.Path.GetFileName(path),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                });

                Grid.SetColumn(stack, 0);
                row.Children.Add(stack);

                var removeBtn = new Button
                {
                    Content = "✕",
                    Width = 28,
                    Height = 28,
                    Tag = path,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                };
                removeBtn.Click += RemoveProgram_Click;

                Grid.SetColumn(removeBtn, 1);
                row.Children.Add(removeBtn);

                ProgramsList.Children.Add(row);
            }
        }

        // ===== Слоты =====

        private Button AddSlot()
        {
            var slotId = _nextSlotId++;
            var btn = new Button
            {
                Style = (Style)FindResource("SlotButtonStyle"),
                Tag = slotId,
                ToolTip = $"Слот {slotId}\n(ЛКМ — выбрать)"
            };

            btn.Content = new TextBlock
            {
                Text = "+",
                Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
                FontSize = 22
            };

            btn.Click += QuickSlot_Click;
            btn.MouseRightButtonDown += QuickSlot_RightClick;
            SlotsContainer.Children.Add(btn);
            return btn;
        }

        private void AddSlotButton_Click(object sender, RoutedEventArgs e) => AddSlot();

        private void QuickSlot_Click(object sender, RoutedEventArgs e)
        {
            var btn = (Button)sender;
            int slot = (int)btn.Tag;

            if (_quickSlots.ContainsKey(slot))
            {
                LaunchApp(_quickSlots[slot]);
            }
            else
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Программы (*.exe)|*.exe|Все файлы (*.*)|*.*",
                    Title = "Выбери приложение для слота " + slot
                };

                if (dlg.ShowDialog() == true)
                {
                    _quickSlots[slot] = dlg.FileName;
                    var name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
                    var icon = GetIconFromExe(dlg.FileName);

                    if (icon != null)
                    {
                        btn.Content = new Image
                        {
                            Source = icon,
                            Width = 32,
                            Height = 32,
                            Stretch = Stretch.Uniform
                        };
                    }
                    else
                    {
                        var sn = name.Length > 3 ? name.Substring(0, 3) : name;
                        btn.Content = new TextBlock
                        {
                            Text = sn,
                            Foreground = Brushes.White,
                            FontSize = 12,
                            FontWeight = FontWeights.SemiBold
                        };
                    }

                    btn.ToolTip = $"{name}\n(ПКМ — убрать)";
                }
            }
        }

        private void QuickSlot_RightClick(object sender, MouseButtonEventArgs e)
        {
            var btn = (Button)sender;
            int slot = (int)btn.Tag;

            if (_quickSlots.ContainsKey(slot))
            {
                _quickSlots.Remove(slot);
                btn.Content = new TextBlock
                {
                    Text = "+",
                    Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
                    FontSize = 22
                };
                btn.ToolTip = $"Слот {slot}\n(ЛКМ — выбрать)";
                e.Handled = true;
            }
        }

        private ImageSource? GetIconFromExe(string path)
        {
            IntPtr hIcon = IntPtr.Zero;
            try
            {
                hIcon = ExtractIcon(IntPtr.Zero, path, 0);
                if (hIcon == IntPtr.Zero || hIcon == (IntPtr)1) return null;

                var img = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                img.Freeze();
                return img;
            }
            catch { return null; }
            finally
            {
                if (hIcon != IntPtr.Zero && hIcon != (IntPtr)1) DestroyIcon(hIcon);
            }
        }

        private void LaunchApp(string path)
        {
            try
            {
                Topmost = false;
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });

                if (proc != null)
                {
                    _ = Task.Run(async () =>
                    {
                        for (int i = 0; i < 50; i++)
                        {
                            await Task.Delay(100);
                            try
                            {
                                proc.Refresh();
                                if (proc.MainWindowHandle != IntPtr.Zero)
                                {
                                    Dispatcher.Invoke(() =>
                                    {
                                        ShowWindow(proc.MainWindowHandle, SW_RESTORE);
                                        SetForegroundWindow(proc.MainWindowHandle);
                                    });
                                    break;
                                }
                            }
                            catch { break; }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось запустить:\n" + ex.Message, "GameOverlay");
                Topmost = true;
            }
        }

        // ===== Браузер =====

        private async void BrowserButton_Click(object sender, RoutedEventArgs e)
        {
            BrowserPanel.Visibility = Visibility.Visible;

            if (!_browserInitialized)
            {
                try
                {
                    await Browser.EnsureCoreWebView2Async(null);
                    _browserInitialized = true;

                    Browser.CoreWebView2.SourceChanged += (s, ev) =>
                    {
                        UrlBox.Text = Browser.Source?.ToString() ?? "";
                    };

                    Browser.CoreWebView2.Navigate("https://www.google.com");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Не удалось запустить WebView2:\n" + ex.Message,
                                    "GameOverlay", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void CloseBrowserButton_Click(object sender, RoutedEventArgs e) => BrowserPanel.Visibility = Visibility.Collapsed;

        private void Navigate()
        {
            if (Browser.CoreWebView2 == null) return;
            var url = UrlBox.Text.Trim();
            if (string.IsNullOrEmpty(url)) return;

            if (!url.Contains(".") || url.Contains(" "))
                url = "https://www.google.com/search?q=" + Uri.EscapeDataString(url);
            else if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                url = "https://" + url;

            try { Browser.CoreWebView2.Navigate(url); }
            catch (Exception ex) { MessageBox.Show("Ошибка перехода:\n" + ex.Message, "GameOverlay"); }
        }

        private void GoButton_Click(object sender, RoutedEventArgs e) => Navigate();

        private void UrlBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Navigate();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Browser.CoreWebView2?.CanGoBack == true)
                Browser.CoreWebView2.GoBack();
        }

        private void ForwardButton_Click(object sender, RoutedEventArgs e)
        {
            if (Browser.CoreWebView2?.CanGoForward == true)
                Browser.CoreWebView2.GoForward();
        }

        private void BrowserHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingBrowser = true;
            _browserStartScreen = PointToScreen(e.GetPosition(this));
            _browserStartX = BrowserTransform.X;
            _browserStartY = BrowserTransform.Y;
            BrowserHeader.CaptureMouse();
        }

        private void BrowserHeader_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingBrowser) return;
            var current = PointToScreen(e.GetPosition(this));
            BrowserTransform.X = _browserStartX + (current.X - _browserStartScreen.X);
            BrowserTransform.Y = _browserStartY + (current.Y - _browserStartScreen.Y);
        }

        private void BrowserHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingBrowser = false;
            BrowserHeader.ReleaseMouseCapture();
        }

        // ===== Настройки =====

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsPanel.Visibility = Visibility.Visible;
            UpdateProgramsList();
        }

        private void CloseSettingsButton_Click(object sender, RoutedEventArgs e) => SettingsPanel.Visibility = Visibility.Collapsed;

        private void SettingsHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSettings = true;
            _settingsStartScreen = PointToScreen(e.GetPosition(this));
            _settingsStartX = SettingsTransform.X;
            _settingsStartY = SettingsTransform.Y;
            SettingsHeader.CaptureMouse();
        }

        private void SettingsHeader_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingSettings) return;
            var current = PointToScreen(e.GetPosition(this));
            SettingsTransform.X = _settingsStartX + (current.X - _settingsStartScreen.X);
            SettingsTransform.Y = _settingsStartY + (current.Y - _settingsStartScreen.Y);
        }

        private void SettingsHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDraggingSettings = false;
            SettingsHeader.ReleaseMouseCapture();
        }

        protected override void OnClosed(EventArgs e)
        {
            _programCheckTimer?.Stop();
            _pingTimer?.Stop();

            try
            {
                if (_presentMon != null && !_presentMon.HasExited)
                {
                    _presentMon.Kill();
                    _presentMon.Dispose();
                }
            }
            catch { }

            var helper = new WindowInteropHelper(this);
            if (_hotkeyRegistered)
                UnregisterHotKey(helper.Handle, HOTKEY_ID);

            Browser?.Dispose();
            base.OnClosed(e);
        }
    }
}