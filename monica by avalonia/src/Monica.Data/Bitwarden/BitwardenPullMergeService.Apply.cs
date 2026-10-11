using System.Text.Json;
using Monica.Core.Bitwarden;
using Monica.Core.Models;

namespace Monica.Data.Bitwarden;

public sealed partial class BitwardenPullMergeService
{
    private async Task<LocalContext> LoadLocalContextAsync(
        long vaultId,
        CancellationToken cancellationToken)
    {
        var passwords = (await repository.GetPasswordsAsync(
                includeDeleted: true,
                includeArchived: true,
                cancellationToken))
            .Where(entry => entry.BitwardenVaultId == vaultId && entry.BitwardenCipherId is not null)
            .ToList();
        var secureItems = (await repository.GetSecureItemsAsync(
                itemType: null,
                includeDeleted: true,
                cancellationToken))
            .Where(item => item.BitwardenVaultId == vaultId && item.BitwardenCipherId is not null)
            .ToList();
        var passwordIds = passwords.Select(entry => entry.Id).ToArray();
        var customFields = await repository.GetCustomFieldsByEntryIdsAsync(passwordIds, cancellationToken);
        var histories = await repository.GetPasswordHistoryByEntryIdsAsync(passwordIds, cancellationToken);
        // The same projection the upload queue judges drift with: a row filed under one of this vault's
        // folders is described to the merge by the folder its local category resolves to, so a move that is
        // still waiting to be uploaded reads as the local difference it is rather than as a remote change to
        // overwrite.
        var boundCategories = BitwardenLocalFolderProjection.BoundCategories(
            await folderStore.GetAsync(vaultId, cancellationToken));
        var baseline = await syncStateStore.GetPayloadHashesAsync(vaultId, cancellationToken);
        var references = new List<BitwardenLocalCipherReference>(passwords.Count + secureItems.Count);

        foreach (var stored in passwords)
        {
            var entry = BitwardenLocalFolderProjection.Project(stored, boundCategories);
            var fields = customFields.GetValueOrDefault(entry.Id) ?? [];
            var history = histories.GetValueOrDefault(entry.Id) ?? [];
            var payloadHash = BitwardenPayloadFingerprint.ForPassword(entry, fields, history);
            references.Add(new(
                entry.Id,
                entry.BitwardenCipherId!,
                entry.BitwardenFolderId,
                entry.BitwardenRevisionDate,
                entry.BitwardenCipherType,
                entry.IsDeleted,
                entry.BitwardenLocalModified || HasLocalDrift(baseline, entry.BitwardenCipherId!, payloadHash, entry.IsDeleted),
                payloadHash,
                "password",
                entry.UpdatedAt));
        }

        foreach (var stored in secureItems)
        {
            var item = BitwardenLocalFolderProjection.Project(stored, boundCategories);
            var payloadHash = BitwardenPayloadFingerprint.ForSecureItem(item);
            references.Add(new(
                item.Id,
                item.BitwardenCipherId!,
                item.BitwardenFolderId,
                item.BitwardenRevisionDate,
                ToCipherType(item.ItemType),
                item.IsDeleted,
                item.BitwardenLocalModified || HasLocalDrift(baseline, item.BitwardenCipherId!, payloadHash, item.IsDeleted),
                payloadHash,
                "secure-item",
                item.UpdatedAt));
        }

        return new LocalContext(passwords, secureItems, customFields, histories, references);
    }

    private static bool HasLocalDrift(
        IReadOnlyDictionary<string, string> baseline,
        string cipherId,
        string payloadHash,
        bool isDeleted) =>
        baseline.TryGetValue(cipherId, out var syncedHash) &&
        !string.Equals(syncedHash, payloadHash, StringComparison.Ordinal) &&
        // Ordinary editor saves do not set the dirty flag. Use the same content baseline
        // as the upload queue, including its exception for a settled remote trash entry.
        !(isDeleted && BitwardenPayloadFingerprint.IsRemoteDeletionMarker(syncedHash));

