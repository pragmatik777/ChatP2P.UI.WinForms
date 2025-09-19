using System.Collections.Generic;

namespace ChatP2P.SecurityTester.Core
{
    /// <summary>
    /// 📋 Configuration GLOBALE du Security Tester (DEFAULTS & AUTO-POPULATE)
    ///
    /// Cette classe fournit les valeurs par défaut qui auto-remplissent l'interface utilisateur.
    /// Les champs Target Configuration (en haut) et Port Forwarding sont synchronisés automatiquement.
    ///
    /// Usage:
    /// 1. Modifier les defaults ici pour vos tests récurrents
    /// 2. Cliquer "🎯 Update" synchronise tous les champs UI
    /// 3. Plus besoin de re-taper les mêmes IPs à chaque fois
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

        // 🌐 Network Configuration (DEFAULTS - Auto-populate UI fields)
        public static string TargetClientIP { get; set; } = "192.168.1.147";    // ← Victim to attack (VM)
        public static string RelayServerIP { get; set; } = "192.168.1.152";     // ← Local ChatP2P relay server
        public static string Gateway_IP { get; set; } = "192.168.1.1";          // ← Network gateway
        public static string AttackerIP { get; set; } = "192.168.1.102";        // ← This machine IP

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

        // 🌐 Network Interface Selection (Persistence)
        public static string PreferredNetworkInterface { get; set; } = "Microsoft Hyper-V Network Adapter #2";
    }
}