# 📋 Claude Code Session Guide - ChatP2P

## 🏗️ **ARCHITECTURE P2P DÉCENTRALISÉE (C# PUR)**

**ChatP2P.Server** : Console C# - Pure signaling relay (TCP localhost:8889)
**ChatP2P.Client** : WPF C# - WebRTC DataChannels directs P2P
**ChatP2P.Crypto** : VB.NET legacy - Post-Quantum Crypto (gardé)

## 🚀 Build Commands
```bash
dotnet build ChatP2P.UI.WinForms.sln --configuration Debug
dotnet clean ChatP2P.UI.WinForms.sln
```

## 📁 Solution Structure
```
ChatP2P.UI.WinForms.sln
├── ChatP2P.Server.csproj     (C# pure relay)
├── ChatP2P.Client.csproj     (C# WebRTC client)
└── ChatP2P.Crypto.vbproj     (VB.NET crypto)

## ⚡ **SYSTÈME OPÉRATIONNEL (Sept 2025)**

### ✅ **P2P WebRTC Fonctionnel**
- Messages/fichiers P2P directs via WebRTC DataChannels
- ICE signaling: offer/answer/candidates avec anti-spam protection
- Synchronisation bidirectionnelle VM1↔VM2
- Progress bars synchronisées + UI reception

### 🔧 **Architecture**
```
VM1-Client ←── WebRTC DataChannels ──→ VM2-Client
     ↓                                     ↓
   Server Relay (ICE signaling only)
```

### 🛡️ **Sécurité**
- TOFU Trust management + Post-Quantum Crypto
- Database SQLite `%APPDATA%\ChatP2P\`
- Logs: `Desktop\ChatP2P_Logs\`

### 📊 **Configuration Tri-Canal**
- **Ports**: 7777 (friends), 8888 (chat), 8891 (files), 8889 (API), WebRTC P2P
- **ICE Servers**: Google STUN + Cloudflare backup
- **API**: `SendApiRequest("p2p", "action", data)`

## 🔧 **FIXES TECHNIQUES APPLIQUÉS**

### ✅ **Fixes Critiques SIPSorcery**
- **createDataChannel()** : `.Result` pour Task<RTCDataChannel>
- **Variable scope** : Fix polling backup dans async context
- **JSON deserialization** : Direct JsonElement usage vs re-serialization
- **WebRTC_Binary** : SIPSorcery envoie strings comme binary

### ✅ **Fixes P2P Core**
- **SendWebRTCSignal** : API relay pour offers/answers (était TODO)
- **Double JSON encoding** : Simple encoding ICE candidates
- **Progress bars** : FileTransferProgress avec filename
- **Header parsing** : FILENAME:nom.ext| format simple

### ✅ **Architecture Canal Séparé Fichiers (Sept 2025)**
- **Port 8891** : Canal dédié fichiers TCP relay (évite saturation chat)
- **Format PRIV** : `PRIV:fromPeer:toPeer:FILE_CHUNK_RELAY:...`
- **Optimisations** : 1MB chunks TCP, logs réduits (évite 5GB logs)
- **UX** : Suppression MessageBox confirmation fin transfert

### ✅ **Optimisations Performance**
- **Buffer thresholds** : 1MB/256KB (16x plus agressif)
- **Chunk size** : 64KB (4x plus gros)
- **Burst control** : 5 chunks + micro-pauses adaptatives
- **Flow control** : 10ms polling (10x plus rapide)

## 📚 **RÉFÉRENCES PERFORMANCE WebRTC 2025**

**Sources recherche utilisées** :
- SIPSorcery GitHub : Examples DataChannel + flow control
- Mozilla MDN WebRTC 2025 : bufferedAmount/bufferedAmountLowThreshold
- Stack Overflow : High bandwidth applications + chunk optimization
- W3C WebRTC-PC : Buffer capacity + flow control mechanisms

**Best Practices identifiées** :
- Chunk Size : 64KB limite compatible Firefox/Chrome
- Buffer Management : 1MB standard, 4MB pour ultra-performance
- Flow Control : 10ms polling max, burst control recommandé
- SIPSorcery : bufferedAmountLowThreshold critique performance

**STATUS FINAL** : ✅ P2P WebRTC + Canal Séparé Fichiers 100% opérationnels

## 🎯 **RÉSUMÉ ARCHITECTURE FINALE (15 Sept 2025)**

### ✅ **Canaux Serveur RelayHub**
```
CLIENT ←──── Port 7777 (Friend Requests) ────→ SERVER
CLIENT ←──── Port 8888 (Messages Chat)   ────→ SERVER
CLIENT ←──── Port 8891 (Fichiers TCP)    ────→ SERVER  ← NOUVEAU
CLIENT ←──── Port 8889 (API Commands)    ────→ SERVER
CLIENT ←──── WebRTC DataChannels P2P      ────→ CLIENT
```

### 🚀 **Transferts Fichiers Dual-Mode**
- **P2P WebRTC** : Fichiers directs via DataChannels (optimal)
- **TCP Relay** : Fallback automatique via port 8891 (évite saturation chat)
- **Auto-detection** : Utilise P2P si disponible, sinon TCP relay
- **Progress bars** : Synchronisées temps réel des deux côtés

### 📊 **Performance Optimisée**
- **WebRTC** : 64KB chunks, 1MB buffers, flow control agressif
- **TCP Relay** : 1MB chunks, canal séparé, logs optimisés
- **Résultat** : Transferts fluides sans saturation + UX améliorée

*Dernière mise à jour: 15 Septembre 2025 - Architecture Canal Séparé Fichiers Complète*