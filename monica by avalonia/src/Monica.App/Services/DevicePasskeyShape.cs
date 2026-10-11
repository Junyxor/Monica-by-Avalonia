using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Monica.Core.Models;
using Monica.Core.Passkeys;
using Monica.Data.Mdbx;

namespace Monica.App.Services;

internal static class DevicePasskeyShape
{
    public static string Identity(PasskeyEntry entry)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("passkey:" + entry.RpId + ":" + PasskeyCredentialId.Normalize(entry.CredentialId))));
        return $"{hash[..8]}-{hash[8..12]}-{hash[12..16]}-{hash[16..20]}-{hash[20..32]}";
    }

    public static JsonObject Export(PasskeyEntry entry, string? key, bool verify = true)
    {
        var payload = new JsonObject
        {
            ["kind"] = "passkey", ["credential_id"] = entry.CredentialId, ["rp_id"] = entry.RpId, ["rp_name"] = entry.RpName,
            ["user_id"] = entry.UserId, ["user_name"] = entry.UserName, ["user_display_name"] = entry.UserDisplayName,
            ["public_key_algorithm"] = entry.PublicKeyAlgorithm,
            ["public_key"] = Convert.ToBase64String(PasskeyKeyMaterialGenerator.BuildCosePublicKey(entry.PublicKeyAlgorithm, entry.PublicKey)),
            ["private_key_alias"] = key ?? "", ["key_status"] = string.IsNullOrEmpty(key) ? "reference" : "portable",
            ["created_at"] = entry.CreatedAt.ToUnixTimeMilliseconds(), ["last_used_at"] = entry.LastUsedAt.ToUnixTimeMilliseconds(),
            ["use_count"] = entry.UseCount, ["icon_url"] = entry.IconUrl, ["is_discoverable"] = entry.IsDiscoverable,
            ["is_user_verification_required"] = entry.IsUserVerificationRequired, ["transports"] = entry.Transports,
            ["aaguid"] = entry.Aaguid, ["sign_count"] = 0, ["notes"] = entry.Notes, ["passkey_mode"] = entry.PasskeyMode
        };
        var data = DeviceVaultShape.Normalize(payload.ToJsonString(), entry.RpName, "", "passkey");
        data["folder_path"] = new JsonArray(); data["labels"] = new JsonArray(); data["relations"] = new JsonArray(); data["assets"] = new JsonArray(); data["trashed"] = false;
        if(verify) Verify(data);
        return data;
    }

    public static void Verify(JsonObject data)
    {
        DeviceVaultShape.Validate(data);
        var payload = data["payload"]!.AsObject();
        var algorithm = payload["public_key_algorithm"]!.GetValue<int>();
        var encoded = Convert.FromBase64String(payload["public_key"]!.GetValue<string>());
        var spki = PasskeyKeyMaterialGenerator.TryParseCosePublicKey(encoded, out var cose, out var read) && read == encoded.Length
            ? cose!.ToSubjectPublicKeyInfoBase64() : Convert.ToBase64String(encoded);
        var key = payload["private_key_alias"]?.GetValue<string>() ?? "";
        if (key.Length > 0)
        {
            var challenge = RandomNumberGenerator.GetBytes(32);
            var signature = PasskeyKeyMaterialGenerator.Sign(algorithm, key, challenge);
            if (!PasskeyKeyMaterialGenerator.Verify(algorithm, spki, challenge, signature))
                throw new InvalidDataException("invalid_passkey_material");
        }
        else if (payload["key_status"]?.GetValue<string>() != "reference") throw new InvalidDataException("passkey_key_unavailable");
    }

    public static PasskeyEntry Import(JsonObject data, long id)
    {
        Verify(data);
        var p = data["payload"]!.AsObject();
        string Text(string name, string fallback = "") => p[name]?.GetValue<string>() ?? fallback;
        bool Flag(string name, bool fallback = true) => p[name]?.GetValue<bool>() ?? fallback;
        var encoded = Convert.FromBase64String(Text("public_key"));
        var spki = PasskeyKeyMaterialGenerator.TryParseCosePublicKey(encoded, out var cose, out var read) && read == encoded.Length
            ? cose!.ToSubjectPublicKeyInfoBase64() : Convert.ToBase64String(encoded);
        return new PasskeyEntry
        {
            Id = id, CredentialId = Text("credential_id"), RpId = Text("rp_id"), RpName = Text("rp_name"), UserId = Text("user_id"),
            UserName = Text("user_name"), UserDisplayName = Text("user_display_name"), PublicKeyAlgorithm = p["public_key_algorithm"]!.GetValue<int>(),
            PublicKey = spki, PrivateKeyAlias = Text("private_key_alias"), Notes = Text("notes"), Transports = Text("transports", "internal"),
            Aaguid = Text("aaguid"), PasskeyMode = Text("passkey_mode", "LEGACY"), IsDiscoverable = Flag("is_discoverable"),
            IsUserVerificationRequired = Flag("is_user_verification_required"), SignCount = 0, IsBackedUp = Text("key_status") == "portable",
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(p["created_at"]?.GetValue<long>() ?? 0),
            LastUsedAt = DateTimeOffset.FromUnixTimeMilliseconds(p["last_used_at"]?.GetValue<long>() ?? 0), UseCount = p["use_count"]?.GetValue<int>() ?? 0,
            IconUrl = Text("icon_url")
        };
    }

    public static string Guard(JsonObject data) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(data).ToJsonString())));
    private static JsonNode Canonical(JsonNode value) => value switch
    {
        JsonObject map => new JsonObject(map.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, p.Value is null ? null : Canonical(p.Value)))),
        JsonArray list => new JsonArray(list.Select(v => v is null ? null : Canonical(v)).ToArray()),
        _ => value.DeepClone()
    };
}
