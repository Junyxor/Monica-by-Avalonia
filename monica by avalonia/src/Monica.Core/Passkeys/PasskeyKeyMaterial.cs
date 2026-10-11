using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace Monica.Core.Passkeys;

public sealed record PasskeyKeyMaterial(
    int Algorithm,
    string PublicKeySpkiBase64,
    byte[] CosePublicKey,
    string PrivateKeyPkcs8Base64);

public sealed record CosePublicKey(
    int KeyType,
    int Algorithm,
    byte[]? XCoordinate,
    byte[]? YCoordinate,
    byte[]? Modulus,
    byte[]? Exponent)
{
    public string ToSubjectPublicKeyInfoBase64()
    {
        if (KeyType == PasskeyAlgorithm.OkpKeyType && Algorithm == PasskeyAlgorithm.EdDsa)
        {
            if (XCoordinate is not { Length: 32 }) throw new CryptographicException("An Ed25519 key must contain 32 bytes.");
            return Convert.ToBase64String(SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(
                new Ed25519PublicKeyParameters(XCoordinate)).GetEncoded());
        }
        using var key = ToAsymmetricAlgorithm();
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    public bool MatchesSubjectPublicKeyInfo(string spkiBase64)
    {
        try
        {
            return ToSubjectPublicKeyInfoBase64() == spkiBase64;
        }
        catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException or IOException)
        {
            return false;
        }
    }

    private AsymmetricAlgorithm ToAsymmetricAlgorithm()
    {
        if (KeyType == PasskeyAlgorithm.Ec2KeyType)
        {
            if (XCoordinate is not { Length: 32 } || YCoordinate is not { Length: 32 })
            {
                throw new CryptographicException("An EC2 COSE key must carry two 32-byte coordinates.");
            }

            var ec = ECDsa.Create();
            ec.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = XCoordinate, Y = YCoordinate }
            });
            return ec;
        }

        if (KeyType == PasskeyAlgorithm.RsaKeyType)
        {
            if (Modulus is not { Length: > 0 } || Exponent is not { Length: > 0 })
            {
                throw new CryptographicException("An RSA COSE key must carry a modulus and an exponent.");
            }

            var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters
            {
                Modulus = Modulus,
                Exponent = Exponent
            });
            return rsa;
        }

        throw new CryptographicException($"COSE key type {KeyType} is not supported.");
    }
}

public static class PasskeyAlgorithm
{
    public const int Es256 = -7;
    public const int Rs256 = -257;
    public const int Ps256 = -37;
    public const int EdDsa = -8;

    public const int Ec2KeyType = 2;
    public const int RsaKeyType = 3;
    public const int OkpKeyType = 1;
    public const int P256Curve = 1;
    public const int Ed25519Curve = 6;

    public const int LabelKeyType = 1;
    public const int LabelAlgorithm = 3;
    public const int LabelCurve = -1;
    public const int LabelXCoordinate = -2;
    public const int LabelYCoordinate = -3;
    public const int LabelModulus = -1;
    public const int LabelExponent = -2;

    // Mirrors the Android authenticator: only these two are ever generated, while PS256 assertions
    // imported from elsewhere can still be verified.
    public static bool CanGenerate(int algorithm) => algorithm is Es256 or Rs256;

    public static bool CanVerify(int algorithm) => algorithm is Es256 or Rs256 or Ps256 or EdDsa;

    public static string Describe(int algorithm) => algorithm switch
    {
        Es256 => "ES256",
        Rs256 => "RS256",
        Ps256 => "PS256",
        EdDsa => "EdDSA",
        _ => $"COSE-{algorithm}"
    };
}

public static class PasskeyKeyMaterialGenerator
{
    public static PasskeyKeyMaterial Generate(int algorithm)
    {
        if (!PasskeyAlgorithm.CanGenerate(algorithm))
        {
            throw new NotSupportedException(
                $"Monica's passkey authenticator cannot generate {PasskeyAlgorithm.Describe(algorithm)} keys.");
        }

        return algorithm switch
        {
            PasskeyAlgorithm.Es256 => GenerateEc(),
            _ => GenerateRsa(algorithm)
        };
    }

