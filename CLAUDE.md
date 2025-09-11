# 📋 Claude Code Session Guide - ChatP2P

## 🚀 Commandes de Build Rapides

```bash
# Build Debug
dotnet build --configuration Debug

# Build Release 
dotnet build --configuration Release

# Clean + Build
dotnet clean && dotnet build --configuration Debug
```

## 📁 Architecture du Projet

### Fichiers Principaux
- **`Form1.vb`** : Interface principale avec tous les transferts P2P/Relay
- **`PrivateChatForm.vb`** : Fenêtre de chat privé avec barre de progression et débit
- **`P2PAdvancedForm.vb`** : Panneau de configuration BitTorrent avancée
- **`P2PFileTransfer.vb`** : Système BitTorrent-like (ChatP2P.Core)

### Emplacements Exécutables
- **Debug**: `bin\Debug\net8.0-windows10.0.17763\ChatP2P.UI.WinForms.exe`
- **Release**: `bin\Release\net8.0-windows10.0.17763\ChatP2P.UI.WinForms.exe`

## ⚡ Fonctionnalités Récentes Implémentées

### 🎯 **Affichage du Débit en Temps Réel**
- **Localisation**: `PrivateChatForm.vb:472-490` (UpdateSendProgress)
- **Format**: `"Envoi : fichier.txt — 85% | 1.2 MB/s"`
- **Calcul**: Débit moyen depuis le début du transfert
- **Rafraîchissement**: Toutes les 500ms
- **Unités**: B/s, KB/s, MB/s (auto-adaptatif)

### 🎛️ **Limitation de Bande Passante**
- **Localisation**: `P2PAdvancedForm.vb:125-147` (contrôles UI)
- **Implémentation**: `Form1.vb:2115-2133` (logique BitTorrent)
- **Propriétés**: 
  - `EnableBandwidthLimit: Boolean`
  - `MaxSpeedKBps: Integer` (10-10000 KB/s)
- **Surveillance**: Contrôle toutes les secondes avec pauses intelligentes

### 🔧 **Panneau P2P Avancé Revu**
- **Taille**: 400x490px
- **5 Presets optimisés**:
  - 🚀 **ULTRA RAPIDE**: 16KB chunks, 500 batches, 0ms délais, pas de limite
  - ⚡ **RAPIDE**: 12KB chunks, 350 batches, 2ms délais, 2MB/s max
  - ⚖️ **ÉQUILIBRÉ**: 8KB chunks, 200 batches, 10ms délais, 1MB/s max
  - 🛡️ **SÉCURISÉ**: 4KB chunks, 100 batches, 50ms délais, 500KB/s max
  - ↺ **DÉFAUT**: Reset aux valeurs par défaut

## 🔄 Systèmes de Transfert

### 🏃 **Relay (TCP) - Ultra Rapide Restauré**
- **Localisation**: `Form1.vb:2232-2305` (SendFileRelayOptimized)
- **Caractéristiques**: 
  - **ZERO délais** (secret de la vitesse originale)
  - 32KB buffer
  - Support cryptage Ed25519
  - Vitesse "à fond" restaurée

### 🔗 **P2P BitTorrent (UDP WebRTC)**
- **Localisation**: `Form1.vb:2025-2160` (SendFileP2PBitTorrentLike)
- **Caractéristiques**:
  - SHA256 hash par chunk
  - Assemblage non-séquentiel
  - Anti-crash 400 chunks (limite WebRTC)
  - Limitation bande passante intégrée
  - Retry automatique des chunks perdus

## 🐛 Problèmes Résolus

### ✅ **Checkboxes Persistence** (`Form1.vb:590-645`)
- **Solution**: `Form1_Shown` + Timer 100ms + corrections save functions
- **Problème principal**: Fonctions `PersistXXXToSettingsIfPossible()` utilisaient Reflection pour sauver des Boolean dans des propriétés String
- **Fix critique**: Remplacement par `My.Settings.StrictTrust = chkStrictTrust.Checked.ToString()`
- **Toutes checkboxes**: StrictTrust, Verbose, EncryptRelay, PqRelay

### ✅ **P2P Advanced Settings Persistence** (`P2PAdvancedForm.vb:414-501`)
- **Solution**: Fichier texte simple `p2p_settings.txt` avec format key=value  
- **LoadFromSettings()**: Chargement au Form_Load de P2PAdvancedForm
- **SaveToSettings()**: Sauvegarde automatique lors de l'application des changements
- **Limite bande passante**: Maximum étendu à 999MB/s (était 10MB/s)

