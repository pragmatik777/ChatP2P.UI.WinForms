using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace ChatP2P.Client
{
    public partial class SecurityCenterWindow : Window
    {
        private readonly ObservableCollection<PeerSecurityInfo> _peers = new();
        private string _searchText = "";

        public SecurityCenterWindow()
        {
            InitializeComponent();
            dgPeers.ItemsSource = _peers;
            this.Loaded += SecurityCenterWindow_Loaded;
        }

        private async void SecurityCenterWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await LoadMyFingerprint();
                await LoadPeers();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading Security Center: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadMyFingerprint()
        {
            try
            {
                var response = await SendApiRequest("security", "get_my_fingerprint", new { });
                if (response != null && response.ContainsKey("Fingerprint"))
                {
                    var fp = response["Fingerprint"]?.ToString() ?? "(unknown)";
                    lblMyFingerprint.Content = $"My fingerprint: {fp}";
                }
                else
                {
                    lblMyFingerprint.Content = "My fingerprint: (not available)";
                }
            }
            catch (Exception ex)
            {
                lblMyFingerprint.Content = $"My fingerprint: (error: {ex.Message})";
            }
        }

        private async Task LoadPeers(string searchFilter = "")
        {
            try
            {
                var response = await SendApiRequest("security", "list_peers", new { filter = searchFilter });
                
                _peers.Clear();
                
                if (response != null && response.ContainsKey("peers") && response["peers"] is System.Text.Json.JsonElement peersElement)
                {
                    foreach (var peerElement in peersElement.EnumerateArray())
                    {
                        var peer = new PeerSecurityInfo
                        {
                            Name = GetJsonString(peerElement, "Name"),           // Capital N
                            Trusted = GetJsonBool(peerElement, "Trusted"),      // Capital T  
                            AuthOk = GetJsonBool(peerElement, "AuthOk"),        // Capital A and O
                            Fingerprint = GetJsonString(peerElement, "Fingerprint"), // Capital F
                            CreatedUtc = GetJsonString(peerElement, "CreatedUtc"),   // Capital C and U
                            LastSeenUtc = GetJsonString(peerElement, "LastSeenUtc"), // Capital L, S and U
                            Note = GetJsonString(peerElement, "Note")                // Capital N
                        };
                        _peers.Add(peer);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading peers: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task<System.Collections.Generic.Dictionary<string, object>?> SendApiRequest(string category, string action, object data)
        {
            try
            {
                var serverAddress = GetServerAddress();
                if (string.IsNullOrEmpty(serverAddress))
                    return null;

                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync(System.Net.IPAddress.Parse(serverAddress), 8889);
                
                using var stream = client.GetStream();
                
                var request = new { Command = category, Action = action, Data = data };
                var json = System.Text.Json.JsonSerializer.Serialize(request);
                var buffer = Encoding.UTF8.GetBytes(json + "\n");
                
                await stream.WriteAsync(buffer, 0, buffer.Length);
                
                var responseBuffer = new byte[8192];
                var bytesRead = await stream.ReadAsync(responseBuffer, 0, responseBuffer.Length);
                var responseJson = Encoding.UTF8.GetString(responseBuffer, 0, bytesRead).Trim();
                
                var apiResponse = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(responseJson);
                
                // Extract the Data field from the API response  
                if (apiResponse != null && apiResponse.ContainsKey("Success") && apiResponse.ContainsKey("Data"))
                {
                    var success = apiResponse["Success"];
                    if (success is System.Text.Json.JsonElement successElement && successElement.GetBoolean())
                    {
                        var responseData = apiResponse["Data"];
                        if (responseData is System.Text.Json.JsonElement dataElement)
                        {
                            return System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(dataElement.GetRawText());
                        }
                    }
                }
                
                return null;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"API request failed: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
        }

        private string GetServerAddress()
        {
            try
            {
                var serverFile = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server.txt");
                if (System.IO.File.Exists(serverFile))
                {
                    return System.IO.File.ReadAllText(serverFile).Trim();
                }
                return "127.0.0.1";
            }
            catch
            {
                return "127.0.0.1";
            }
        }

        private string GetJsonString(System.Text.Json.JsonElement element, string propertyName)
        {
            try
            {
                if (element.TryGetProperty(propertyName, out var prop))
                {
                    return prop.GetString() ?? "";
                }
            }
            catch { }
            return "";
        }

        private bool GetJsonBool(System.Text.Json.JsonElement element, string propertyName)
        {
            try
            {
                if (element.TryGetProperty(propertyName, out var prop))
                {
                    return prop.GetBoolean();
                }
            }
            catch { }
            return false;
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchText = txtSearch.Text.Trim();
            _ = LoadPeers(_searchText);
        }

        private PeerSecurityInfo? GetSelectedPeer()
        {
            return dgPeers.SelectedItem as PeerSecurityInfo;
        }

        private async void BtnTrust_Click(object sender, RoutedEventArgs e)
        {
            var peer = GetSelectedPeer();
            if (peer == null) return;

            try
            {
                await SendApiRequest("security", "set_trusted", new { peer_name = peer.Name, trusted = true });
                await LoadPeers(_searchText);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error setting trust: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnUntrust_Click(object sender, RoutedEventArgs e)
        {
            var peer = GetSelectedPeer();
            if (peer == null) return;

            try
            {
                await SendApiRequest("security", "set_trusted", new { peer_name = peer.Name, trusted = false });
                await LoadPeers(_searchText);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error removing trust: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCopyFingerprint_Click(object sender, RoutedEventArgs e)
        {
            var peer = GetSelectedPeer();
            if (peer == null || string.IsNullOrEmpty(peer.Fingerprint)) return;

            try
            {
                Clipboard.SetText(peer.Fingerprint);
                MessageBox.Show($"Fingerprint copied to clipboard:\n{peer.Fingerprint}", "Copied", 
                              MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error copying fingerprint: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnDetails_Click(object sender, RoutedEventArgs e)
        {
            var peer = GetSelectedPeer();
            if (peer == null) return;

            var details = $"Peer: {peer.Name}\n" +
                         $"Trusted: {(peer.Trusted ? "Yes" : "No")}\n" +
                         $"Auth OK: {(peer.AuthOk ? "Yes" : "No")}\n" +
                         $"Fingerprint: {peer.Fingerprint}\n" +
                         $"Created: {peer.CreatedUtc}\n" +
                         $"Last Seen: {peer.LastSeenUtc}\n" +
                         $"Note: {peer.Note}";

            var input = Microsoft.VisualBasic.Interaction.InputBox(
                $"{details}\n\nModify note:", "Peer Details", peer.Note);
            
            if (!string.IsNullOrEmpty(input) && input != peer.Note)
            {
                try
                {
                    await SendApiRequest("security", "set_note", 
                                       new { peer_name = peer.Name, note = input.Trim() });
                    await LoadPeers(_searchText);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error updating note: {ex.Message}", "Error", 
                                  MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnResetTofu_Click(object sender, RoutedEventArgs e)
        {
            var peer = GetSelectedPeer();
            if (peer == null) return;

            var result = MessageBox.Show($"Reset TOFU for {peer.Name}?\n\n" +
                                       "This will forget the stored public key. " +
                                       "The next connection will be treated as first contact.",
                                       "Reset TOFU", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    await SendApiRequest("security", "reset_tofu", new { peer_name = peer.Name });
                    await LoadPeers(_searchText);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error resetting TOFU: {ex.Message}", "Error", 
                                  MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnImportKey_Click(object sender, RoutedEventArgs e)
        {
            var peer = GetSelectedPeer();
            if (peer == null) return;

            var input = Microsoft.VisualBasic.Interaction.InputBox(
                $"Paste the Ed25519 public key for {peer.Name} (Base64):\n\n" +
                "Warning: This will overwrite the existing TOFU key.", "Import Public Key", "");

            if (!string.IsNullOrWhiteSpace(input))
            {
                try
                {
                    await SendApiRequest("security", "import_key", 
                                       new { peer_name = peer.Name, public_key_b64 = input.Trim() });
                    await LoadPeers(_searchText);
                    MessageBox.Show($"Key imported for {peer.Name}", "Success", 
                                  MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error importing key: {ex.Message}", "Error", 
                                  MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnExportFingerprint_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var response = await SendApiRequest("security", "export_my_key", new { });
                if (response != null && response.ContainsKey("PublicKeyB64") && response.ContainsKey("Fingerprint"))
                {
                    var pubKey = response["PublicKeyB64"]?.ToString() ?? "";
                    var fingerprint = response["Fingerprint"]?.ToString() ?? "";
                    var exportText = $"PubKey(Base64): {pubKey}\nFingerprint: {fingerprint}";
                    
                    Clipboard.SetText(exportText);
                    MessageBox.Show($"Your public key and fingerprint copied to clipboard:\n\n{fingerprint}", 
                                  "Export Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Failed to export key", "Error", 
                                  MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting key: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }

    public class PeerSecurityInfo : INotifyPropertyChanged
    {
        private string _name = "";
        private bool _trusted = false;
        private bool _authOk = false;
        private string _fingerprint = "";
        private string _createdUtc = "";
        private string _lastSeenUtc = "";
        private string _note = "";

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); }
        }

        public bool Trusted
        {
            get => _trusted;
            set { _trusted = value; OnPropertyChanged(nameof(Trusted)); }
        }

        public bool AuthOk
        {
            get => _authOk;
            set { _authOk = value; OnPropertyChanged(nameof(AuthOk)); }
        }

        public string Fingerprint
        {
            get => _fingerprint;
            set { _fingerprint = value; OnPropertyChanged(nameof(Fingerprint)); }
        }

        public string CreatedUtc
        {
            get => _createdUtc;
            set { _createdUtc = value; OnPropertyChanged(nameof(CreatedUtc)); }
        }

        public string LastSeenUtc
        {
            get => _lastSeenUtc;
            set { _lastSeenUtc = value; OnPropertyChanged(nameof(LastSeenUtc)); }
        }

        public string Note
        {
            get => _note;
            set { _note = value; OnPropertyChanged(nameof(Note)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}