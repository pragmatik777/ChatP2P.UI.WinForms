using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChatP2P.SecurityTester.Models;
using ChatP2P.SecurityTester.Core;
using System.Text;
using SharpPcap;
using SharpPcap.LibPcap;
using PacketDotNet;
using System.Linq;
using System.Net;

namespace ChatP2P.SecurityTester.Network
{
    /// <summary>
    /// 🕷️ Module de capture RÉELLE du trafic VICTIME intercepté (SharpPcap)
    /// </summary>
    public class PacketCapture
    {
        private bool _isCapturing = false;
        private ICaptureDevice? _captureDevice;
        private int _packetCount = 0;

        public event Action<CapturedPacket>? PacketCaptured;
        public event Action<string>? LogMessage;

        public async Task<bool> StartCapture(string interfaceName = "")
        {
            try
            {
                LogMessage?.Invoke($"🕷️ DÉMARRAGE CAPTURE TRAFIC VICTIME");
                LogMessage?.Invoke($"📡 Interface demandée: {interfaceName}");
                LogMessage?.Invoke($"🎯 Target: {SecurityTesterConfig.TargetClientIP}");
                LogMessage?.Invoke($"⚠️ ATTENTION: Capture RÉELLE du trafic réseau");

                // Obtenir toutes les interfaces réseau
                var devices = CaptureDeviceList.Instance;
                LogMessage?.Invoke($"🔍 {devices.Count} interfaces réseau détectées:");

                if (devices.Count == 0)
                {
                    LogMessage?.Invoke("❌ Aucune interface réseau trouvée!");
                    return false;
                }

                // Lister toutes les interfaces disponibles pour diagnostic
                for (int i = 0; i < devices.Count; i++)
                {
                    var device = devices[i];
                    LogMessage?.Invoke($"  [{i}] {device.Description} - {device.Name}");
                }

                // Sélectionner l'interface (première active si pas spécifiée)
                _captureDevice = devices.FirstOrDefault(d =>
                    string.IsNullOrEmpty(interfaceName) || d.Description.Contains(interfaceName))
                    ?? devices.FirstOrDefault();

                if (_captureDevice == null)
                {
                    LogMessage?.Invoke("❌ Interface réseau non trouvée!");
                    return false;
                }

                LogMessage?.Invoke($"✅ Interface sélectionnée: {_captureDevice.Description}");
                LogMessage?.Invoke($"📍 Interface Name: {_captureDevice.Name}");

                // Configuration de l'interface
                _captureDevice.OnPacketArrival += OnPacketArrival;

                // Essayer plusieurs modes si promiscuous échoue
                try
                {
                    _captureDevice.Open(DeviceModes.Promiscuous, 1000);
                    LogMessage?.Invoke("🔓 Mode promiscuous activé");
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"⚠️ Mode promiscuous échoué: {ex.Message}");
                    LogMessage?.Invoke("🔄 Tentative mode par défaut...");
                    _captureDevice.Open(DeviceModes.None, 1000);
                    LogMessage?.Invoke("✅ Mode par défaut activé");
                }

                // FILTRE PLUS LARGE pour capturer plus de trafic
                // Suppression du filtre restrictif pour voir tout d'abord
                LogMessage?.Invoke("🔍 Application filtre large pour diagnostic...");

                // Pas de filtre au début pour voir si on capture quelque chose
                // _captureDevice.Filter = "";  // Commenté pour tester sans filtre

                LogMessage?.Invoke("📡 AUCUN FILTRE - Capture tout le trafic pour diagnostic");

                _isCapturing = true;
                _packetCount = 0;

                // Démarrer la capture
                _captureDevice.StartCapture();

                LogMessage?.Invoke("🚀 CAPTURE DÉMARRÉE - Monitoring ALL trafic pour diagnostic!");
                LogMessage?.Invoke("📊 Si aucun packet n'apparaît, problème d'interface ou drivers");
                LogMessage?.Invoke("🔧 Test: Ouvrez un navigateur sur cette machine pour générer du trafic");

                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur démarrage capture: {ex.Message}");
                LogMessage?.Invoke($"🔍 Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// 🕷️ HANDLER PRINCIPAL - Traite chaque packet intercepté et l'envoie au GridView
        /// </summary>
        private void OnPacketArrival(object sender, SharpPcap.PacketCapture e)
        {
            try
            {
                _packetCount++;

                // Log chaque 10 packets pour voir si on capture
                if (_packetCount % 10 == 1)
                {
                    LogMessage?.Invoke($"📦 Packet #{_packetCount} reçu - Capture fonctionnelle!");
                }

                // Limiter le nombre de packets pour éviter overflow
                if (_packetCount > 1000)
                {
                    LogMessage?.Invoke("⚠️ Limite de 1000 packets atteinte - arrêt auto");
                    StopCapture();
                    return;
                }

                var rawPacket = e.GetPacket();
                var packet = Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);

                var capturedPacket = AnalyzePacket(packet, rawPacket.Timeval.Date);

                if (capturedPacket != null)
                {
                    // 🎯 ENVOYER AU GRIDVIEW via event
                    LogMessage?.Invoke($"✅ Packet analysé: {capturedPacket.Summary}");
                    PacketCaptured?.Invoke(capturedPacket);
                }
                else
                {
                    // Log packets ignorés pour diagnostic
                    if (_packetCount <= 20) // Seulement premiers packets pour éviter spam
                    {
                        LogMessage?.Invoke($"⚪ Packet #{_packetCount} ignoré par filtre");
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur analyse packet: {ex.Message}");
                LogMessage?.Invoke($"🔍 Stack: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// 🔍 ANALYSE COMPLÈTE du packet pour extraction des infos importantes
        /// </summary>
        private CapturedPacket? AnalyzePacket(Packet packet, DateTime timestamp)
        {
            try
            {
                var ethernetPacket = packet.Extract<EthernetPacket>();
                if (ethernetPacket == null) return null;

                var ipPacket = ethernetPacket.Extract<IPPacket>();
                if (ipPacket == null) return null;

                string sourceIP = ipPacket.SourceAddress.ToString();
                string destIP = ipPacket.DestinationAddress.ToString();
                string protocol = ipPacket.Protocol.ToString();
                int size = packet.Bytes.Length;
                string content = "";
                PacketType packetType = PacketType.Unknown;
                int sourcePort = 0;
                int destPort = 0;

                // 🔍 ANALYSE TCP
                var tcpPacket = ipPacket.Extract<TcpPacket>();
                if (tcpPacket != null)
                {
                    protocol = "TCP";
                    sourcePort = tcpPacket.SourcePort;
                    destPort = tcpPacket.DestinationPort;

                    // Identifier le type de trafic
                    if (sourcePort == 80 || destPort == 80)
                        packetType = PacketType.Unknown; // HTTP
                    else if (sourcePort == 443 || destPort == 443)
                        packetType = PacketType.Unknown; // HTTPS
                    else if (sourcePort == 8889 || destPort == 8889)
                        packetType = PacketType.StatusSync; // ChatP2P API
                    else if (sourcePort == 7777 || destPort == 7777)
                        packetType = PacketType.FriendRequest; // ChatP2P Friend
                    else if (sourcePort == 8888 || destPort == 8888)
                        packetType = PacketType.ChatMessage; // ChatP2P Chat
                    else
                        packetType = PacketType.Unknown;

                    // Extraire contenu si possible
                    if (tcpPacket.PayloadData != null && tcpPacket.PayloadData.Length > 0)
                    {
                        var payload = tcpPacket.PayloadData;
                        content = ExtractReadableContent(payload, packetType.ToString());
                    }
                }

                // 🔍 ANALYSE UDP
                var udpPacket = ipPacket.Extract<UdpPacket>();
                if (udpPacket != null)
                {
                    protocol = "UDP";
                    sourcePort = udpPacket.SourcePort;
                    destPort = udpPacket.DestinationPort;

                    if (sourcePort == 53 || destPort == 53)
                        packetType = PacketType.Unknown; // DNS
                    else
                        packetType = PacketType.Unknown;

                    if (udpPacket.PayloadData != null && udpPacket.PayloadData.Length > 0)
                    {
                        content = ExtractReadableContent(udpPacket.PayloadData, packetType.ToString());
                    }
                }

                // 🎯 FILTRER seulement le trafic intéressant
                if (!IsInterestingTraffic(sourceIP, destIP, packetType.ToString()))
                    return null;

                return new CapturedPacket
                {
                    Timestamp = timestamp,
                    SourceIP = sourceIP,
                    DestinationIP = destIP,
                    SourcePort = sourcePort,
                    DestinationPort = destPort,
                    Protocol = protocol,
                    Type = packetType,
                    Size = size,
                    ParsedContent = content.Length > 100 ? content.Substring(0, 100) + "..." : content,
                    RawData = packet.Bytes
                };
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur analyse packet détaillée: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 🔍 Extrait le contenu lisible des packets (HTTP, DNS, ChatP2P)
        /// </summary>
        private string ExtractReadableContent(byte[] payload, string type)
        {
            try
            {
                var text = Encoding.UTF8.GetString(payload);

                // Filtrer seulement les caractères imprimables
                var readable = new string(text.Where(c => c >= 32 && c <= 126).ToArray());

                if (string.IsNullOrWhiteSpace(readable) || readable.Length < 3)
                    return $"[Binary {payload.Length} bytes]";

                // Rechercher des patterns intéressants
                if (type == "HTTP" && readable.Contains("GET /"))
                    return ExtractHttpRequest(readable);
                else if (type == "DNS")
                    return ExtractDnsQuery(readable);
                else if (type.Contains("ChatP2P"))
                    return ExtractChatP2PContent(readable);
                else
                    return readable.Length > 50 ? readable.Substring(0, 50) + "..." : readable;
            }
            catch
            {
                return $"[Binary {payload.Length} bytes]";
            }
        }

        private string ExtractHttpRequest(string content)
        {
            var lines = content.Split('\n');
            var firstLine = lines.FirstOrDefault(l => l.Contains("GET ") || l.Contains("POST "));
            var hostLine = lines.FirstOrDefault(l => l.StartsWith("Host:"));

            if (firstLine != null && hostLine != null)
                return $"{firstLine.Trim()} - {hostLine.Trim()}";
            else if (firstLine != null)
                return firstLine.Trim();
            else
                return content.Length > 50 ? content.Substring(0, 50) + "..." : content;
        }

        private string ExtractDnsQuery(string content)
        {
            // DNS queries sont binaires, mais on peut essayer d'extraire le domain
            foreach (var word in content.Split('\0', ' ', '\n', '\r'))
            {
                if (word.Length > 3 && word.Contains('.') && !word.Contains('\x01'))
                {
                    return $"Query: {word}";
                }
            }
            return "[DNS Query]";
        }

        private string ExtractChatP2PContent(string content)
        {
            // Chercher les patterns ChatP2P
            if (content.Contains("FRIEND_REQ"))
                return "🤝 Friend Request";
            else if (content.Contains("CHAT_MSG"))
                return "💬 Chat Message";
            else if (content.Contains("FILE_"))
                return "📁 File Transfer";
            else
                return content.Length > 30 ? content.Substring(0, 30) + "..." : content;
        }

        /// <summary>
        /// 🎯 Filtre pour ne garder que le trafic intéressant
        /// </summary>
        private bool IsInterestingTraffic(string sourceIP, string destIP, string type)
        {
            var targetIP = SecurityTesterConfig.TargetClientIP;

            // MODE DIAGNOSTIC: Capturer TOUT pour voir si on reçoit des packets
            // Commenter cette ligne pour revenir au filtrage normal
            return true; // ⚠️ DIAGNOSTIC MODE - CAPTURE TOUT

            // FILTRAGE NORMAL (désactivé temporairement pour diagnostic)
            /*
            // Garder tout ce qui implique la cible
            if (sourceIP.Contains(targetIP) || destIP.Contains(targetIP))
                return true;

            // Garder les ports ChatP2P
            if (type.Contains("ChatP2P") || type.Contains("8889") || type.Contains("7777") || type.Contains("8888"))
                return true;

            // Garder HTTP/HTTPS/DNS intéressant
            if (type == "HTTP" || type == "HTTPS" || type == "DNS")
                return true;

            // Ignorer le reste
            return false;
            */
        }


        public void StopCapture()
        {
            try
            {
                _isCapturing = false;

                if (_captureDevice != null)
                {
                    _captureDevice.StopCapture();
                    _captureDevice.Close();
                    _captureDevice = null;
                }

                LogMessage?.Invoke($"⏹️ CAPTURE ARRÊTÉE - {_packetCount} packets interceptés");
                LogMessage?.Invoke("📊 Analyse du trafic victime terminée");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur arrêt capture: {ex.Message}");
            }
        }

        public List<string> GetAvailableInterfaces()
        {
            try
            {
                var interfaces = new List<string>();
                var devices = CaptureDeviceList.Instance;

                foreach (var device in devices)
                {
                    if (device.Description != null)
                    {
                        interfaces.Add(device.Description);
                    }
                }

                // Fallback si aucune interface trouvée
                if (interfaces.Count == 0)
                {
                    interfaces.AddRange(new[]
                    {
                        "Ethernet - Network Adapter",
                        "Wi-Fi - Wireless Adapter",
                        "Loopback - Microsoft Loopback Adapter"
                    });
                }

                return interfaces;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ Erreur enumération interfaces: {ex.Message}");
                return new List<string> { "Default Interface" };
            }
        }

        public bool IsCapturing => _isCapturing;
    }
}