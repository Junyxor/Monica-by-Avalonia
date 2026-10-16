using System.Diagnostics;
using System.Text.Json;
using Monica.Core.Models;
using Monica.Data.Mdbx;
using Monica.Platform.Services;

namespace Monica.Tests;

public sealed class MdbxLanSyncTests
{
    private const string Password = "synthetic-lan-test-password";

    [Fact]
    public async Task Real_vaults_exchange_additions_deletions_and_attachments_without_duplicates()
    {
        var bridge = new MdbxUniffiNativeBridge();
        var hostPath = TestTempPaths.CreateFilePath(".mdbx");
        var peerPath = TestTempPaths.CreateFilePath(".mdbx");
        using var host = await bridge.CreateVaultAsync(hostPath, Password, "host", MdbxTigaMode.Multi);
        var project = await host.CreateProjectAsync("Synthetic LAN test");
        var removed = await host.CreateEntryAsync(project.ProjectId, "login", "Remove me", """{"password":"synthetic-old"}""");
        await bridge.CreatePortableBackupAsync(hostPath, peerPath);
        using var peer = await bridge.OpenVaultAsync(peerPath, Password, "peer");
        await host.DeleteEntryAsync(project.ProjectId, removed.EntryId);
        var hostEntry = await host.CreateEntryAsync(project.ProjectId, "login", "Host entry", """{"password":"synthetic-host"}""");
        var peerEntry = await peer.CreateEntryAsync(project.ProjectId, "login", "Peer entry", """{"password":"synthetic-peer"}""");
        byte[] attachmentBytes = [0, 1, 2, 3, 127, 128, 254, 255];
        var attachment = await peer.CreateAttachmentAsync(project.ProjectId, peerEntry.EntryId, "synthetic.bin", "application/octet-stream", attachmentBytes);

        var hostOut = TestTempPaths.CreateDirectoryPath();
        var peerOut = TestTempPaths.CreateDirectoryPath();
        var hostSync = Assert.IsAssignableFrom<IMdbxNativeLanSync>(host);
        var peerSync = Assert.IsAssignableFrom<IMdbxNativeLanSync>(peer);
        var bundle = await hostSync.ExportLanBundleAsync(Path.Combine(hostOut, "changes.mdbxsync"), CancellationToken.None);
        await peerSync.ExportLanBundleAsync(Path.Combine(peerOut, "changes.mdbxsync"), CancellationToken.None);

        await ExchangeAsync(bundle.VaultId, hostOut, peerOut,
            async incoming => { await hostSync.ApplyLanBundleAsync(Path.Combine(incoming, "changes.mdbxsync"), CancellationToken.None); },
            async incoming => { await peerSync.ApplyLanBundleAsync(Path.Combine(incoming, "changes.mdbxsync"), CancellationToken.None); });

        foreach (var vault in new[] { host, peer })
        {
            var entries = await vault.ListEntriesAsync(project.ProjectId);
            Assert.Equal(new[] { hostEntry.EntryId, peerEntry.EntryId }.Order(), entries.Select(entry => entry.EntryId).Order());
            Assert.Equal(removed.EntryId, Assert.Single(await vault.ListDeletedEntriesAsync(project.ProjectId)).EntryId);
            Assert.Equal(attachmentBytes, await vault.ReadAttachmentContentAsync(attachment.AttachmentId));
        }
        var repeated = await hostSync.ApplyLanBundleAsync(Path.Combine(peerOut, "changes.mdbxsync"), CancellationToken.None);
        Assert.Equal(0U, repeated.Applied);
        Assert.Equal(0U, repeated.Conflicts);
        Assert.Equal(2, (await host.ListEntriesAsync(project.ProjectId)).Count);
    }

    [Fact]
    public async Task First_receiver_imports_the_original_vault_identity_and_requires_its_password()
    {
        var bridge = new MdbxUniffiNativeBridge();
        var hostPath = TestTempPaths.CreateFilePath(".mdbx");
        using var host = await bridge.CreateVaultAsync(hostPath, Password, "host", MdbxTigaMode.Multi);
        var project = await host.CreateProjectAsync("Bootstrap");
        var entry = await host.CreateEntryAsync(project.ProjectId, "login", "Synthetic entry", """{"password":"synthetic"}""");
        var outgoing = TestTempPaths.CreateDirectoryPath();
        var bundle = await ((IMdbxNativeLanSync)host).ExportLanBundleAsync(Path.Combine(outgoing, "changes.mdbxsync"), CancellationToken.None);
        await bridge.CreatePortableBackupAsync(hostPath, Path.Combine(outgoing, "vault.mdbx"));
        var importedPath = TestTempPaths.CreateFilePath(".mdbx");

        await ExchangeAsync(bundle.VaultId, outgoing, null,
            incoming => { Assert.Empty(Directory.EnumerateFiles(incoming)); return Task.CompletedTask; },
            async incoming =>
            {
                File.Copy(Path.Combine(incoming, "vault.mdbx"), importedPath);
                await Assert.ThrowsAsync<MdbxSnapshotException>(() => bridge.ValidateSnapshotAsync(importedPath, "incorrect-synthetic-password"));
                var validated = await bridge.ValidateSnapshotAsync(importedPath, Password);
                Assert.Equal(bundle.VaultId, validated.VaultId);
                using var imported = await bridge.OpenVaultAsync(importedPath, Password, "new-peer");
                await ((IMdbxNativeLanSync)imported).ApplyLanBundleAsync(Path.Combine(incoming, "changes.mdbxsync"), CancellationToken.None);
                Assert.Equal(entry.EntryId, Assert.Single(await imported.ListEntriesAsync(project.ProjectId)).EntryId);
            });
    }

