using System;
using System.Collections.Generic;

namespace ChatP2P.Server
{
    public static class LocalDb
    {
        private static readonly Dictionary<string, string> _peerDtlsFingerprints = new(StringComparer.OrdinalIgnoreCase);

        public static void SetPeerDtlsFp(string peer, string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(peer) || string.IsNullOrWhiteSpace(fingerprint))
                return;
                
            _peerDtlsFingerprints[peer] = fingerprint;
            Console.WriteLine($"DTLS fingerprint stored for {peer}: {fingerprint}");
        }

        public static string? GetPeerDtlsFp(string peer)
        {
            if (string.IsNullOrWhiteSpace(peer))
                return null;
                
            _peerDtlsFingerprints.TryGetValue(peer, out var fingerprint);
            return fingerprint;
        }

        public static void ClearPeerDtlsFp(string peer)
        {
            if (string.IsNullOrWhiteSpace(peer))
                return;
                
            _peerDtlsFingerprints.Remove(peer);
        }

        public static Dictionary<string, string> GetAllDtlsFingerprints()
        {
            return new Dictionary<string, string>(_peerDtlsFingerprints);
        }
    }
}