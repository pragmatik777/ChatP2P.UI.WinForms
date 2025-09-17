using System;
using System.Net;
using System.Linq;

class Program
{
    static void Main()
    {
        try
        {
            // Test de la méthode GetLocalIPAddress
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            Console.WriteLine($"Hostname: {System.Net.Dns.GetHostName()}");

            Console.WriteLine("\nToutes les adresses IP:");
            foreach (var ip in host.AddressList)
            {
                Console.WriteLine($"  {ip} - Family: {ip.AddressFamily} - IsLoopback: {IPAddress.IsLoopback(ip)}");
            }

            var localIP = host.AddressList
                .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Where(ip => !IPAddress.IsLoopback(ip))
                .FirstOrDefault();

            Console.WriteLine($"\nIP locale sélectionnée: {localIP?.ToString() ?? "Unknown"}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur: {ex.Message}");
        }
    }
}