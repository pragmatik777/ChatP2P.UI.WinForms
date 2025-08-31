// ChatP2P.Crypto.Shims/MlKemShim.cs
using System;
using System.Linq;
using System.Reflection;
using Org.BouncyCastle.Security; // SecureRandom

namespace ChatP2P.Crypto.Shims
{
    /// <summary>
    /// Shim ML-KEM au format byte[] via REFLECTION.
    /// NE référence jamais les types BC dans les signatures -> compile même si ML-KEM est absent.
    /// </summary>
    public static class MlKemShim
    {
        public enum Strength { MLKEM512 = 512, MLKEM768 = 768, MLKEM1024 = 1024 }

        public static Strength DefaultStrength = Strength.MLKEM768;

        private static readonly SecureRandom Rng = new SecureRandom();

        // ---------- Résolution des types ML-KEM au runtime ----------
        private static Type T_Params() => GetTypeOrThrow("Org.BouncyCastle.Pqc.Crypto.MLKem.MLKemParameters");
        private static Type T_KeyPairGen() => GetTypeOrThrow("Org.BouncyCastle.Pqc.Crypto.MLKem.MLKemKeyPairGenerator");
        private static Type T_KeyGenParams() => GetTypeOrThrow("Org.BouncyCastle.Pqc.Crypto.MLKem.MLKemKeyGenerationParameters");
        private static Type T_PubKey() => GetTypeOrThrow("Org.BouncyCastle.Pqc.Crypto.MLKem.MLKemPublicKeyParameters");
        private static Type T_PrivKey() => GetTypeOrThrow("Org.BouncyCastle.Pqc.Crypto.MLKem.MLKemPrivateKeyParameters");
        private static Type T_Encapsulator() => GetTypeOrThrow("Org.BouncyCastle.Pqc.Crypto.MLKem.MLKemEncapsulator");
        private static Type T_Decapsulator() => GetTypeOrThrow("Org.BouncyCastle.Pqc.Crypto.MLKem.MLKemDecapsulator");

        private static object MapParams(Strength s)
        {
            var tp = T_Params();
            var fieldName = s switch
            {
                Strength.MLKEM512 => "ml_kem_512",
                Strength.MLKEM1024 => "ml_kem_1024",
                _ => "ml_kem_768"
            };
            var f = tp.GetField(fieldName, BindingFlags.Public | BindingFlags.Static)
                    ?? throw new InvalidOperationException($"Champ {fieldName} introuvable sur {tp.FullName}.");
            return f.GetValue(null)!;
        }

        // Utilitaires Reflection
        private static Type GetTypeOrThrow(string fullName)
        {
            // Cherche dans toutes les assemblies chargées
            var t = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => SafelyGetType(a, fullName))
                    .FirstOrDefault(x => x != null);

            if (t == null)
                throw new NotSupportedException(
                    $"Le type '{fullName}' est introuvable. " +
                    $"Ta version de BouncyCastle ne semble pas inclure ML-KEM. " +
                    $"Installe 'BouncyCastle.Cryptography' (version avec ML-KEM) sur le projet Shims uniquement.");

            return t!;
        }

        private static Type? SafelyGetType(Assembly asm, string fullName)
        {
            try { return asm.GetType(fullName, throwOnError: false); }
            catch { return null; }
        }

        private static object New(Type t, params object[] args)
        {
            var inst = Activator.CreateInstance(t, args);
            if (inst == null)
                throw new InvalidOperationException($"Impossible d’instancier {t.FullName}.");
            return inst;
        }

        private static object Call(object target, string methodName, params object[] args)
        {
            var t = target is Type tt ? tt : target.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic |
                        (target is Type ? BindingFlags.Static : BindingFlags.Instance);

            // Resolve par nombre d'args
            var m = t.GetMethods(flags).FirstOrDefault(mi =>
            {
                if (!string.Equals(mi.Name, methodName, StringComparison.Ordinal)) return false;
                var p = mi.GetParameters();
                return p.Length == args.Length;
            });

            if (m == null)
                throw new MissingMethodException($"{t.FullName}.{methodName}({args.Length} args) introuvable.");

            return m.Invoke(target is Type ? null : target, args)!;
        }

