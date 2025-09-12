# 📋 Claude Code Session Guide - ChatP2P

## 🏗️ **NOUVELLE ARCHITECTURE CLIENT/SERVEUR**

### Architecture Séparée
- **ChatP2P.Server** : Console application C# gérant la logique P2P/réseau
- **ChatP2P.Client** : Interface utilisateur WPF moderne
- **Communication** : TCP localhost sur port 8889 avec protocole JSON
- **Migration** : De VB.NET WinForms vers C# WPF moderne

## 🚀 Commandes de Build

```bash
# Build complet solution
dotnet build ChatP2P.UI.WinForms.sln --configuration Debug

# Build serveur uniquement
dotnet build ChatP2P.Server\ChatP2P.Server.csproj --configuration Debug

# Build client uniquement  
dotnet build ChatP2P.Client\ChatP2P.Client.csproj --configuration Debug

# Clean complet
dotnet clean ChatP2P.UI.WinForms.sln
```

## 📁 Architecture du Projet

### 🖥️ **ChatP2P.Server (Console C#)**
- **`Program.cs`** : Point d'entrée, serveur TCP et dispatcher API
- **`P2PService.cs`** : Service P2P WebRTC avec signaling
- **`ContactManager.cs`** : Gestion contacts et demandes d'amis
- **`DatabaseService.cs`** : Persistance données locales
- **`KeyExchangeManager.cs`** : Gestion négociation clés cryptographiques
- **`P2PManager.cs`** : Interface avec modules VB.NET existants
- **`LocalDb.cs`** : Base de données locale
- **`IceP2PSession.cs`** : Sessions P2P individuelles

