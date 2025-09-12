using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ChatP2P.Server
{
    public class P2PService
    {
        private static readonly Dictionary<string, bool> _peerConnections = new();
        private static string _localDisplayName = "";
        private static Func<string, string, Task> _signalSender = null!;
        private static bool _initialized = false;

        public static void Initialize(string displayName, Func<string, string, Task> signalSender)
        {
            if (_initialized) return;

            _localDisplayName = displayName;
            _signalSender = signalSender;

            // Initialize P2PManager C# version
            P2PManager.Init(_signalSender, _localDisplayName);

            // Hook P2P events
            P2PManager.OnP2PState += OnP2PStateChanged;
            P2PManager.OnP2PText += OnP2PTextReceived;
            P2PManager.OnP2PBinary += OnP2PBinaryReceived;
            P2PManager.OnLog += OnP2PLog;

            _initialized = true;
            Console.WriteLine($"P2P Service initialized for: {displayName}");
        }

        public static async Task<bool> StartP2PConnection(string peer)
        {
            try
            {
                if (!_initialized)
                {
                    Console.WriteLine("P2P Service not initialized");
                    return false;
                }

                var stunUrls = new string[] { "stun:stun.l.google.com:19302" };
                P2PManager.StartP2P(peer, stunUrls);
                
                Console.WriteLine($"P2P connection started for peer: {peer}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting P2P connection: {ex.Message}");
                return false;
            }
        }

        public static bool SendTextMessage(string peer, string message)
        {
            try
            {
                if (!_initialized) return false;
                return P2PManager.TrySendText(peer, message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending text to {peer}: {ex.Message}");
                return false;
            }
        }

        public static bool SendBinaryData(string peer, byte[] data)
        {
            try
            {
                if (!_initialized) return false;
                return P2PManager.TrySendBinary(peer, data);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending binary data to {peer}: {ex.Message}");
                return false;
            }
        }

        public static bool IsConnected(string peer)
        {
            try
            {
                if (!_initialized) return false;
                return P2PManager.IsP2PConnected(peer);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking connection for {peer}: {ex.Message}");
                return false;
            }
        }

        public static void HandleOffer(string fromPeer, string sdp)
        {
            try
            {
                if (!_initialized) return;
                var stunUrls = new string[] { "stun:stun.l.google.com:19302" };
                P2PManager.HandleOffer(fromPeer, sdp, stunUrls);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling offer from {fromPeer}: {ex.Message}");
            }
        }

        public static void HandleAnswer(string fromPeer, string sdp)
        {
            try
            {
                if (!_initialized) return;
                P2PManager.HandleAnswer(fromPeer, sdp);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling answer from {fromPeer}: {ex.Message}");
            }
        }

        public static void HandleCandidate(string fromPeer, string candidate)
        {
            try
            {
                if (!_initialized) return;
                P2PManager.HandleCandidate(fromPeer, candidate);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling candidate from {fromPeer}: {ex.Message}");
            }
        }

        public static Dictionary<string, bool> GetConnectedPeers()
        {
            return new Dictionary<string, bool>(_peerConnections);
        }

        public static Dictionary<string, object> GetStats()
        {
            var stats = new Dictionary<string, object>
            {
                ["total_peers"] = _peerConnections.Count,
                ["connected_peers"] = _peerConnections.Values.Count(connected => connected),
                ["local_name"] = _localDisplayName,
                ["initialized"] = _initialized
            };
            return stats;
        }

        // Event handlers
        private static void OnP2PStateChanged(string peer, bool connected)
        {
            _peerConnections[peer] = connected;
            Console.WriteLine($"P2P State: {peer} = {(connected ? "connected" : "disconnected")}");
        }

        private static void OnP2PTextReceived(string peer, string text)
        {
            Console.WriteLine($"P2P Text from {peer}: {text}");
            // TODO: Forward to message handlers
        }

        private static void OnP2PBinaryReceived(string peer, byte[] data)
        {
            Console.WriteLine($"P2P Binary from {peer}: {data.Length} bytes");
            // TODO: Forward to file transfer handlers
        }

        private static void OnP2PLog(string peer, string line)
        {
            Console.WriteLine($"P2P Log [{peer}]: {line}");
        }
    }
}