    [Fact]
    public async Task Concurrent_edits_are_reported_as_conflicts()
    {
        var bridge = new MdbxUniffiNativeBridge();
        var hostPath = TestTempPaths.CreateFilePath(".mdbx");
        var peerPath = TestTempPaths.CreateFilePath(".mdbx");
        using var host = await bridge.CreateVaultAsync(hostPath, Password, "host", MdbxTigaMode.Multi);
        var project = await host.CreateProjectAsync("Conflict test");
        var entry = await host.CreateEntryAsync(project.ProjectId, "login", "Original", """{"password":"synthetic-original"}""");
        await bridge.CreatePortableBackupAsync(hostPath, peerPath);
        using var peer = await bridge.OpenVaultAsync(peerPath, Password, "peer");
        await host.UpdateEntryAsync(project.ProjectId, entry.EntryId, "login", "Host edit", """{"password":"synthetic-host"}""");
        await peer.UpdateEntryAsync(project.ProjectId, entry.EntryId, "login", "Peer edit", """{"password":"synthetic-peer"}""");
        var path = TestTempPaths.CreateFilePath(".mdbxsync");
        await ((IMdbxNativeLanSync)peer).ExportLanBundleAsync(path, CancellationToken.None);
        var result = await ((IMdbxNativeLanSync)host).ApplyLanBundleAsync(path, CancellationToken.None);
        Assert.True(result.Conflicts > 0);
        Assert.Equal(entry.EntryId, Assert.Single(await host.ListEntriesAsync(project.ProjectId)).EntryId);
    }

    private static async Task ExchangeAsync(string vaultId, string hostOut, string? peerOut,
        Func<string, Task> mergeHost, Func<string, Task> mergePeer)
    {
        var hostIn = TestTempPaths.CreateDirectoryPath();
        var peerIn = TestTempPaths.CreateDirectoryPath();
        var host = LanSyncNative.Request(new { operation = "start", vault_id = vaultId, outgoing_directory = hostOut, incoming_directory = hostIn });
        var hostHandle = host.GetProperty("handle").GetUInt64();
        ulong? peerHandle = null;
        try
        {
            var invitation = $"monica-lan:1:127.0.0.1:{host.GetProperty("port").GetInt32()}:{host.GetProperty("code").GetString()}";
            var peer = LanSyncNative.Request(new
            {
                operation = "connect",
                invitation,
                vault_id = peerOut is null ? "" : vaultId,
                outgoing_directory = peerOut,
                incoming_directory = peerIn
            });
            peerHandle = peer.GetProperty("handle").GetUInt64();
            await Task.WhenAll(FinishAsync(hostHandle, hostIn, mergeHost), FinishAsync(peerHandle.Value, peerIn, mergePeer));
        }
        finally
        {
            foreach (var handle in new ulong?[] { hostHandle, peerHandle }.OfType<ulong>())
            {
                LanSyncNative.Request(new { operation = "cancel", handle });
                var deadline = Stopwatch.StartNew();
                while (Phase(handle) is not ("completed" or "cancelled" or "failed") && deadline.Elapsed < TimeSpan.FromSeconds(8))
                    await Task.Delay(20);
                LanSyncNative.Request(new { operation = "release", handle });
            }
        }
    }

    private static string? Phase(ulong handle) => LanSyncNative.Request(new { operation = "progress", handle }).GetProperty("phase").GetString();

    private static async Task FinishAsync(ulong handle, string incoming, Func<string, Task> merge)
    {
        var timer = Stopwatch.StartNew();
        var applied = false;
        while (timer.Elapsed < TimeSpan.FromSeconds(30))
        {
            var progress = LanSyncNative.Request(new { operation = "progress", handle });
            var phase = progress.GetProperty("phase").GetString();
            if (phase == "completed") { Assert.True(applied); return; }
            Assert.False(phase is "failed" or "cancelled", $"Transport failed: {progress.GetProperty("error")}");
            if (phase == "received" && !applied)
            {
                try { await merge(incoming); }
                catch { LanSyncNative.Request(new { operation = "acknowledge", handle, success = false }); throw; }
                applied = true;
                LanSyncNative.Request(new { operation = "acknowledge", handle, success = true });
            }
            await Task.Delay(20);
        }
        Assert.Fail("Native LAN synchronization did not finish within 30 seconds.");
    }
}
