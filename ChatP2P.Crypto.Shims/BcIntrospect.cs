// ChatP2P.Crypto.Shims/BcIntrospect.cs
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using Org.BouncyCastle.Security;

namespace ChatP2P.Crypto.Shims
{
    public static class BcIntrospect
    {
        private static Assembly GetBcAssembly() => typeof(SecureRandom).Assembly;

        public static string Report(bool listTypes = false, string nsPrefix = "Org.BouncyCastle.Pqc")
        {
            var asm = GetBcAssembly();
            var an = asm.GetName();
            var loc = asm.Location;

            var pktBytes = an.GetPublicKeyToken() ?? Array.Empty<byte>();
            var pkt = string.Join("", pktBytes.Select(b => b.ToString("x2")));

            string fileVersion;
            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(loc);
                fileVersion = fvi.FileVersion ?? "(n/a)";
            }
            catch
            {
                fileVersion = "(n/a)";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[BC] {an.Name}, Version={an.Version}, PKT={pkt}");
            sb.AppendLine($"[BC] Location: {loc}");
            sb.AppendLine($"[BC] FileVersion: {fileVersion}");

            // Présence de ML-KEM ?
            bool hasMlKem = HasType("Org.BouncyCastle.Pqc.Crypto.MLKem.MLKemParameters");
            sb.AppendLine($"[BC] MLKem present: {hasMlKem}");

            if (!listTypes) return sb.ToString();

            string[] types;
            try
            {
                types = asm.GetTypes()
                           .Where(t => t?.FullName != null &&
                                       t.FullName.StartsWith(nsPrefix, StringComparison.Ordinal))
                           .Select(t => t!.FullName!)
                           .OrderBy(s => s)
                           .ToArray();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types
                          .Where(t => t?.FullName != null &&
                                      t.FullName.StartsWith(nsPrefix, StringComparison.Ordinal))
                          .Select(t => t!.FullName!)
                          .OrderBy(s => s)
                          .ToArray();
            }

            if (types.Length == 0)
            {
                sb.AppendLine($"[BC] Aucun type sous '{nsPrefix}'.");
            }
            else
            {
                sb.AppendLine($"[BC] Types sous '{nsPrefix}':");
                foreach (var t in types) sb.AppendLine(t);
            }

            return sb.ToString();
        }

        public static bool HasType(string fullName)
        {
            try
            {
                return GetBcAssembly().GetType(fullName, throwOnError: false, ignoreCase: false) != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
