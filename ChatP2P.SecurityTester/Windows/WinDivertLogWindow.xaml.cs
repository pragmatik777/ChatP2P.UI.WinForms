using System;
using System.Windows;
using System.Windows.Threading;
using ChatP2P.SecurityTester.Network;

namespace ChatP2P.SecurityTester.Windows
{
    public partial class WinDivertLogWindow : Window
    {
        private WinDivertInterceptor_Fixed? _interceptor;
        private int _packetCount = 0;
        private int _interceptedCount = 0;
        private string _relayServerIP = "";
        private string _attackerIP = "";
        private string _victimIP = "";

        public WinDivertLogWindow()
        {
            InitializeComponent();
            AddLog("🕷️ WinDivert Packet Interceptor initialized");
            AddLog("⚠️  Requires Administrator privileges to function");
            AddLog("📡 Ready to intercept TCP packets...");
        }

        public void SetConfiguration(string relayServerIP, string attackerIP, string victimIP = "")
        {
            _relayServerIP = relayServerIP;
            _attackerIP = attackerIP;
            _victimIP = victimIP;
            AddLog($"🎯 Configuration set:");
            AddLog($"   Relay Server: {relayServerIP}");
            AddLog($"   Attacker IP: {attackerIP}");
            if (!string.IsNullOrEmpty(victimIP))
                AddLog($"   Victim IP: {victimIP}");
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            StartInterception();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopInterception();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            LogTextBox.Clear();
            _packetCount = 0;
            _interceptedCount = 0;
            UpdateCounters();
            AddLog("🧹 Logs cleared");
        }

        public async void StartInterception()
        {
            if (string.IsNullOrEmpty(_relayServerIP) || string.IsNullOrEmpty(_attackerIP))
            {
                AddLog("❌ Configuration missing - set relay server and attacker IP first");
                return;
            }

            try
            {
                AddLog("🚀 Starting WinDivert interception...");

                _interceptor = new WinDivertInterceptor_Fixed(_relayServerIP, _attackerIP, _victimIP);
                _interceptor.LogMessage += OnLogMessage;
                _interceptor.PacketIntercepted += OnPacketIntercepted;

                var started = await _interceptor.StartInterception();

                if (started)
                {
                    AddLog("✅ WinDivert interception ACTIVE");
                    UpdateStatus("Running", "#4ecdc4");
                    StartButton.IsEnabled = false;
                    StopButton.IsEnabled = true;
                }
                else
                {
                    AddLog("❌ Failed to start WinDivert interception");
                    AddLog("   Possible causes:");
                    AddLog("   - Not running as Administrator");
                    AddLog("   - WinDivert driver not installed");
                    AddLog("   - Missing WinDivert.dll or .sys files");
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ Exception starting interception: {ex.Message}");
                AddLog($"   Stack trace: {ex.StackTrace}");
            }
        }

        public void StopInterception()
        {
            try
            {
                AddLog("⏹️ Stopping WinDivert interception...");

                _interceptor?.StopInterception();
                _interceptor = null;

                UpdateStatus("Stopped", "#ff6b6b");
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = false;

                AddLog("✅ WinDivert interception stopped");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Exception stopping interception: {ex.Message}");
            }
        }

        private void OnLogMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                AddLog($"[WinDivert] {message}");
            });
        }

        private void OnPacketIntercepted(string description, byte[] packet)
        {
            Dispatcher.Invoke(() =>
            {
                _packetCount++;
                _interceptedCount++;
                UpdateCounters();

                AddLog($"📦 PACKET INTERCEPTED: {description}");
                AddLog($"   Size: {packet.Length} bytes");

                // Hex dump for debugging
                if (packet.Length > 0)
                {
                    var hexDump = BitConverter.ToString(packet, 0, Math.Min(32, packet.Length));
                    AddLog($"   Hex: {hexDump}...");
                }

                // Try to parse IP header for debugging
                if (packet.Length >= 20)
                {
                    try
                    {
                        var version = (packet[0] >> 4) & 0xF;
                        var protocol = packet[9];
                        var srcIP = $"{packet[12]}.{packet[13]}.{packet[14]}.{packet[15]}";
                        var dstIP = $"{packet[16]}.{packet[17]}.{packet[18]}.{packet[19]}";

                        AddLog($"   IP: v{version}, Protocol: {protocol}, {srcIP} → {dstIP}");
                    }
                    catch (Exception ex)
                    {
                        AddLog($"   Parse error: {ex.Message}");
                    }
                }
            });
        }

        private void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var logEntry = $"[{timestamp}] {message}\r\n";

            LogTextBox.AppendText(logEntry);

            if (AutoScrollCheckbox.IsChecked == true)
            {
                LogScrollViewer.ScrollToEnd();
            }
        }

        private void UpdateStatus(string status, string color)
        {
            StatusText.Text = status;
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        }

        private void UpdateCounters()
        {
            PacketCountText.Text = _packetCount.ToString();
            InterceptedCountText.Text = _interceptedCount.ToString();
        }

        protected override void OnClosed(EventArgs e)
        {
            StopInterception();
            base.OnClosed(e);
        }
    }
}