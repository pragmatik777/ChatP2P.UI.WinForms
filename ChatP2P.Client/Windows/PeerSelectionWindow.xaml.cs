using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace ChatP2P.Client
{
    public partial class PeerSelectionWindow : Window
    {
        public string? SelectedPeer { get; private set; }
        private readonly ObservableCollection<ContactInfo> _contacts;

        public PeerSelectionWindow(ObservableCollection<ContactInfo> contacts)
        {
            InitializeComponent();
            _contacts = contacts;
            lstPeers.ItemsSource = _contacts;
        }

        private void LstPeers_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            btnSelect.IsEnabled = lstPeers.SelectedItem != null;
        }

        private void BtnSelect_Click(object sender, RoutedEventArgs e)
        {
            if (lstPeers.SelectedItem is ContactInfo selectedContact)
            {
                SelectedPeer = selectedContact.PeerName;
                DialogResult = true;
                Close();
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}