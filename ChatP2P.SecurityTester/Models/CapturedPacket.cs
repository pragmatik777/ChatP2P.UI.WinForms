using System;

namespace ChatP2P.SecurityTester.Models
{
    /// <summary>
    /// Représente un packet réseau capturé pour analyse
    /// </summary>
    public class CapturedPacket
    {
        public DateTime Timestamp { get; set; }
        public string SourceIP { get; set; } = "";
        public string DestinationIP { get; set; } = "";
        public int SourcePort { get; set; }
        public int DestinationPort { get; set; }
        public string Protocol { get; set; } = "";
        public byte[] RawData { get; set; } = Array.Empty<byte>();
        public string ParsedContent { get; set; } = "";
        public PacketType Type { get; set; }
        public int Size { get; set; }

        public string Summary => $"{Timestamp:HH:mm:ss.fff} {Protocol} {SourceIP}:{SourcePort} → {DestinationIP}:{DestinationPort} ({Size} bytes)";
    }

    public enum PacketType
    {
        Unknown,
        FriendRequest,
        FriendAccept,
        KeyExchange,
        ChatMessage,
        FileTransfer,
        ICESignaling,
        StatusSync
    }
}