    public static byte[] Sign(int algorithm, string privateKeyPkcs8Base64, byte[] payload)
    {
        if (!PasskeyAlgorithm.CanVerify(algorithm))
        {
            throw new NotSupportedException(
                $"Monica's passkey authenticator cannot sign with {PasskeyAlgorithm.Describe(algorithm)}.");
        }

        var pkcs8 = Convert.FromBase64String(privateKeyPkcs8Base64);
        try
        {
            if (algorithm == PasskeyAlgorithm.EdDsa)
            {
                if (PrivateKeyFactory.CreateKey(pkcs8) is not Ed25519PrivateKeyParameters key)
                    throw new CryptographicException("The private key does not match Ed25519.");
                var signer = new Ed25519Signer();
                signer.Init(true, key);
                signer.BlockUpdate(payload, 0, payload.Length);
                return signer.GenerateSignature();
            }
            if (algorithm == PasskeyAlgorithm.Es256)
            {
                using var ec = ECDsa.Create();
                ec.ImportPkcs8PrivateKey(pkcs8, out var consumed);
                if (consumed != pkcs8.Length) throw new CryptographicException("Trailing private-key data is not permitted.");
                // WebAuthn ES256 assertions use ASN.1 DER, not fixed-width r || s.
                return ec.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            }
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(pkcs8, out var rsaConsumed);
            if (rsaConsumed != pkcs8.Length) throw new CryptographicException("Trailing private-key data is not permitted.");
            return rsa.SignData(payload, HashAlgorithmName.SHA256,
                algorithm == PasskeyAlgorithm.Ps256 ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1);
        }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
    }

