using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace ChatP2P.Client
{
    public partial class P2PConfigWindow : Window
    {
        public P2PConfig Config { get; private set; }
        private readonly ObservableCollection<string> _stunServers = new();

        public P2PConfigWindow(P2PConfig config)
        {
            InitializeComponent();
            Config = config;
            LoadConfiguration();
        }

        private void LoadConfiguration()
        {
            txtChunkSize.Text = Config.ChunkSize.ToString();
            txtMaxFileSize.Text = (Config.MaxFileSize / 1024 / 1024).ToString(); // Convert to MB
            chkUseCompression.IsChecked = Config.UseCompression;
            txtConnectionTimeout.Text = Config.ConnectionTimeout.ToString();

            _stunServers.Clear();
            foreach (var server in Config.StunServers)
            {
                _stunServers.Add(server);
            }
            lstStunServers.ItemsSource = _stunServers;
        }

        private void SaveConfiguration()
        {
            try
            {
                Config.ChunkSize = int.Parse(txtChunkSize.Text);
                Config.MaxFileSize = int.Parse(txtMaxFileSize.Text) * 1024 * 1024; // Convert from MB
                Config.UseCompression = chkUseCompression.IsChecked ?? false;
                Config.ConnectionTimeout = int.Parse(txtConnectionTimeout.Text);
                Config.StunServers = _stunServers.ToArray();
            }
            catch (FormatException)
            {
                MessageBox.Show("Please enter valid numeric values for all settings.", 
                              "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                throw;
            }
        }

        private void BtnAddStunServer_Click(object sender, RoutedEventArgs e)
        {
            var serverUrl = txtNewStunServer.Text.Trim();
            if (!string.IsNullOrWhiteSpace(serverUrl))
            {
                if (!_stunServers.Contains(serverUrl))
                {
                    _stunServers.Add(serverUrl);
                    txtNewStunServer.Text = "";
                }
                else
                {
                    MessageBox.Show("STUN server already exists in the list.", 
                                  "Duplicate Entry", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void BtnRemoveStunServer_Click(object sender, RoutedEventArgs e)
        {
            if (lstStunServers.SelectedItem is string selectedServer)
            {
                _stunServers.Remove(selectedServer);
            }
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Reset all settings to default values?", 
                                       "Confirm Reset", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                Config = new P2PConfig(); // Reset to defaults
                LoadConfiguration();
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveConfiguration();
                DialogResult = true;
                Close();
            }
            catch (FormatException)
            {
                // Error message already shown in SaveConfiguration
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}