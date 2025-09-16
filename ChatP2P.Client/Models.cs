using System;
using System.ComponentModel;
using System.Linq;

namespace ChatP2P.Client
{
    public class PeerInfo : INotifyPropertyChanged
    {
        private string _name = "";
        private string _status = "Offline";
        private string _p2pStatus = "❌";
        private string _cryptoStatus = "❌";
        private string _authStatus = "❌";

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); }
        }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(nameof(Status)); }
        }

        public string P2PStatus
        {
            get => _p2pStatus;
            set { _p2pStatus = value; OnPropertyChanged(nameof(P2PStatus)); }
        }

        public string CryptoStatus
        {
            get => _cryptoStatus;
            set { _cryptoStatus = value; OnPropertyChanged(nameof(CryptoStatus)); }
        }

        public string AuthStatus
        {
            get => _authStatus;
            set { _authStatus = value; OnPropertyChanged(nameof(AuthStatus)); }
        }

        public string StatusIcon => Status == "Online" ? "🟢" : "⚫";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class ContactInfo : INotifyPropertyChanged
    {
        private string _peerName = "";
        private string _status = "Offline";
        private bool _isVerified = false;
        private DateTime _addedDate = DateTime.Now;
        private string _lastSeen = "Never";
        private string _trustNote = "";
        private bool _isPinned = false;

        public string PeerName
        {
            get => _peerName;
            set { _peerName = value; OnPropertyChanged(nameof(PeerName)); }
        }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(nameof(Status)); }
        }

        public bool IsVerified
        {
            get => _isVerified;
            set { _isVerified = value; OnPropertyChanged(nameof(IsVerified)); }
        }

        public DateTime AddedDate
        {
            get => _addedDate;
            set { _addedDate = value; OnPropertyChanged(nameof(AddedDate)); }
        }

        public string LastSeen
        {
            get => _lastSeen;
            set { _lastSeen = value; OnPropertyChanged(nameof(LastSeen)); }
        }

        public string TrustNote
        {
            get => _trustNote;
            set { _trustNote = value; OnPropertyChanged(nameof(TrustNote)); }
        }

        public bool IsPinned
        {
            get => _isPinned;
            set { _isPinned = value; OnPropertyChanged(nameof(IsPinned)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class ChatSession : INotifyPropertyChanged
    {
        private string _peerName = "";
        private bool _isP2PConnected = false;
        private bool _isCryptoActive = false;
        private bool _isAuthenticated = false;
        private DateTime _lastActivity = DateTime.Now;
        private string _lastMessage = "";
        private int _unreadCount = 0;
        private bool _isOnline = false;

        public string PeerName
        {
            get => _peerName;
            set { _peerName = value; OnPropertyChanged(nameof(PeerName)); OnPropertyChanged(nameof(DisplayName)); }
        }

        public bool IsP2PConnected
        {
            get => _isP2PConnected;
            set { _isP2PConnected = value; OnPropertyChanged(nameof(IsP2PConnected)); OnPropertyChanged(nameof(StatusIndicator)); }
        }

        public bool IsCryptoActive
        {
            get => _isCryptoActive;
            set { _isCryptoActive = value; OnPropertyChanged(nameof(IsCryptoActive)); OnPropertyChanged(nameof(StatusIndicator)); }
        }

        public bool IsAuthenticated
        {
            get => _isAuthenticated;
            set { _isAuthenticated = value; OnPropertyChanged(nameof(IsAuthenticated)); OnPropertyChanged(nameof(StatusIndicator)); }
        }

        public DateTime LastActivity
        {
            get => _lastActivity;
            set { _lastActivity = value; OnPropertyChanged(nameof(LastActivity)); OnPropertyChanged(nameof(LastActivityFormatted)); }
        }

        public string LastMessage
        {
            get => _lastMessage;
            set { _lastMessage = value; OnPropertyChanged(nameof(LastMessage)); }
        }

        public int UnreadCount
        {
            get => _unreadCount;
            set { _unreadCount = value; OnPropertyChanged(nameof(UnreadCount)); OnPropertyChanged(nameof(HasUnread)); OnPropertyChanged(nameof(UnreadDisplay)); }
        }

        public bool IsOnline
        {
            get => _isOnline;
            set { _isOnline = value; OnPropertyChanged(nameof(IsOnline)); OnPropertyChanged(nameof(StatusIcon)); }
        }

        public string DisplayName => PeerName;
        public string LastActivityFormatted => LastActivity.ToString("HH:mm");
        public bool HasUnread => UnreadCount > 0;
        public string UnreadDisplay => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
        public string StatusIcon => IsOnline ? "🟢" : "⚫";
        public bool HasErrors => LastMessage != null && (LastMessage.Contains("[DECRYPT_ERROR") || LastMessage.Contains("[ENCRYPT_ERROR"));

        public string StatusIndicator
        {
            get
            {
                if (IsP2PConnected && IsCryptoActive && IsAuthenticated)
                    return "🟢";
                else if (IsP2PConnected)
                    return "🟡";
                else
                    return "🔴";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class ChatMessage
    {
        public string Sender { get; set; } = "";
        public string Content { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public bool IsFromMe { get; set; } = false;
        public MessageType Type { get; set; } = MessageType.Text;
    }

    public enum MessageType
    {
        Text,
        File,
        System
    }

    public class FriendRequest : INotifyPropertyChanged
    {
        private string _fromPeer = "";
        private string _toPeer = "";
        private string _message = "";
        private DateTime _requestDate = DateTime.Now;
        private string _status = "pending";
        private string _publicKey = "";

        public string FromPeer
        {
            get => _fromPeer;
            set { _fromPeer = value; OnPropertyChanged(nameof(FromPeer)); }
        }

        public string ToPeer
        {
            get => _toPeer;
            set { _toPeer = value; OnPropertyChanged(nameof(ToPeer)); }
        }

        public string Message
        {
            get => _message;
            set { _message = value; OnPropertyChanged(nameof(Message)); }
        }

        public DateTime RequestDate
        {
            get => _requestDate;
            set { _requestDate = value; OnPropertyChanged(nameof(RequestDate)); }
        }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(nameof(Status)); }
        }

        public string PublicKey
        {
            get => _publicKey;
            set { _publicKey = value; OnPropertyChanged(nameof(PublicKey)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class P2PConfig
    {
        public int ChunkSize { get; set; } = 8192;
        public int MaxFileSize { get; set; } = 104857600; // 100MB
        public bool UseCompression { get; set; } = true;
        public IceServerConfig[] IceServers { get; set; } = GetDefaultIceServers();
        public int ConnectionTimeout { get; set; } = 30000; // 30 seconds
        public int IceGatheringTimeout { get; set; } = 15000; // 15 seconds
        public bool EnableNatTypeDetection { get; set; } = true;
        public int MaxRetryAttempts { get; set; } = 3;

        // Legacy property for compatibility
        public string[] StunServers 
        { 
            get => IceServers?.Where(s => s.Type == "stun").Select(s => s.Urls[0]).ToArray() ?? new string[0];
            set => throw new NotSupportedException("Use IceServers property instead");
        }

        private static IceServerConfig[] GetDefaultIceServers()
        {
            return new[]
            {
                // Multiple STUN servers for better reliability
                new IceServerConfig { Type = "stun", Urls = new[] { "stun:stun.l.google.com:19302" } },
                new IceServerConfig { Type = "stun", Urls = new[] { "stun:stun1.l.google.com:19302" } },
                new IceServerConfig { Type = "stun", Urls = new[] { "stun:stun2.l.google.com:19302" } },
                new IceServerConfig { Type = "stun", Urls = new[] { "stun:stun.cloudflare.com:3478" } },
                new IceServerConfig { Type = "stun", Urls = new[] { "stun:openrelay.metered.ca:80" } },
                
                // Free TURN servers for NAT traversal (add authentication as needed)
                new IceServerConfig 
                { 
                    Type = "turn", 
                    Urls = new[] { "turn:openrelay.metered.ca:80" },
                    Username = "openrelayproject",
                    Credential = "openrelayproject"
                },
                new IceServerConfig 
                { 
                    Type = "turn", 
                    Urls = new[] { "turn:openrelay.metered.ca:443", "turn:openrelay.metered.ca:443?transport=tcp" },
                    Username = "openrelayproject", 
                    Credential = "openrelayproject"
                }
            };
        }
    }

    public class IceServerConfig
    {
        public string Type { get; set; } = "stun"; // "stun" or "turn"
        public string[] Urls { get; set; } = new string[0];
        public string? Username { get; set; }
        public string? Credential { get; set; }
        public string? CredentialType { get; set; } = "password";
        public bool Enabled { get; set; } = true;
    }

    public class FileTransferInfo
    {
        public string FileName { get; set; } = "";
        public long FileSize { get; set; } = 0;
        public long BytesTransferred { get; set; } = 0;
        public bool IsIncoming { get; set; } = false;
        public DateTime StartTime { get; set; } = DateTime.Now;
        public string Peer { get; set; } = "";

        public double ProgressPercentage => FileSize > 0 ? (double)BytesTransferred / FileSize * 100 : 0;
        public bool IsCompleted => BytesTransferred >= FileSize;
    }
}