### 🖥️ **ChatP2P.Client (WPF C#)**
- **`MainWindow.xaml/.cs`** : Interface principale moderne avec 3 onglets
- **`Models.cs`** : Modèles de données (PeerInfo, ContactInfo, ChatSession, etc.)
- **`SecurityCenterWindow.xaml/.cs`** : Centre de sécurité
- **Windows/** : Fenêtres additionnelles (AddContact, P2PConfig, etc.)

### Emplacements Exécutables
- **Serveur Debug**: `ChatP2P.Server\bin\Debug\net8.0\ChatP2P.Server.exe`
- **Client Debug**: `ChatP2P.Client\bin\Debug\net8.0-windows\ChatP2P.Client.exe`

## ⚡ Fonctionnalités Implémentées

### 🌐 **Communication Client/Serveur**
- **Protocole IPC** : Voir `IPC_PROTOCOL.md` pour spécifications complètes
- **Format JSON** : Requêtes/réponses structurées
- **Commands disponibles** : p2p, contacts, crypto, keyexchange, search, security, status
- **Port TCP** : 8889 sur localhost

### 🎯 **Interface Client Moderne (WPF)**
- **Onglet Connection** : Configuration réseau, statuts serveur/P2P, liste amis en ligne
- **Onglet Chat** : Interface type Telegram avec messages en temps réel
- **Onglet Contacts** : Gestion contacts, demandes d'amis, recherche pairs
- **Thème sombre** : Interface moderne avec couleurs #FF2B2B2B, #FF0D7377

### 🔧 **Gestion Contacts Avancée**
- **Recherche pairs** : Via `SearchPeers()` dans serveur
- **Demandes d'amis** : Workflow complet avec accept/reject
- **Import clés** : Support clés publiques manuelles
- **Status temps réel** : Online/Offline basé sur connexions actives

### 🛡️ **Sécurité Intégrée**
- **Génération clés PQC** : Post-Quantum Cryptography via `P2PMessageCrypto`
- **TOFU (Trust On First Use)** : Gestion automatique confiance pairs
- **Centre sécurité** : Interface dédiée gestion trust et empreintes
- **Persistance sécurisée** : Base données locale pour contacts/clés

## 🔄 Systèmes de Transfert

### 🏃 **P2P WebRTC Moderne**
- **Localisation**: `P2PService.cs` dans serveur
- **Caractéristiques**: 
  - WebRTC DataChannels pour connexion directe
  - Support signaling via serveur TCP
  - Gestion sessions avec `IceP2PSession.cs`
  - Messages texte et binaires

### 📁 **Transferts Fichiers**
- **Base64 encoding** : Pour transport via JSON
- **Chunks** : Support découpage gros fichiers
- **Progress tracking** : Barre progression dans client WPF
- **Cancel support** : Annulation transferts en cours

### 🔗 **Communication Hybride**
- **Messages courts** : Via WebRTC P2P direct
- **Gros fichiers** : Via découpage chunks + TCP relay si nécessaire
- **Fallback** : TCP relay si P2P échoue

## 🚧 État Actuel et Problèmes

### ⚠️ **Problème Actuel - Liste d'Amis**
- **Location**: `MainWindow.xaml.cs:272-306` (`RefreshPeersList()`)
- **Problème**: Affichage de la liste d'amis après réception requête friend request
- **Symptôme**: La liste ne se met pas à jour correctement après accept/reject
- **Méthodes concernées**: 
  - `RefreshPeersList()` : Récupération contacts depuis serveur
  - `AcceptFriendRequest()` : Acceptance requête et refresh
  - `_peers.Clear()` et ajout dans collection WPF

### ✅ **Migration VB.NET → C# Terminée**
- **UI moderne** : WPF avec binding MVVM remplace WinForms
- **Logique réseau** : P2P service C# remplace modules VB.NET
- **Persistance** : JSON remplace My.Settings Windows
- **Architecture** : Client/Serveur séparé remplace monolithe

### ✅ **Settings Persistence Moderne**
- **Client WPF**: `Properties.Settings.Default` pour config UI
- **Serveur**: JSON files (`contacts.json`, `contact_requests.json`)
- **P2P Config**: `P2PConfig` class avec sérialisation

## 📊 Configuration P2P Moderne

### Variables Principales (`Models.cs - P2PConfig`)
```csharp
public class P2PConfig
{
    public int ChunkSize { get; set; } = 8192;
    public int MaxFileSize { get; set; } = 104857600; // 100MB
    public bool UseCompression { get; set; } = true;
    public string[] StunServers { get; set; } = { "stun:stun.l.google.com:19302" };
    public int ConnectionTimeout { get; set; } = 30000; // 30 seconds
}
```

### 📁 Fichiers de Configuration
- **Client Settings**: `Properties.Settings.Default` (WPF standard)
- **Serveur Contacts**: `contacts.json` (dictionnaire contacts avec clés)
- **Friend Requests**: `contact_requests.json` (liste demandes pendantes)
- **Server IP**: `server.txt` (IP serveur pour client)

### API Communication (`MainWindow.xaml.cs`)
```csharp
private async Task<ApiResponse?> SendApiRequest(string command, string? action = null, object? data = null)
{
    var request = new ApiRequest { Command = command, Action = action, Data = data };
    // TCP communication vers localhost:8889
}
```

## 🔧 Points d'Optimisation Futurs

### Améliorations Interface
1. **Real-time updates**: WebSocket pour notifications push serveur→client
2. **Message history**: Persistance historique conversations
3. **File preview**: Aperçu fichiers images/documents
4. **Status indicators**: Indicateurs visuels état connexions P2P
5. **Search optimization**: Recherche contacts plus rapide

### Améliorations Backend
1. **Scalability**: Support plusieurs clients simultanés
2. **Relay server**: Serveur relay pour NAT traversal
3. **Encryption**: Chiffrement E2E messages et fichiers
4. **Database**: Migration vers SQLite pour performance
5. **Logging**: Système logs structurés avec niveaux

### WebRTC Optimisations
- **TURN servers**: Support TURN pour NAT strict
- **Bandwidth adaptation**: Ajustement débit selon qualité réseau
- **Connection pooling**: Réutilisation connexions existantes

## 📝 Debugging et Logs

### Logs Serveur Console
```bash
# API Requests
"API: p2p - start"
"API: contacts - list" 
"API: search - find_peer"

# P2P Events
"P2P Signal to peer: ..."
"Connected peers count: X"
"Message sent to peer: ..."

# Contact Management
"Contact ajouté: PeerName (Verified: true)"
"Friend request from X to Y created"
"Demande acceptée: X ↔ Y"
```

### Logs Client WPF
- **Location**: `Desktop\ChatP2P_Logs\client.log`
- **Method**: `LogToFile()` dans `MainWindow.xaml.cs:1058-1076`
- **Format**: `[timestamp] message`

## 🚨 Notes Importantes

### Sécurité
- **PQC Crypto**: Post-Quantum Cryptography via `ChatP2P.Crypto`
- **TOFU Trust**: Trust On First Use pour nouveaux contacts
- **Local only**: Communication uniquement localhost:8889
- **No plaintext secrets**: Clés stockées en base64 uniquement

### Performance
- **Async/await**: Toute communication réseau asynchrone
- **ObservableCollection**: Binding WPF réactif pour listes
- **JSON parsing**: Sérialisation rapide avec System.Text.Json
- **TCP persistent**: Connexion maintenue client↔serveur

## 🧹 Migration Status

### ✅ Terminé
- Architecture client/serveur séparée
- Interface WPF moderne avec 3 onglets
- Communication IPC via TCP/JSON
- Gestion contacts avec friend requests
- Recherche pairs fonctionnelle
- Persistance JSON côté serveur
- Settings WPF côté client

### 🚧 En Cours
- **Problème liste d'amis**: Affichage après accept/reject request
- Optimisation refresh des collections WPF
- Messages temps réel dans chat

### 📋 À Faire
- WebSocket pour push notifications
- Historique conversations persistant
- P2P file transfer avec progress
- Security Center complet

---

**🎯 Status: MIGRATION ARCHITECTURE** - Client/Serveur séparé, problème liste d'amis en cours

*Dernière mise à jour: Session du 12/09/2025 - Migration VB.NET→C# terminée, debugging liste contacts*