    private async Task ApplyActiveRemoteAsync(
        long vaultId,
        LocalContext local,
        long? localId,
        BitwardenDecodedCipher decoded,
        IReadOnlyDictionary<string, long> folderBindings,
        CancellationToken cancellationToken)
    {
        long? categoryId = decoded.Metadata.FolderId is { } folderId &&
                           folderBindings.TryGetValue(folderId, out var boundCategoryId)
            ? boundCategoryId
            : null;
        if (decoded.Metadata.CipherType is 1 or 5)
        {
            var entry = NormalizePassword(decoded.Password!, decoded.Metadata, vaultId, categoryId);
            entry.Id = localId ?? 0;
            if (localId is not null && FindPassword(local, decoded.Metadata.CipherId) is { } previous)
            {
                // The native UUID, rather than the numeric local id, identifies the existing
                // record. Losing it on a pull creates a duplicate instead of applying an edit.
                entry.MdbxDatabaseId = previous.MdbxDatabaseId;
                entry.MdbxFolderId = previous.MdbxFolderId;
            }
            await repository.SavePasswordAsync(entry, cancellationToken);
            await repository.ReplaceCustomFieldsAsync(entry.Id, decoded.CustomFields, cancellationToken);
            await repository.ClearPasswordHistoryAsync(entry.Id, cancellationToken);
            foreach (var history in decoded.PasswordHistory)
            {
                await repository.SavePasswordHistoryAsync(new PasswordHistoryEntry
                {
                    EntryId = entry.Id,
                    Password = history.Password,
                    LastUsedAt = history.LastUsedAt
                }, cancellationToken);
            }

            return;
        }

        var item = NormalizeSecureItem(decoded.SecureItem!, decoded.Metadata, vaultId, categoryId);
        item.Id = localId ?? 0;
        if (localId is not null)
        {
            var previous = FindSecureItem(local, decoded.Metadata.CipherId);
            item.MdbxDatabaseId = previous.MdbxDatabaseId;
            item.MdbxFolderId = previous.MdbxFolderId;
        }
        await repository.SaveSecureItemAsync(item, cancellationToken);
    }

    private async Task ApplyRemoteDeletionAsync(
        LocalContext local,
        BitwardenMergeDecision decision,
        CancellationToken cancellationToken)
    {
        if (decision.LocalId is null)
        {
            throw new BitwardenProtocolException("A remote deletion has no local identity.");
        }

        var password = FindPassword(local, decision.CipherId);
        if (password is not null)
        {
            await repository.SoftDeletePasswordAsync(password.Id, cancellationToken);
            await AcknowledgeAsync(password, decision.RemoteRevisionDate, cancellationToken);
            return;
        }

        var secureItem = FindSecureItem(local, decision.CipherId);
        await repository.SoftDeleteSecureItemAsync(secureItem.Id, cancellationToken);
        await AcknowledgeAsync(secureItem, decision.RemoteRevisionDate, cancellationToken);
    }

    private async Task MarkLocalCleanAsync(
        LocalContext local,
        BitwardenMergeDecision decision,
        CancellationToken cancellationToken)
    {
        var password = FindPassword(local, decision.CipherId);
        if (password is not null)
        {
            await AcknowledgeAsync(password, decision.RemoteRevisionDate, cancellationToken);
            return;
        }

        var item = FindSecureItem(local, decision.CipherId);
        await AcknowledgeAsync(item, decision.RemoteRevisionDate, cancellationToken);
    }

    private Task<bool> AcknowledgeAsync(PasswordEntry entry, string? revision, CancellationToken token) =>
        repository.TryAcknowledgeBitwardenMutationAsync(new(true, entry.Id, entry.BitwardenVaultId!.Value,
            entry.BitwardenCipherId, entry.BitwardenCipherId!, revision, entry.MdbxDatabaseId, entry.MdbxFolderId), token);

    private Task<bool> AcknowledgeAsync(SecureItem item, string? revision, CancellationToken token) =>
        repository.TryAcknowledgeBitwardenMutationAsync(new(false, item.Id, item.BitwardenVaultId!.Value,
            item.BitwardenCipherId, item.BitwardenCipherId!, revision, item.MdbxDatabaseId, item.MdbxFolderId), token);

    private async Task RestoreLocalIfDeletedAsync(LocalContext local, BitwardenMergeDecision decision, CancellationToken token)
    {
        // Native storage owns its recycle-bin state separately from the payload. Saving an
        // active model alone cannot restore a trashed native record.
        if (FindPassword(local, decision.CipherId) is { } password)
        {
            if (password.IsDeleted) await repository.RestorePasswordAsync(password.Id, token);
        }
        else if (FindSecureItem(local, decision.CipherId) is { IsDeleted: true } item)
        {
            await repository.RestoreSecureItemAsync(item.Id, token);
        }
    }

