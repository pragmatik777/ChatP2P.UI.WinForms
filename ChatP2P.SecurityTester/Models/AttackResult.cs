using System;

namespace ChatP2P.SecurityTester.Models
{
    /// <summary>
    /// Résultat d'une tentative d'attaque
    /// </summary>
    public class AttackResult
    {
        public bool Success { get; set; }
        public string AttackType { get; set; } = "";
        public string Description { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string TargetPeer { get; set; } = "";
        public string Details { get; set; } = "";
        public byte[]? CapturedData { get; set; }
        public string ErrorMessage { get; set; } = "";

        public string Status => Success ? "✅ SUCCESS" : "❌ FAILED";
        public string Summary => $"[{Timestamp:HH:mm:ss}] {Status} {AttackType} → {TargetPeer}";
    }

    public enum AttackType
    {
        KeySubstitution,
        ARPSpoofing,
        FriendRequestInjection,
        MessageInterception,
        RelayHijacking,
        TOFUBypass
    }
}