        private static byte[] GetEncoded(object keyParamsObj)
        {
            var m = keyParamsObj.GetType().GetMethod("GetEncoded", BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new MissingMethodException($"{keyParamsObj.GetType().FullName}.GetEncoded introuvable.");
            return (byte[])m.Invoke(keyParamsObj, null)!;
        }

        private static object PubFromEncoding(object parms, byte[] encoded)
        {
            var tp = T_PubKey();
            var m = tp.GetMethod("FromEncoding", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new MissingMethodException($"{tp.FullName}.FromEncoding introuvable.");
            return m.Invoke(null, new object[] { parms, encoded })!;
        }

        private static object PrivFromEncoding(object parms, byte[] encoded)
        {
            var tp = T_PrivKey();
            var m = tp.GetMethod("FromEncoding", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new MissingMethodException($"{tp.FullName}.FromEncoding introuvable.");
            return m.Invoke(null, new object[] { parms, encoded })!;
        }

        // ---------- API publique byte[] ----------

        public static (byte[] pk, byte[] sk) KeyGen() => KeyGen(DefaultStrength);

        public static (byte[] pk, byte[] sk) KeyGen(Strength strength)
        {
            var parms = MapParams(strength);

            var gen = New(T_KeyPairGen());
            // new MLKemKeyGenerationParameters(rng, parms)
            var kgp = New(T_KeyGenParams(), Rng, parms);

            // gen.Init(kgp)
            Call(gen, "Init", kgp);

            // var kp = gen.GenerateKeyPair()
            var kp = Call(gen, "GenerateKeyPair");

            // kp.Public / kp.Private
            var pub = kp.GetType().GetProperty("Public", BindingFlags.Public | BindingFlags.Instance)!.GetValue(kp)!;
            var prv = kp.GetType().GetProperty("Private", BindingFlags.Public | BindingFlags.Instance)!.GetValue(kp)!;

            return (GetEncoded(pub), GetEncoded(prv));
        }

        public static (byte[] cipherText, byte[] sharedSecret) Encapsulate(byte[] peerPublic)
            => Encapsulate(peerPublic, DefaultStrength);

        public static (byte[] cipherText, byte[] sharedSecret) Encapsulate(byte[] peerPublic, Strength strength)
        {
            if (peerPublic == null || peerPublic.Length == 0)
                throw new ArgumentException("peerPublic vide.");

            var parms = MapParams(strength);
            var pub = PubFromEncoding(parms, peerPublic);

            // Essaye ctor(parms), sinon ctor()
            object enc;
            var ctorParms = T_Encapsulator().GetConstructor(new[] { T_Params() });
            enc = ctorParms != null ? New(T_Encapsulator(), parms) : New(T_Encapsulator());

            // Version A: Encapsulate(pub) -> (ct, ss) ou ISecretWithEncapsulation
            // Version B: Encapsulate(pub, rng) -> idem
            object res;
            try
            {
                res = Call(enc, "Encapsulate", pub);
            }
            catch
            {
                res = Call(enc, "Encapsulate", pub, Rng);
            }

            return UnpackEncapsulation(res);
        }

        public static byte[] Decapsulate(byte[] myPrivate, byte[] cipherText)
            => Decapsulate(myPrivate, cipherText, DefaultStrength);

        public static byte[] Decapsulate(byte[] myPrivate, byte[] cipherText, Strength strength)
        {
            if (myPrivate == null || myPrivate.Length == 0)
                throw new ArgumentException("myPrivate vide.");
            if (cipherText == null || cipherText.Length == 0)
                throw new ArgumentException("cipherText vide.");

            var parms = MapParams(strength);
            var prv = PrivFromEncoding(parms, myPrivate);

            // Essaye ctor(parms), sinon ctor() + Init(prv)
            object dec;
            var ctorParms = T_Decapsulator().GetConstructor(new[] { T_Params() });
            dec = ctorParms != null ? New(T_Decapsulator(), parms) : New(T_Decapsulator());

            // Certaines versions exigent Init(prv)
            var needsInit = T_Decapsulator().GetMethod("Init", BindingFlags.Public | BindingFlags.Instance);
            if (needsInit != null)
                Call(dec, "Init", prv);

            // Version A : Decapsulate(cipherText) -> ss
            // Version B : Decapsulate(prv, cipherText) -> ss
            try
            {
                var ss = Call(dec, "Decapsulate", cipherText);
                return (byte[])ss;
            }
            catch
            {
                var ss = Call(dec, "Decapsulate", prv, cipherText);
                return (byte[])ss;
            }
        }

        // ---------- Extraction (ISecretWithEncapsulation | tuple | objet) ----------
        private static (byte[] ct, byte[] ss) UnpackEncapsulation(object encapsResult)
        {
            if (encapsResult == null)
                throw new InvalidOperationException("Encapsulate a renvoyé null.");

            // 1) Interface ISecretWithEncapsulation ?
            var iface = encapsResult.GetType().GetInterfaces()
                         .FirstOrDefault(i => i.Name == "ISecretWithEncapsulation");
            if (iface != null)
            {
                var getEnc = encapsResult.GetType().GetMethod("GetEncapsulation", BindingFlags.Public | BindingFlags.Instance);
                var getSec = encapsResult.GetType().GetMethod("GetSecret", BindingFlags.Public | BindingFlags.Instance);
                var ct = (byte[])getEnc!.Invoke(encapsResult, null)!;
                var ss = (byte[])getSec!.Invoke(encapsResult, null)!;

                if (encapsResult is IDisposable disp) disp.Dispose();
                return (ct, ss);
            }

            // 2) Tuple/ValueTuple (Item1/Item2)
            var p1 = encapsResult.GetType().GetProperty("Item1", BindingFlags.Public | BindingFlags.Instance);
            var p2 = encapsResult.GetType().GetProperty("Item2", BindingFlags.Public | BindingFlags.Instance);
            if (p1 != null && p2 != null)
            {
                var ct = (byte[])p1.GetValue(encapsResult)!;
                var ss = (byte[])p2.GetValue(encapsResult)!;
                return (ct, ss);
            }

            // 3) Méthodes GetEncapsulation/GetSecret directes
            var getEnc2 = encapsResult.GetType().GetMethod("GetEncapsulation", BindingFlags.Public | BindingFlags.Instance);
            var getSec2 = encapsResult.GetType().GetMethod("GetSecret", BindingFlags.Public | BindingFlags.Instance);
            if (getEnc2 != null && getSec2 != null)
            {
                var ct = (byte[])getEnc2.Invoke(encapsResult, null)!;
                var ss = (byte[])getSec2.Invoke(encapsResult, null)!;
                return (ct, ss);
            }

            throw new NotSupportedException($"Type de retour Encapsulate non supporté: {encapsResult.GetType().FullName}");
        }
    }
}
