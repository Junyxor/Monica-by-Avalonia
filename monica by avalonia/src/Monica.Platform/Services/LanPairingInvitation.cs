using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Monica.Platform.Services;

/// <summary>Parse display fields from a complete invitation. Rust still authenticates the connection.</summary>
public sealed record LanPairingInvitation(string Address, int Port, string PairingCode, int Version = 1)
{
    public IReadOnlyList<string> Addresses => Address.Split(',');
    public string Link => $"monica-lan:{Version}:{Address}:{Port}:{PairingCode}";
    public override string ToString() => $"LanPairingInvitation({Address}:{Port})";

    public static LanPairingInvitation? Parse(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 512) return null;
        var parts = value.Split(':');
        if (parts.Length != 5 || parts[0] != "monica-lan" || parts[1] is not ("1" or "2")) return null;
        var addresses = parts[2].Split(',');
        if (addresses.Length is < 1 or > 8 || addresses.Distinct().Count() != addresses.Length || (parts[1] == "1" && addresses.Length != 1)) return null;
        foreach (var candidate in addresses)
        {
            if (!IPAddress.TryParse(candidate, out var address) || address.AddressFamily != AddressFamily.InterNetwork || address.ToString() != candidate) return null;
            var ip = address.GetAddressBytes();
            var local = ip[0] is 10 or 127 || (ip[0] == 172 && ip[1] is >= 16 and <= 31) ||
                        (ip[0] == 192 && ip[1] == 168) || (ip[0] == 169 && ip[1] == 254);
            if (!local) return null;
        }
        if (!parts[3].All(char.IsAsciiDigit) ||
            !ushort.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port == 0) return null;
        var code = parts[4];
        if (code.Length != 43 || code.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))) return null;
        Span<byte> key = stackalloc byte[32];
        try
        {
            if (!Convert.TryFromBase64String(code.Replace('-', '+').Replace('_', '/') + "=", key, out var count) || count != 32 ||
                Convert.ToBase64String(key).TrimEnd('=').Replace('+', '-').Replace('/', '_') != code) return null;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        return new LanPairingInvitation(parts[2], port, code, int.Parse(parts[1], CultureInfo.InvariantCulture));
    }
}