### ✅ **Performance Relay vs P2P**
- **Relay**: Méthode originale rapide complètement séparée
- **P2P**: BitTorrent optimisé avec presets configurables
- **Séparation**: Aucune interférence entre les deux systèmes

## 📊 Configuration BitTorrent

### Variables Principales (`P2PAdvancedForm.vb`)
```vb
Public Property ChunkSize As Integer = 8192        ' Taille des chunks
Public Property BatchSize As Integer = 200         ' Chunks par batch  
Public Property BatchDelayMs As Integer = 10       ' Délai entre batches
Public Property SetupDelayMs As Integer = 100      ' Délai initial
Public Property MaxRetries As Integer = 3          ' Retry par chunk
Public Property EnableBandwidthLimit As Boolean = False
Public Property MaxSpeedKBps As Integer = 1000     ' Limite en KB/s (10-999999)
```

### 📁 Fichiers de Configuration
- **Checkbox settings**: `My.Settings` (fichier utilisateur Windows)
- **P2P Advanced**: `p2p_settings.txt` (répertoire application)
  ```
  ChunkSize=8192
  BatchSize=200
  EnableBandwidthLimit=True
  MaxSpeedKBps=1000
  ...
  ```

### Utilisation dans le Code (`Form1.vb:2027-2037`)
```vb
Dim CHUNK_SIZE = If(_p2pConfig IsNot Nothing, _p2pConfig.ChunkSize, 8192)
Dim BATCH_SIZE = If(_p2pConfig IsNot Nothing, _p2pConfig.BatchSize, 200)
Dim ENABLE_BANDWIDTH_LIMIT = If(_p2pConfig IsNot Nothing, _p2pConfig.EnableBandwidthLimit, False)
```

## 🔧 Points d'Optimisation Futurs

### Potentielles Améliorations
1. **Compression chunks**: Ajouter compression LZ4/Gzip optionnelle
2. **Priorité chunks**: Système de priorité pour chunks critiques  
3. **Multi-stream**: Parallélisation avec plusieurs WebRTC DataChannels
4. **Cache intelligent**: Cache des chunks récents pour re-envoi rapide
5. **QoS adaptatif**: Ajustement automatique selon latence réseau

### WebRTC Limitations Connues
- **400 chunks max**: Crash WebRTC au-delà (contourné par BitTorrent)
- **DataChannel size**: Messages trop gros causent des pertes
- **Ordre delivery**: Messages peuvent arriver désordonnés (géré par hash)

## 📝 Logs de Debug Importants

### Patterns de Recherche Utiles
```bash
# Transferts P2P
[P2P TORRENT]
[BT]
[BANDWIDTH]

# Relay
[RELAY]
[RELAY+ENC]

# Configuration
[SETTINGS]
[P2P CONFIG]
```

## 🚨 Notes Importantes

### Sécurité
- **Ed25519**: Clés publiques pour authentification TOFU
- **SHA256**: Hash de vérification intégrité chunks
- **XChaCha20-Poly1305**: Cryptage PQC optionnel
- **Pas de secrets**: Aucune clé privée n'est loggée

### Performance
- **Relay TCP**: Pour vitesse maximale (crypté ou non)
- **P2P UDP**: Pour connexion directe avec limitation bande passante
- **BitTorrent**: Pour gros fichiers avec reprise/vérification

## 🧹 Nettoyage Debug

### Logs Supprimés
- ❌ `*** [DEBUG] NOUVELLE VERSION P2P FILE TRANSFER CHARGÉE ***`
- ❌ `[SETTINGS] StrictTrust UI forced: False`
- ❌ `[SETTINGS] Verbose saved: True` 
- ❌ `[SETTINGS] XXX UI forced: XXX`

### Logs Conservés
- ✅ Logs d'erreur (`[SETTINGS] XXX save error:` en verbose)
- ✅ Logs fonctionnels importants (connexions, transferts)

---

**🎯 Status: COMPLET** - Toutes les fonctionnalités demandées sont implémentées et fonctionnelles

*Dernière mise à jour: Session du 11/09/2025 - Finalisation persistence + nettoyage debug*