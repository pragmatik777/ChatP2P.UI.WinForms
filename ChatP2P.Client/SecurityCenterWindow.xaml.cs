using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
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
                var fingerprint = await DatabaseService.Instance.GetMyFingerprint();
                lblMyFingerprint.Content = $"My fingerprint: {fingerprint}";
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
                _peers.Clear();

                var peers = await DatabaseService.Instance.GetSecurityPeerList(searchFilter);
                foreach (var peer in peers)
                {
                    _peers.Add(peer);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading peers: {ex.Message}", "Error",
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
                await DatabaseService.Instance.SetPeerTrusted(peer.Name, true);
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
                await DatabaseService.Instance.SetPeerTrusted(peer.Name, false);
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
                    await DatabaseService.Instance.SetPeerNote(peer.Name, input.Trim());
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
                    await DatabaseService.Instance.ResetPeerTofu(peer.Name);
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
                    await DatabaseService.Instance.ImportPeerKey(peer.Name, input.Trim());
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
                var (publicKeyB64, fingerprint) = await DatabaseService.Instance.ExportMyKey();
                var exportText = $"PubKey(Base64): {publicKeyB64}\nFingerprint: {fingerprint}";

                Clipboard.SetText(exportText);
                MessageBox.Show($"Your public key and fingerprint copied to clipboard:\n\n{fingerprint}",
                              "Export Success", MessageBoxButton.OK, MessageBoxImage.Information);
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
}