    public static bool Verify(
        int algorithm,
        string publicKeySpkiBase64,
        byte[] payload,
        ReadOnlySpan<byte> signature)
    {
        if (!PasskeyAlgorithm.CanVerify(algorithm))
        {
            return false;
        }

        try
        {
            var subjectPublicKeyInfo = Convert.FromBase64String(publicKeySpkiBase64);
            if (algorithm == PasskeyAlgorithm.EdDsa)
            {
                if (PublicKeyFactory.CreateKey(subjectPublicKeyInfo) is not Ed25519PublicKeyParameters key) return false;
                var verifier = new Ed25519Signer();
                verifier.Init(false, key);
                verifier.BlockUpdate(payload, 0, payload.Length);
                return verifier.VerifySignature(signature.ToArray());
            }
            if (algorithm == PasskeyAlgorithm.Es256)
            {
                using var ec = ECDsa.Create();
                ec.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
                if (bytesRead != subjectPublicKeyInfo.Length)
                {
                    return false;
                }

                return ec.VerifyData(
                    payload,
                    signature.ToArray(),
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence);
            }

            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var rsaBytesRead);
            if (rsaBytesRead != subjectPublicKeyInfo.Length)
            {
                return false;
            }

            return rsa.VerifyData(
                payload,
                signature.ToArray(),
                HashAlgorithmName.SHA256,
                algorithm == PasskeyAlgorithm.Ps256 ? RSASignaturePadding.Pss : RSASignaturePadding.Pkcs1);
        }
        catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException or IOException)
        {
            // Invalid imported keys and malformed signatures fail the bool verification boundary.
            return false;
        }
    }

    public static byte[] BuildCosePublicKey(int algorithm, string publicKeySpkiBase64)
    {
        var subjectPublicKeyInfo = Convert.FromBase64String(publicKeySpkiBase64);
        if (algorithm == PasskeyAlgorithm.EdDsa)
        {
            if (PublicKeyFactory.CreateKey(subjectPublicKeyInfo) is not Ed25519PublicKeyParameters key)
                throw new CryptographicException("The public key does not match Ed25519.");
            return new CborBuilder().WriteStartMap(4)
                .WriteInteger(PasskeyAlgorithm.LabelKeyType).WriteInteger(PasskeyAlgorithm.OkpKeyType)
                .WriteInteger(PasskeyAlgorithm.LabelAlgorithm).WriteInteger(algorithm)
                .WriteInteger(PasskeyAlgorithm.LabelCurve).WriteInteger(PasskeyAlgorithm.Ed25519Curve)
                .WriteInteger(PasskeyAlgorithm.LabelXCoordinate).WriteByteString(key.GetEncoded()).Build();
        }
        if (!PasskeyAlgorithm.CanVerify(algorithm)) throw new NotSupportedException("Unsupported passkey algorithm.");
        if (algorithm is PasskeyAlgorithm.Es256)
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
            var parameters = ec.ExportParameters(false);
            return new CborBuilder()
                .WriteStartMap(5)
                .WriteInteger(PasskeyAlgorithm.LabelKeyType)
                .WriteInteger(PasskeyAlgorithm.Ec2KeyType)
                .WriteInteger(PasskeyAlgorithm.LabelAlgorithm)
                .WriteInteger(algorithm)
                .WriteInteger(PasskeyAlgorithm.LabelCurve)
                .WriteInteger(PasskeyAlgorithm.P256Curve)
                .WriteInteger(PasskeyAlgorithm.LabelXCoordinate)
                .WriteByteString(parameters.Q.X!)
                .WriteInteger(PasskeyAlgorithm.LabelYCoordinate)
                .WriteByteString(parameters.Q.Y!)
                .Build();
        }

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
        var rsaParameters = rsa.ExportParameters(false);
        return new CborBuilder()
            .WriteStartMap(4)
            .WriteInteger(PasskeyAlgorithm.LabelKeyType)
            .WriteInteger(PasskeyAlgorithm.RsaKeyType)
            .WriteInteger(PasskeyAlgorithm.LabelAlgorithm)
            .WriteInteger(algorithm)
            .WriteInteger(PasskeyAlgorithm.LabelModulus)
            .WriteByteString(rsaParameters.Modulus!)
            .WriteInteger(PasskeyAlgorithm.LabelExponent)
            .WriteByteString(rsaParameters.Exponent!)
            .Build();
    }

    public static bool TryParseCosePublicKey(
        ReadOnlySpan<byte> coseKey,
        out CosePublicKey? parsed,
        out int consumedBytes)
    {
        parsed = null;
        consumedBytes = 0;
        try
        {
            var reader = new CborReader(coseKey.ToArray());
            var entries = reader.ReadStartMap();
            var values = new Dictionary<int, CborValue>();

            for (var index = 0; index < entries; index++)
            {
                var label = reader.ReadValue().AsInteger();
                values[label] = reader.ReadValue();
            }

            if (!values.TryGetValue(PasskeyAlgorithm.LabelKeyType, out var keyTypeEntry) ||
                !values.TryGetValue(PasskeyAlgorithm.LabelAlgorithm, out var algorithmEntry))
            {
                return false;
            }

            var keyType = keyTypeEntry.AsInteger();
            var algorithm = algorithmEntry.AsInteger();

            // The coordinate labels are -1/-2 for RSA and -1/-2/-3 for EC2, so a value can only be
            // typed once the key type is known; that is why nothing is interpreted inside the loop.
            parsed = keyType switch
            {
                PasskeyAlgorithm.Ec2KeyType
                    when algorithm == PasskeyAlgorithm.Es256 &&
                         values.TryGetValue(PasskeyAlgorithm.LabelCurve, out var curve) && curve.AsInteger() == PasskeyAlgorithm.P256Curve &&
                         TryBytes(values, PasskeyAlgorithm.LabelXCoordinate, out var x) && x is { Length: 32 } &&
                         TryBytes(values, PasskeyAlgorithm.LabelYCoordinate, out var y) && y is { Length: 32 } =>
                    new CosePublicKey(keyType, algorithm, x, y, null, null),
                PasskeyAlgorithm.RsaKeyType
                    when algorithm is PasskeyAlgorithm.Rs256 or PasskeyAlgorithm.Ps256 &&
                         TryBytes(values, PasskeyAlgorithm.LabelModulus, out var modulus) &&
                         TryBytes(values, PasskeyAlgorithm.LabelExponent, out var exponent) =>
                    new CosePublicKey(keyType, algorithm, null, null, modulus, exponent),
                PasskeyAlgorithm.OkpKeyType
                    when algorithm == PasskeyAlgorithm.EdDsa &&
                         values.TryGetValue(PasskeyAlgorithm.LabelCurve, out var edCurve) && edCurve.AsInteger() == PasskeyAlgorithm.Ed25519Curve &&
                         TryBytes(values, PasskeyAlgorithm.LabelXCoordinate, out var edX) && edX is { Length: 32 } =>
                    new CosePublicKey(keyType, algorithm, edX, null, null, null),
                _ => null
            };
            if (parsed is null)
            {
                return false;
            }

            consumedBytes = reader.Position;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryBytes(
        IReadOnlyDictionary<int, CborValue> values,
        int label,
        out byte[]? payload)
    {
        payload = values.TryGetValue(label, out var entry) ? entry.Bytes : null;
        return payload is not null;
    }

    private static PasskeyKeyMaterial GenerateEc()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Material(
            PasskeyAlgorithm.Es256,
            ec.ExportSubjectPublicKeyInfo(),
            ec.ExportPkcs8PrivateKey());
    }

    private static PasskeyKeyMaterial GenerateRsa(int algorithm)
    {
        using var rsa = RSA.Create(2048);
        return Material(
            algorithm,
            rsa.ExportSubjectPublicKeyInfo(),
            rsa.ExportPkcs8PrivateKey());
    }

    private static PasskeyKeyMaterial Material(int algorithm, byte[] spki, byte[] pkcs8)
    {
        var publicKeyBase64 = Convert.ToBase64String(spki);
        return new PasskeyKeyMaterial(
            algorithm,
            publicKeyBase64,
            BuildCosePublicKey(algorithm, publicKeyBase64),
            Convert.ToBase64String(pkcs8));
    }
}