    // The two local tables number their rows independently, so resolving a row by its id alone can land
    // on the other table's row. Measured against a real server: trashing a note's cipher threw a live
    // login into the recycle bin and left the note's own row stuck on a stale revision. Cipher ids are
    // unique across both tables, so they are the only identity safe to locate a row with.
    private static PasswordEntry? FindPassword(LocalContext local, string cipherId) =>
        local.Passwords.SingleOrDefault(entry =>
            string.Equals(entry.BitwardenCipherId, cipherId, StringComparison.Ordinal));

    private static SecureItem FindSecureItem(LocalContext local, string cipherId) =>
        local.SecureItems.SingleOrDefault(item =>
            string.Equals(item.BitwardenCipherId, cipherId, StringComparison.Ordinal))
        ?? throw new BitwardenProtocolException($"No local secure item is bound to cipher {cipherId}.");

    private async Task SaveConflictBackupAsync(
        long vaultId,
        LocalContext local,
        BitwardenMergeDecision decision,
        CancellationToken cancellationToken)
    {
        string payload;
        string itemKind;
        long localItemId;
        var password = FindPassword(local, decision.CipherId);
        if (password is not null)
        {
            payload = JsonSerializer.Serialize(new
            {
                password,
                customFields = local.CustomFields.GetValueOrDefault(password.Id) ?? [],
                passwordHistory = local.Histories.GetValueOrDefault(password.Id) ?? []
            });
            itemKind = "password";
            localItemId = password.Id;
        }
        else
        {
            var secureItem = FindSecureItem(local, decision.CipherId);
            payload = JsonSerializer.Serialize(new { secureItem });
            itemKind = "secure-item";
            localItemId = secureItem.Id;
        }

        await conflictStore.SaveAsync(new BitwardenConflictBackup(
            0,
            vaultId,
            decision.CipherId,
            itemKind,
            localItemId,
            decision.LocalRevisionDate,
            decision.RemoteRevisionDate,
            payload,
            decision.Reason,
            DateTimeOffset.UtcNow), cancellationToken);
    }

    private static PasswordEntry NormalizePassword(
        PasswordEntry source,
        BitwardenRemoteCipherMetadata metadata,
        long? vaultId,
        long? categoryId)
    {
        var entry = source.CreateDetachedCopy();
        entry.BitwardenVaultId = vaultId;
        entry.BitwardenCipherId = metadata.CipherId;
        entry.BitwardenFolderId = metadata.FolderId;
        entry.BitwardenRevisionDate = metadata.RevisionDate;
        entry.BitwardenCipherType = metadata.CipherType;
        entry.BitwardenLocalModified = false;
        entry.CategoryId = categoryId;
        entry.IsDeleted = metadata.IsDeleted;
        entry.DeletedAt = metadata.IsDeleted ? metadata.UpdatedAt ?? DateTimeOffset.UtcNow : null;
        return entry;
    }

    private static SecureItem NormalizeSecureItem(
        SecureItem source,
        BitwardenRemoteCipherMetadata metadata,
        long? vaultId,
        long? categoryId)
    {
        var item = source.CreateDetachedCopy();
        item.BitwardenVaultId = vaultId;
        item.BitwardenCipherId = metadata.CipherId;
        item.BitwardenFolderId = metadata.FolderId;
        item.BitwardenRevisionDate = metadata.RevisionDate;
        item.BitwardenLocalModified = false;
        item.CategoryId = categoryId;
        item.IsDeleted = metadata.IsDeleted;
        item.DeletedAt = metadata.IsDeleted ? metadata.UpdatedAt ?? DateTimeOffset.UtcNow : null;
        return item;
    }

    private static int ToCipherType(VaultItemType itemType) => itemType switch
    {
        VaultItemType.Note => 2,
        VaultItemType.BankCard => 3,
        VaultItemType.Document => 4,
        _ => 2
    };

    private sealed record LocalContext(
        IReadOnlyList<PasswordEntry> Passwords,
        IReadOnlyList<SecureItem> SecureItems,
        IReadOnlyDictionary<long, IReadOnlyList<CustomField>> CustomFields,
        IReadOnlyDictionary<long, IReadOnlyList<PasswordHistoryEntry>> Histories,
        IReadOnlyList<BitwardenLocalCipherReference> References);
}
