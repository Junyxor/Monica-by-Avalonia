using System.Text.Json.Nodes;
using Monica.Core.Bitwarden;
using Monica.Core.Models;

namespace Monica.Data.Mdbx;

public sealed partial class MdbxVaultStore
{
    public async Task<bool> TryAcknowledgeBitwardenMutationAsync(
        LocalMdbxDatabase database,
        BitwardenMutationAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (database.Id != acknowledgement.MdbxDatabaseId || string.IsNullOrWhiteSpace(acknowledgement.MdbxEntryId)) return false;
        // One lease holds the same gate as editor writes across the read, guard and patch.
        using var vault = await OpenAsync(database, cancellationToken);
        var existing = await FindEntryAsync(vault, acknowledgement.MdbxEntryId,
            acknowledgement.IsPassword ? PasswordEntryTypes : SecureEntryTypes, includeDeleted: true, cancellationToken);
        if (existing is null) return false;

        var password = acknowledgement.IsPassword ? DeserializePasswordPayload(existing.PayloadJson, existing.Title)?.Entry : null;
        var item = acknowledgement.IsPassword ? null : DeserializeSecureItemPayloadSnapshot(existing.PayloadJson, existing.Title, existing.EntryType)?.Item;
        var localId = password?.Id ?? item?.Id;
        var vaultId = password?.BitwardenVaultId ?? item?.BitwardenVaultId;
        var cipherId = password?.BitwardenCipherId ?? item?.BitwardenCipherId;
        var deletedAt = password?.DeletedAt ?? item?.DeletedAt;
        if (localId != acknowledgement.LocalId || vaultId != acknowledgement.VaultId ||
            !string.Equals(cipherId, acknowledgement.ExpectedCipherId, StringComparison.Ordinal) ||
            deletedAt == DateTimeOffset.UnixEpoch) return false;

        var root = JsonNode.Parse(existing.PayloadJson)!.AsObject();
        var model = LegacyPayloadOwner(root, acknowledgement.IsPassword ? "entry" : "item", acknowledgement.LocalId);
        var owner = model ?? root;
        var flat = model is null;
        SetMetadata(owner, flat, "bitwarden_cipher_id", "bitwardenCipherId", JsonValue.Create(acknowledgement.CipherId));
        if (acknowledgement.Revision is not null)
            SetMetadata(owner, flat, "bitwarden_revision_date", "bitwardenRevisionDate", JsonValue.Create(acknowledgement.Revision));
        SetMetadata(owner, flat, "bitwarden_local_modified", "bitwardenLocalModified", JsonValue.Create(false));
        if (flat) root["bitwarden_mode"] = true;
        // Preserve every content field and the stored deletion state. No upsert or restore:
        // an item removed during the request must not be recreated by its acknowledgement.
        await vault.UpdateEntryAsync(existing.ProjectId, existing.EntryId, existing.EntryType,
            existing.Title, root.ToJsonString(), cancellationToken);
        return true;
    }

    private static void SetMetadata(JsonObject owner, bool flat, string snake, string camel, JsonNode? value)
    {
        owner.Remove(snake);
        owner.Remove(camel);
        owner[flat ? snake : camel] = value;
    }
}
