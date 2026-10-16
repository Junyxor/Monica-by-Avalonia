using Dapper;
using Monica.Core.Bitwarden;

namespace Monica.Data.Repositories;

public sealed partial class MonicaRepository
{
    public async Task<bool> TryAcknowledgeBitwardenMutationAsync(
        BitwardenMutationAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        await migrator.MigrateAsync(cancellationToken);
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var table = acknowledgement.IsPassword ? "password_entries" : "secure_items";
        // Keep current content and deletion state, even when the user edited or purged the
        // row during HTTP. The separately recorded payload hash remains the drift baseline.
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            $"""
            UPDATE {table}
            SET bitwarden_cipher_id = @CipherId,
                bitwarden_revision_date = COALESCE(@Revision, bitwarden_revision_date),
                bitwarden_local_modified = 0
            WHERE id = @LocalId AND bitwarden_vault_id = @VaultId
              AND bitwarden_cipher_id IS @ExpectedCipherId
            """,
            acknowledgement,
            cancellationToken: cancellationToken));
        return updated == 1;
    }
}
