# ChatP2P - Historique d'optimisation des transferts P2P

## Contexte du projet
- Application ChatP2P en VB.NET avec WinForms
- Système P2P utilisant WebRTC/STUN UDP avec la librairie SIPSorcery
- Authentification Ed25519 avec TOFU (Trust On First Use)
- Transferts de fichiers via DTLS P2P ou relay selon disponibilité

## Problème initial
Les transferts de gros fichiers (1.8GB) causaient:
- Freeze de l'UI côté émetteur et récepteur
- Envoi "à fond" sans flow control
- Récepteur "qui rame" pour traiter les données

## Diagnostic : Crash WebRTC après 400 chunks
**Observation critique** : Le système crash systématiquement après exactement 400 chunks
- 400 chunks × 8KB = 3.2MB de données  
- Timing : ~40 secondes avec délai 100ms
- Pattern reproductible et précis

**Hypothèses sur la cause** :
1. **Buffer overflow WebRTC DataChannel** - Limite SIPSorcery ~3MB
2. **Memory leak boucle async** - Tasks qui s'accumulent sans GC
3. **Stack overflow async/await** mal géré
4. **Timeout DTLS/ICE** connection après ~40s

## Nouvelles fonctionnalités à implémenter

### 1. Système BitTorrent-like pour gros fichiers
- **Hash fichier** en chunks avec checksums
- **Assemblage non-séquentiel** - ordre d'arrivée libre
- **Reconstruction** du fichier complet à partir du squelette
- **Reprise de transfert** en cas d'interruption

### 2. Améliorations UX demandées
- **Sauvegarde état checkboxes** dans MySettings
- **Logs dans fichier** : `Desktop/ChatP2P_Logs/logfile.txt`
- **Messages P2P groupés par date** style WhatsApp
- **Crypto PQC** pour messages texte P2P vérifiés
- **Panel avancé P2P** : taille chunks, ACK, timeout réglables

### 3. Architecture de chunks intelligents
```vb
Structure FileChunk
    ChunkIndex As Integer
    Hash As String
    Data As Byte()
    Confirmed As Boolean
End Structure
```

### 4. Flow control adaptatif
- **ACK system** : confirmation réception par chunk
- **Sliding window** protocol comme TCP
- **Congestion control** : ajuste débit selon latence
- **Retry intelligent** : seulement chunks perdus

## État de développement actuel
- ✅ Diagnostic crash à 400 chunks identifié
- 🔄 Retour au P2P pour résoudre le vrai problème
- ⏳ Implémentation système chunks avec hash
- ⏳ Panel avancé P2P configuration
- ⏳ Logging fichier + UI améliorée
- ⏳ Vérification crypto PQC messages texte

## Fichiers à modifier
- `Form1.vb`: Panel avancé, logging fichier, sauvegarde settings
- `PrivateChatForm.vb`: Messages groupés par date, style WhatsApp
- `P2PManager.vb`: Système chunks avec hash et ACK
- `MySettings`: Nouveaux paramètres UI et P2P

## Objectif final
🎯 **Transferts P2P UDP fiables** pour fichiers multi-GB avec :
- Reprise de transfert automatique
- Interface utilisateur moderne et configurable  
- Crypto PQC complet (messages + fichiers)
- Performances optimales WebRTC/STUN