using System.Collections.Generic;

namespace ChatP2P.SecurityTester.Core
{
    /// <summary>
    /// Configuration globale du Security Tester
    /// </summary>
    public static class SecurityTesterConfig
    {
        // 🎯 ChatP2P Protocol Ports
        public static readonly List<int> ChatP2PPorts = new()
        {
            7777, // Friend Requests
            8888, // Messages Chat
            8889, // API Commands
            8891  // File Transfers
        };

        // 🌐 Network Configuration
        public static string TargetClientIP { get; set; } = "192.168.1.100";
        public static string RelayServerIP { get; set; } = "relay.chatp2p.com";
        public static string Gateway_IP { get; set; } = "192.168.1.1";
        public static string AttackerIP { get; set; } = "192.168.1.102";

        // 🕷️ Attack Configuration
        public static bool EnableRealTimeCapture { get; set; } = true;
        public static bool EnableARPSpoofing { get; set; } = false;
        public static bool EnableKeySubstitution { get; set; } = false;
        public static int MaxCapturedPackets { get; set; } = 10000;

        // 🔐 Crypto Attack Settings
        public static bool LogCryptographicOperations { get; set; } = true;
        public static bool SaveCapturedKeys { get; set; } = true;
        public static string AttackResultsPath { get; set; } = @"C:\Users\pragm\OneDrive\Bureau\SecurityTester_Results";

        // 🛡️ Safety Limits (éviter DoS accidentel)
        public static int MaxARPPacketsPerSecond { get; set; } = 10;
        public static int MaxAttackDurationMinutes { get; set; } = 30;
    }
}