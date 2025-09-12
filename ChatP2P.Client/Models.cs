using System;
using System.ComponentModel;

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
            set { _lastActivity = value; OnPropertyChanged(nameof(LastActivity)); }
        }

        public string DisplayName => PeerName;

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
        public string[] StunServers { get; set; } = { "stun:stun.l.google.com:19302" };
        public int ConnectionTimeout { get; set; } = 30000; // 30 seconds
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