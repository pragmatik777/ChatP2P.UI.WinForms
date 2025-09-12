using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ChatP2P.Client
{
    public partial class MainWindow : Window
    {
        private TcpClient? _serverConnection;
        private NetworkStream? _serverStream;
        private bool _isConnectedToServer = false;
        private string _currentPeer = "";
        private ChatSession? _currentChatSession;
        private FileTransferInfo? _currentFileTransfer;
        private P2PConfig _p2pConfig = new();

        // Collections for data binding
        private readonly ObservableCollection<PeerInfo> _peers = new();
        private readonly ObservableCollection<ContactInfo> _contacts = new();
        private readonly ObservableCollection<ChatSession> _chatSessions = new();
        private readonly ObservableCollection<PeerInfo> _searchResults = new();
        private readonly ObservableCollection<FriendRequest> _friendRequests = new();
        private readonly Dictionary<string, List<ChatMessage>> _chatHistory = new();

        public MainWindow()
        {
            InitializeComponent();
            InitializeCollections();
            this.Loaded += MainWindow_Loaded;
            this.Closing += MainWindow_Closing;
        }

        private void InitializeCollections()
        {
            lstPeers.ItemsSource = _peers;
            lstContacts.ItemsSource = _contacts;
            lstActiveChats.ItemsSource = _chatSessions;
            lstSearchResults.ItemsSource = _searchResults;
            lstFriendRequests.ItemsSource = _friendRequests;

            // Load settings
            LoadSettings();
        }

        private void LoadSettings()
        {
            try
            {
                // Load from app settings or config file
                txtDisplayName.Text = Properties.Settings.Default.DisplayName ?? Environment.UserName;
                
                chkStrictTrust.IsChecked = Properties.Settings.Default.StrictTrust;
                chkVerbose.IsChecked = Properties.Settings.Default.VerboseLogging;
                chkEncryptRelay.IsChecked = Properties.Settings.Default.EncryptRelay;
                chkPqRelay.IsChecked = Properties.Settings.Default.PqRelay;
                chkAutoConnect.IsChecked = Properties.Settings.Default.AutoConnect;
            }
            catch (Exception ex)
            {
                _ = LogToFile($"Error loading settings: {ex.Message}");
            }
        }

        private void SaveSettings()
        {
            try
            {
                Properties.Settings.Default.DisplayName = txtDisplayName.Text;
                
                Properties.Settings.Default.StrictTrust = chkStrictTrust.IsChecked ?? false;
                Properties.Settings.Default.VerboseLogging = chkVerbose.IsChecked ?? false;
                Properties.Settings.Default.EncryptRelay = chkEncryptRelay.IsChecked ?? false;
                Properties.Settings.Default.PqRelay = chkPqRelay.IsChecked ?? false;
                Properties.Settings.Default.AutoConnect = chkAutoConnect.IsChecked ?? false;
                
                Properties.Settings.Default.Save();
            }
            catch (Exception ex)
            {
                _ = LogToFile($"Error saving settings: {ex.Message}");
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Auto-connect only if checkbox is checked
            if (chkAutoConnect.IsChecked == true)
            {
                await ConnectToServer();
                await RefreshAll();
            }
        }

        private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveSettings();
            await DisconnectFromServer();
        }

        // ===== Server Connection =====
        private async Task ConnectToServer()
        {
            var serverIp = "127.0.0.1";
            
            try
            {
                _serverConnection = new TcpClient();
                
                try
                {
                    var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
                    var serverFile = Path.Combine(exeDir, "server.txt");
                    
                    if (File.Exists(serverFile))
                    {
                        serverIp = File.ReadAllText(serverFile).Trim();
                        await LogToFile($"Server IP loaded from {serverFile}: {serverIp}");
                    }
                }
                catch (Exception ex)
                {
                    await LogToFile($"Error reading server.txt: {ex.Message}");
                }
                
                await _serverConnection.ConnectAsync(serverIp, 8889);
                _serverStream = _serverConnection.GetStream();
                _isConnectedToServer = true;
                
                UpdateServerStatus("Connected", Colors.Green);
                await StartP2PNetwork();
            }
            catch (Exception ex)
            {
                UpdateServerStatus($"Connection Error", Colors.Red);
                _isConnectedToServer = false;
                await LogToFile($"SERVER CONNECTION ERROR - IP: {serverIp}, Port: 8889, Error: {ex.Message}");
            }
        }

        private async Task DisconnectFromServer()
        {
            try
            {
                _serverStream?.Close();
                _serverConnection?.Close();
            }
            catch { }
            
            _serverStream = null;
            _serverConnection = null;
            _isConnectedToServer = false;
            UpdateServerStatus("Disconnected", Colors.Red);
        }

        // ===== API Communication =====
        private async Task<ApiResponse?> SendApiRequest(string command, string? action = null, object? data = null)
        {
            if (!_isConnectedToServer || _serverStream == null)
            {
                UpdateServerStatus("Not Connected", Colors.Red);
                return null;
            }

            try
            {
                var request = new ApiRequest
                {
                    Command = command,
                    Action = action,
                    Data = data
                };

                var requestJson = JsonSerializer.Serialize(request);
                var requestBytes = Encoding.UTF8.GetBytes(requestJson);
                
                await _serverStream.WriteAsync(requestBytes, 0, requestBytes.Length);
                
                var buffer = new byte[8192];
                var bytesRead = await _serverStream.ReadAsync(buffer, 0, buffer.Length);
                var responseJson = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                
                return JsonSerializer.Deserialize<ApiResponse>(responseJson);
            }
            catch (Exception ex)
            {
                await LogToFile($"API Request Error: {ex.Message}");
                return null;
            }
        }

        // ===== P2P Network Management =====
        private async Task StartP2PNetwork()
        {
            var response = await SendApiRequest("p2p", "start", new 
            { 
                display_name = txtDisplayName.Text.Trim()
            });
            if (response?.Success == true)
            {
                await RefreshPeersList();
            }
        }

        private async Task StopP2PNetwork()
        {
            var response = await SendApiRequest("p2p", "stop");
            if (response?.Success == true)
            {
                // P2P network stopped
            }
        }

        private async Task RefreshPeersList()
        {
            // Get connected peers from server
            var peersResponse = await SendApiRequest("p2p", "peers");
            var contactsResponse = await SendApiRequest("contacts", "list");
            
            if (peersResponse?.Success == true && contactsResponse?.Success == true)
            {
                try
                {
                    var connectedPeers = new List<string>();
                    var contacts = new List<ContactInfo>();
                    
                    // Parse connected peers
                    if (peersResponse.Data != null)
                    {
                        var peersData = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(peersResponse.Data.ToString()!);
                        if (peersData != null)
                        {
                            connectedPeers = peersData.Select(p => p.GetValueOrDefault("name", "")?.ToString() ?? "").ToList();
                        }
                    }
                    
                    // Parse contacts
                    if (contactsResponse.Data != null)
                    {
                        var contactsData = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(contactsResponse.Data.ToString()!);
                        if (contactsData != null)
                        {
                            foreach (var contactData in contactsData)
                            {
                                var peerName = contactData.GetValueOrDefault("peer_name", "")?.ToString() ?? "";
                                var status = connectedPeers.Contains(peerName) ? "Online" : "Offline";
                                
                                contacts.Add(new ContactInfo
                                {
                                    PeerName = peerName,
                                    Status = status,
                                    IsVerified = contactData.GetValueOrDefault("verified", false)?.ToString() == "True",
                                    AddedDate = DateTime.TryParse(contactData.GetValueOrDefault("added_date", DateTime.Now)?.ToString(), out var addedDate) ? addedDate : DateTime.Now
                                });
                            }
                        }
                    }
                    
                    // Update UI - Friends Online list shows only connected friends
                    Dispatcher.Invoke(() =>
                    {
                        _peers.Clear();
                        _contacts.Clear();
                        
                        Console.WriteLine($"Updating contacts list with {contacts.Count} contacts");
                        
                        // Add connected contacts to Friends Online list
                        foreach (var contact in contacts.Where(c => c.Status == "Online"))
                        {
                            _peers.Add(new PeerInfo
                            {
                                Name = contact.PeerName,
                                Status = contact.Status,
                                P2PStatus = "✅",
                                CryptoStatus = contact.IsVerified ? "🔒" : "❌",
                                AuthStatus = "✅"
                            });
                        }
                        
                        // Add all contacts to Contacts tab
                        foreach (var contact in contacts)
                        {
                            _contacts.Add(contact);
                        }
                        
                        Console.WriteLine($"Added {_peers.Count} online peers and {_contacts.Count} total contacts");
                    });
                }
                catch (Exception ex)
                {
                    await LogToFile($"Error parsing peers/contacts: {ex.Message}");
                }
            }
        }


        private async Task RefreshAll()
        {
            await RefreshPeersList();
            await RefreshFriendRequests();
        }
        
        private async Task RefreshFriendRequests()
        {
            try
            {
                var displayName = txtDisplayName.Text.Trim();
                if (string.IsNullOrWhiteSpace(displayName))
                    return;

                var response = await SendApiRequest("contacts", "get_friend_requests", new 
                { 
                    peer_name = displayName
                });
                
                if (response?.Success == true && response.Data != null)
                {
                    _friendRequests.Clear();
                    
                    // Parse friend requests
                    if (response.Data is JsonElement dataElement && dataElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var requestElement in dataElement.EnumerateArray())
                        {
                            if (requestElement.TryGetProperty("FromPeer", out var fromElement) &&
                                requestElement.TryGetProperty("Message", out var msgElement) &&
                                requestElement.TryGetProperty("RequestDate", out var dateElement) &&
                                requestElement.TryGetProperty("PublicKey", out var keyElement))
                            {
                                _friendRequests.Add(new FriendRequest
                                {
                                    FromPeer = fromElement.GetString() ?? "",
                                    ToPeer = displayName,
                                    Message = msgElement.GetString() ?? "",
                                    RequestDate = DateTime.TryParse(dateElement.GetString(), out var reqDate) ? reqDate : DateTime.Now,
                                    PublicKey = keyElement.GetString() ?? "",
                                    Status = "pending"
                                });
                            }
                        }
                    }
                    
                    await LogToFile($"Loaded {_friendRequests.Count} friend requests for {displayName}");
                }
            }
            catch (Exception ex)
            {
                await LogToFile($"Error refreshing friend requests: {ex.Message}");
            }
        }

        // ===== UI Updates =====
        private void UpdateServerStatus(string status, Color color)
        {
            Dispatcher.Invoke(() =>
            {
                lblServerStatus.Content = status;
                lblServerStatus.Foreground = new SolidColorBrush(color);
            });
        }

        private void UpdateNetworkMode(string mode, Color color)
        {
            Dispatcher.Invoke(() =>
            {
                lblNetworkMode.Content = mode;
                lblNetworkMode.Foreground = new SolidColorBrush(color);
            });
        }

        private void UpdateChatStatus(string peer, bool p2p, bool crypto, bool auth)
        {
            Dispatcher.Invoke(() =>
            {
                lblP2PStatus.Text = p2p ? "P2P: ✅" : "P2P: ❌";
                lblP2PStatus.Foreground = new SolidColorBrush(p2p ? Colors.Green : Colors.Red);
                
                lblCryptoStatus.Text = crypto ? "🔒: ✅" : "🔒: ❌";
                lblCryptoStatus.Foreground = new SolidColorBrush(crypto ? Colors.Green : Colors.Red);
                
                lblAuthStatus.Text = auth ? "Auth: ✅" : "Auth: ❌";
                lblAuthStatus.Foreground = new SolidColorBrush(auth ? Colors.Green : Colors.Red);
            });
        }

        // ===== Connection Tab Event Handlers =====
        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            var displayName = txtDisplayName.Text.Trim();
            
            if (string.IsNullOrWhiteSpace(displayName))
            {
                MessageBox.Show("Please enter a display name", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await ConnectToServer();
            if (_isConnectedToServer)
            {
                await StartP2PNetwork();
                await RefreshAll();
                btnConnect.IsEnabled = false;
                btnDisconnect.IsEnabled = true;
            }
        }

        private async void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            await DisconnectFromServer();
            await StopP2PNetwork();
            
            // Clear peer list
            _peers.Clear();
            
            // Update UI
            btnConnect.IsEnabled = true;
            btnDisconnect.IsEnabled = false;
            UpdateServerStatus("Disconnected", Colors.Red);
            UpdateNetworkMode("None", Colors.Gray);
        }

        private async void BtnSearchPeer_Click(object sender, RoutedEventArgs e)
        {
            var searchTerm = txtSearchPeer.Text.Trim();
            
            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                MessageBox.Show("Please enter a peer name to search", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // Search for peers via server API
                var response = await SendApiRequest("search", "find_peer", new { peer_name = searchTerm });
                
                if (response?.Success == true && response.Data != null)
                {
                    _searchResults.Clear();
                    
                    await LogToFile($"Search response data type: {response.Data.GetType()}, Value: {response.Data}");
                    
                    // Parse search results
                    if (response.Data is JsonElement dataElement && dataElement.ValueKind == JsonValueKind.Array)
                    {
                        await LogToFile($"Found array with {dataElement.GetArrayLength()} elements");
                        foreach (var peerElement in dataElement.EnumerateArray())
                        {
                            if (peerElement.TryGetProperty("name", out var nameElement) &&
                                peerElement.TryGetProperty("status", out var statusElement))
                            {
                                var peerName = nameElement.GetString() ?? "";
                                var peerStatus = statusElement.GetString() ?? "Unknown";
                                await LogToFile($"Adding peer: {peerName} - {peerStatus}");
                                _searchResults.Add(new PeerInfo
                                {
                                    Name = peerName,
                                    Status = peerStatus
                                });
                            }
                        }
                    }
                    
                    if (_searchResults.Count > 0)
                    {
                        lstSearchResults.Visibility = Visibility.Visible;
                        await LogToFile($"Showing {_searchResults.Count} search results");
                    }
                    else
                    {
                        MessageBox.Show($"No peers found with name '{searchTerm}'", "Search Results", 
                                      MessageBoxButton.OK, MessageBoxImage.Information);
                        lstSearchResults.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    MessageBox.Show($"Search failed or returned no data", "Search Results", 
                                  MessageBoxButton.OK, MessageBoxImage.Information);
                    lstSearchResults.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error searching peers: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
                await LogToFile($"Search peer error: {ex.Message}");
            }
        }

        private async void BtnAddFriend_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is PeerInfo peerToAdd)
            {
                try
                {
                    // Send friend request via server API
                    var response = await SendApiRequest("contacts", "send_friend_request", new 
                    { 
                        peer_name = peerToAdd.Name,
                        requester = txtDisplayName.Text.Trim()
                    });
                    
                    if (response?.Success == true)
                    {
                        MessageBox.Show($"Friend request sent to {peerToAdd.Name}!", "Success", 
                                      MessageBoxButton.OK, MessageBoxImage.Information);
                        
                        // Clear search results
                        _searchResults.Clear();
                        lstSearchResults.Visibility = Visibility.Collapsed;
                        txtSearchPeer.Text = "";
                    }
                    else
                    {
                        MessageBox.Show($"Failed to send friend request: {response?.Error}", "Error", 
                                      MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error sending friend request: {ex.Message}", "Error", 
                                  MessageBoxButton.OK, MessageBoxImage.Error);
                    await LogToFile($"Add friend error: {ex.Message}");
                }
            }
        }

        private async void BtnImportKey_Click(object sender, RoutedEventArgs e)
        {
            var importWindow = new AddContactWindow();
            if (importWindow.ShowDialog() == true)
            {
                var peerName = importWindow.PeerName;
                var publicKey = importWindow.PublicKey;
                
                if (!string.IsNullOrWhiteSpace(peerName) && !string.IsNullOrWhiteSpace(publicKey))
                {
                    try
                    {
                        // Add contact with imported key
                        var response = await SendApiRequest("contacts", "import_contact", new 
                        { 
                            peer_name = peerName,
                            public_key = publicKey
                        });
                        
                        if (response?.Success == true)
                        {
                            MessageBox.Show($"Contact {peerName} imported successfully!", "Success", 
                                          MessageBoxButton.OK, MessageBoxImage.Information);
                            await RefreshPeersList();
                        }
                        else
                        {
                            MessageBox.Show($"Failed to import contact: {response?.Error}", "Error", 
                                          MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error importing contact: {ex.Message}", "Error", 
                                      MessageBoxButton.OK, MessageBoxImage.Error);
                        await LogToFile($"Import contact error: {ex.Message}");
                    }
                }
            }
        }

        private async void BtnAcceptFriend_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is FriendRequest request)
            {
                try
                {
                    var displayName = txtDisplayName.Text.Trim();
                    var response = await SendApiRequest("contacts", "accept_friend_request", new 
                    { 
                        requester = request.FromPeer,
                        accepter = displayName
                    });
                    
                    if (response?.Success == true)
                    {
                        MessageBox.Show($"Friend request from {request.FromPeer} accepted!", "Success", 
                                      MessageBoxButton.OK, MessageBoxImage.Information);
                        
                        // Refresh both friend requests and contacts
                        await RefreshFriendRequests();
                        await RefreshPeersList();
                    }
                    else
                    {
                        MessageBox.Show($"Failed to accept friend request: {response?.Error}", "Error", 
                                      MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error accepting friend request: {ex.Message}", "Error", 
                                  MessageBoxButton.OK, MessageBoxImage.Error);
                    await LogToFile($"Accept friend error: {ex.Message}");
                }
            }
        }

        private async void BtnRejectFriend_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is FriendRequest request)
            {
                try
                {
                    var displayName = txtDisplayName.Text.Trim();
                    var response = await SendApiRequest("contacts", "reject_friend_request", new 
                    { 
                        requester = request.FromPeer,
                        rejecter = displayName
                    });
                    
                    if (response?.Success == true)
                    {
                        MessageBox.Show($"Friend request from {request.FromPeer} rejected.", "Success", 
                                      MessageBoxButton.OK, MessageBoxImage.Information);
                        
                        // Refresh friend requests
                        await RefreshFriendRequests();
                    }
                    else
                    {
                        MessageBox.Show($"Failed to reject friend request: {response?.Error}", "Error", 
                                      MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error rejecting friend request: {ex.Message}", "Error", 
                                  MessageBoxButton.OK, MessageBoxImage.Error);
                    await LogToFile($"Reject friend error: {ex.Message}");
                }
            }
        }

        private void LstPeers_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (lstPeers.SelectedItem is PeerInfo peer)
            {
                OpenChatWindow(peer.Name);
            }
        }

        private async void BtnSendFile_Click(object sender, RoutedEventArgs e)
        {
            if (lstPeers.SelectedItem is PeerInfo peer)
            {
                await SendFileToSelectedPeer(peer.Name);
            }
            else
            {
                MessageBox.Show("Please select a peer first", "No Peer Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnSecurityCenter_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var securityWindow = new SecurityCenterWindow();
                securityWindow.Owner = this;
                securityWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening Security Center: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnP2PAdvanced_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Open P2P configuration window
            var configWindow = new P2PConfigWindow(_p2pConfig);
            if (configWindow.ShowDialog() == true)
            {
                _p2pConfig = configWindow.Config;
                // Apply new config
            }
        }

        // ===== Chat Tab Event Handlers =====
        private void LstActiveChats_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstActiveChats.SelectedItem is ChatSession session)
            {
                _currentChatSession = session;
                _currentPeer = session.PeerName;
                LoadChatForSession(session);
            }
        }

        private void BtnNewChat_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Show peer selection dialog
            var peerSelectionWindow = new PeerSelectionWindow(_contacts);
            if (peerSelectionWindow.ShowDialog() == true && peerSelectionWindow.SelectedPeer != null)
            {
                OpenChatWindow(peerSelectionWindow.SelectedPeer);
            }
        }

        private void BtnStartP2PChat_Click(object sender, RoutedEventArgs e)
        {
            if (_currentChatSession != null)
            {
                StartP2PConnection(_currentChatSession.PeerName);
            }
        }

        private async void BtnSendFileChat_Click(object sender, RoutedEventArgs e)
        {
            if (_currentChatSession != null)
            {
                await SendFileToSelectedPeer(_currentChatSession.PeerName);
            }
        }

        private void BtnCancelTransfer_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Cancel file transfer
            fileTransferBorder.Visibility = Visibility.Collapsed;
        }

        private async void BtnSendMessage_Click(object sender, RoutedEventArgs e)
        {
            await SendMessage();
        }

        private async void TxtMessage_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(txtMessage.Text))
            {
                await SendMessage();
            }
        }

        private void BtnClearChat_Click(object sender, RoutedEventArgs e)
        {
            if (_currentChatSession != null)
            {
                messagesPanel.Children.Clear();
                if (_chatHistory.ContainsKey(_currentChatSession.PeerName))
                {
                    _chatHistory[_currentChatSession.PeerName].Clear();
                }
            }
        }

        // ===== Contacts Tab Event Handlers =====

        private async void BtnRemoveContact_Click(object sender, RoutedEventArgs e)
        {
            if (lstContacts.SelectedItem is ContactInfo contact)
            {
                var result = MessageBox.Show($"Are you sure you want to remove {contact.PeerName}?", 
                                           "Confirm Removal", MessageBoxButton.YesNo, MessageBoxImage.Question);
                
                if (result == MessageBoxResult.Yes)
                {
                    var response = await SendApiRequest("contacts", "remove", new { peer_name = contact.PeerName });
                    if (response?.Success == true)
                    {
                        await RefreshPeersList();
                        MessageBox.Show("Contact removed successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
        }

        // ===== Chat Management =====
        private void OpenChatWindow(string peerName)
        {
            // Switch to chat tab
            mainTabControl.SelectedItem = chatTab;
            
            // Find or create chat session
            var existingSession = _chatSessions.FirstOrDefault(s => s.PeerName == peerName);
            if (existingSession == null)
            {
                existingSession = new ChatSession { PeerName = peerName };
                _chatSessions.Add(existingSession);
                
                if (!_chatHistory.ContainsKey(peerName))
                {
                    _chatHistory[peerName] = new List<ChatMessage>();
                }
            }
            
            lstActiveChats.SelectedItem = existingSession;
        }

        private void LoadChatForSession(ChatSession session)
        {
            lblChatPeer.Text = session.PeerName;
            UpdateChatStatus(session.PeerName, session.IsP2PConnected, session.IsCryptoActive, session.IsAuthenticated);
            
            // Enable controls
            txtMessage.IsEnabled = true;
            btnSendMessage.IsEnabled = true;
            
            // Load message history
            messagesPanel.Children.Clear();
            
            if (_chatHistory.TryGetValue(session.PeerName, out var messages))
            {
                foreach (var message in messages)
                {
                    AddMessageToUI(message);
                }
            }
            
            // Add welcome message if no history
            if (messages == null || messages.Count == 0)
            {
                var welcomeMessage = new ChatMessage
                {
                    Content = $"Chat started with {session.PeerName}",
                    Sender = "System",
                    Type = MessageType.System,
                    Timestamp = DateTime.Now
                };
                AddMessageToUI(welcomeMessage);
            }
        }

        private async Task SendMessage()
        {
            if (_currentChatSession == null || string.IsNullOrWhiteSpace(txtMessage.Text))
                return;

            var messageText = txtMessage.Text.Trim();
            txtMessage.Text = "";

            // Create message object
            var message = new ChatMessage
            {
                Content = messageText,
                Sender = txtDisplayName.Text,
                IsFromMe = true,
                Timestamp = DateTime.Now,
                Type = MessageType.Text
            };

            // Add to UI and history
            AddMessageToUI(message);
            AddMessageToHistory(_currentChatSession.PeerName, message);

            // Send via P2P API
            var response = await SendApiRequest("p2p", "send_message", new { 
                peer = _currentChatSession.PeerName, 
                message = messageText 
            });

            if (response?.Success != true)
            {
                // Mark as failed or retry
                var errorMessage = new ChatMessage
                {
                    Content = "❌ Message failed to send",
                    Sender = "System",
                    Type = MessageType.System,
                    Timestamp = DateTime.Now
                };
                AddMessageToUI(errorMessage);
            }
        }

        private void AddMessageToUI(ChatMessage message)
        {
            Dispatcher.Invoke(() =>
            {
                var messageElement = CreateMessageElement(message);
                messagesPanel.Children.Add(messageElement);
                messagesScrollViewer.ScrollToEnd();
            });
        }

        private void AddMessageToHistory(string peerName, ChatMessage message)
        {
            if (!_chatHistory.ContainsKey(peerName))
            {
                _chatHistory[peerName] = new List<ChatMessage>();
            }
            _chatHistory[peerName].Add(message);
        }

        private FrameworkElement CreateMessageElement(ChatMessage message)
        {
            if (message.Type == MessageType.System)
            {
                return new TextBlock
                {
                    Text = message.Content,
                    Foreground = new SolidColorBrush(Colors.Gray),
                    FontSize = 12,
                    FontStyle = FontStyles.Italic,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 10, 0, 10)
                };
            }

            var border = new Border
            {
                Background = message.IsFromMe ? 
                    new SolidColorBrush(Color.FromRgb(13, 115, 119)) : 
                    new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                CornerRadius = new CornerRadius(15),
                Padding = new Thickness(15, 10, 15, 10),
                Margin = new Thickness(message.IsFromMe ? 50 : 10, 5, message.IsFromMe ? 10 : 50, 5),
                HorizontalAlignment = message.IsFromMe ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                MaxWidth = 400
            };

            var textBlock = new TextBlock
            {
                Text = message.Content,
                Foreground = Brushes.White,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap
            };

            border.Child = textBlock;
            return border;
        }

        // ===== File Transfer =====
        private async Task SendFileToSelectedPeer(string peerName)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select file to send",
                Filter = "All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var fileInfo = new FileInfo(dialog.FileName);
                    var fileData = await File.ReadAllBytesAsync(dialog.FileName);
                    var base64Data = Convert.ToBase64String(fileData);

                    // Show transfer progress
                    ShowFileTransferProgress(fileInfo.Name, fileInfo.Length, false);

                    var response = await SendApiRequest("p2p", "send_file", new {
                        peer = peerName,
                        filename = fileInfo.Name,
                        data = base64Data
                    });

                    if (response?.Success == true)
                    {
                        var message = new ChatMessage
                        {
                            Content = $"📎 Sent file: {fileInfo.Name} ({FormatFileSize(fileInfo.Length)})",
                            Sender = txtDisplayName.Text,
                            IsFromMe = true,
                            Type = MessageType.File,
                            Timestamp = DateTime.Now
                        };
                        
                        if (_currentChatSession?.PeerName == peerName)
                        {
                            AddMessageToUI(message);
                        }
                        AddMessageToHistory(peerName, message);
                    }
                    else
                    {
                        MessageBox.Show($"Failed to send file: {response?.Error}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error sending file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    await LogToFile($"File send error: {ex.Message}");
                }
                finally
                {
                    HideFileTransferProgress();
                }
            }
        }

        private void ShowFileTransferProgress(string fileName, long fileSize, bool isIncoming)
        {
            Dispatcher.Invoke(() =>
            {
                lblFileTransferStatus.Text = $"{(isIncoming ? "Receiving" : "Sending")} {fileName}...";
                progressFileTransfer.Value = 0;
                fileTransferBorder.Visibility = Visibility.Visible;
            });
        }

        private void HideFileTransferProgress()
        {
            Dispatcher.Invoke(() =>
            {
                fileTransferBorder.Visibility = Visibility.Collapsed;
            });
        }

        // ===== P2P Connection Management =====
        private async void StartP2PConnection(string peerName)
        {
            var response = await SendApiRequest("p2p", "connect", new { peer = peerName });
            if (response?.Success == true)
            {
                // Update chat session status
                var session = _chatSessions.FirstOrDefault(s => s.PeerName == peerName);
                if (session != null)
                {
                    session.IsP2PConnected = true;
                    if (_currentChatSession == session)
                    {
                        UpdateChatStatus(peerName, true, session.IsCryptoActive, session.IsAuthenticated);
                    }
                }
            }
        }

        // ===== Helper Methods =====
        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private async Task LogToFile(string message)
        {
            try
            {
                var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var logDir = Path.Combine(desktopPath, "ChatP2P_Logs");
                Directory.CreateDirectory(logDir);
                
                var logFile = Path.Combine(logDir, "client.log");
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var logEntry = $"[{timestamp}] {message}{Environment.NewLine}";
                
                await File.AppendAllTextAsync(logFile, logEntry);
            }
            catch
            {
                // Ignore logging errors
            }
        }

        // ===== Data Classes =====
        public class ApiRequest
        {
            public string Command { get; set; } = "";
            public string? Action { get; set; }
            public object? Data { get; set; }
        }

        public class ApiResponse
        {
            [JsonPropertyName("success")]
            public bool Success { get; set; }
            
            [JsonPropertyName("data")]
            public object? Data { get; set; }
            
            [JsonPropertyName("error")]
            public string? Error { get; set; }
            
            [JsonPropertyName("timestamp")]
            public DateTime Timestamp { get; set; }
        }
    }
}