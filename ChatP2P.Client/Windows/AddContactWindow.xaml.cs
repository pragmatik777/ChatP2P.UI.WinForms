using System.Windows;

namespace ChatP2P.Client
{
    public partial class AddContactWindow : Window
    {
        public string PeerName => txtPeerName.Text.Trim();
        public string PublicKey => txtPublicKey.Text.Trim();

        public AddContactWindow()
        {
            InitializeComponent();
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(PeerName))
            {
                MessageBox.Show("Please enter a peer name.", "Validation Error", 
                              MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(PublicKey))
            {
                MessageBox.Show("Please enter a public key.", "Validation Error", 
                              MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}