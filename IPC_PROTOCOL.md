# Protocole IPC ChatP2P Client/Server

## Architecture générale

- **ChatP2P.Server** : Console application gérant la logique P2P/réseau
- **ChatP2P.Client** : Interface utilisateur moderne (WPF)
- **Communication** : TCP localhost sur port 8889
- **Format** : JSON avec structure de requête/réponse

## Structure des messages

### Requête Client → Server
```json
{
    "command": "p2p|contacts|crypto|status",
    "action": "action_specifique",
    "data": { /* payload optionnel */ }
}
```

### Réponse Server → Client
```json
{
    "success": true|false,
    "data": { /* réponse ou null */ },
    "error": "message d'erreur si success=false",
    "timestamp": "2025-01-01T12:00:00.000Z"
}
```

## Commandes disponibles

### 1. Commandes P2P (`command: "p2p"`)

| Action | Description | Data |
|--------|-------------|------|
| `start` | Démarre le réseau P2P | `{ "stun_urls": ["stun:..."] }` |
| `stop` | Arrête le réseau P2P | `null` |
| `peers` | Liste des peers connectés | `null` |
| `send_message` | Envoie un message P2P | `{ "peer": "nom", "message": "texte" }` |
| `send_file` | Envoie un fichier P2P | `{ "peer": "nom", "file_path": "...", "chunk_size": 8192 }` |

### 2. Commandes Contacts (`command: "contacts"`)

| Action | Description | Data |
|--------|-------------|------|
| `list` | Liste des contacts | `null` |
| `add` | Ajoute un contact | `{ "peer": "nom", "public_key": "base64..." }` |
| `remove` | Supprime un contact | `{ "peer": "nom" }` |
| `negotiate_keys` | Lance négociation clés | `{ "peer": "nom" }` |

### 3. Commandes Crypto (`command: "crypto"`)

| Action | Description | Data |
|--------|-------------|------|
| `generate_keypair` | Génère paire de clés PQC | `null` |
| `encrypt` | Chiffre un message | `{ "message": "...", "public_key": "base64..." }` |
| `decrypt` | Déchiffre un message | `{ "encrypted_data": "base64...", "private_key": "base64..." }` |

### 4. Commandes Status (`command: "status"`)

| Action | Description | Data |
|--------|-------------|------|
| `null` | État du serveur | `null` |

## Exemples d'usage

### Démarrer P2P
```json
// Client → Server
{
    "command": "p2p",
    "action": "start",
    "data": {
        "stun_urls": ["stun:stun.l.google.com:19302"]
    }
}

// Server → Client
{
    "success": true,
    "data": "P2P network started",
    "timestamp": "2025-01-01T12:00:00.000Z"
}
```

### Envoyer un message
```json
// Client → Server
{
    "command": "p2p",
    "action": "send_message", 
    "data": {
        "peer": "Alice",
        "message": "Hello World!"
    }
}

// Server → Client
{
    "success": true,
    "data": "Message sent",
    "timestamp": "2025-01-01T12:00:00.000Z"
}
```

### Ajouter un contact
```json
// Client → Server
{
    "command": "contacts",
    "action": "add",
    "data": {
        "peer": "Bob",
        "public_key": "base64encodedkey..."
    }
}

// Server → Client  
{
    "success": true,
    "data": "Contact added",
    "timestamp": "2025-01-01T12:00:00.000Z"
}
```

## Gestion des erreurs

```json
{
    "success": false,
    "error": "Peer not found: Alice",
    "timestamp": "2025-01-01T12:00:00.000Z"
}
```

## Événements Server → Client (WebSocket futur)

Pour les notifications en temps réel (messages reçus, changement d'état P2P), le serveur pourra envoyer des événements :

```json
{
    "event": "p2p_message_received",
    "data": {
        "peer": "Alice",
        "message": "Hello!",
        "timestamp": "2025-01-01T12:00:00.000Z"
    }
}
```

## Sécurité

- Communication uniquement sur localhost (127.0.0.1:8889)
- Pas d'authentification nécessaire (sécurité par isolation réseau)
- Validation JSON côté serveur
- Timeout des connexions inactives (30s)

## Implémentation

### Serveur (C# .NET 8)
- `TcpListener` sur localhost:8889
- Gestion asynchrone des clients multiples
- Intégration avec modules VB.NET existants (P2PManager, ChatP2P.Crypto)

### Client (WPF à créer)
- `TcpClient` vers localhost:8889
- Interface moderne type Telegram/WhatsApp
- Gestion asynchrone des requêtes/réponses
- Binding MVVM pour la réactivité UI