using System.Security.Cryptography;
using System.Text.Json;
using Monica.Data;
using Monica.Platform.Services;

namespace Monica.App.Services;

public sealed class DeviceSyncPeer
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string PairId { get; set; } = "";
    public string TrustedKey { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public string LastSentRevision { get; set; } = "";
    public string LastReceivedRevision { get; set; } = "";
    public ulong LastReceivedRequest { get; set; }
    public override string ToString() => $"DeviceSyncPeer({Name})";
}

public sealed class DeviceSyncPreferences
{
    public int Version { get; set; } = 1;
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string ReplicaKey { get; set; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; }
    public ulong SettingsVersion { get; set; }
    public string SettingsWriter { get; set; } = "";
    public ulong SyncRequest { get; set; }
    public List<DeviceSyncPeer> Peers { get; set; } = [];

    public void SetInterval(int seconds)
    {
        if (seconds != 0 && seconds is not (>= 60 and <= 86400)) throw new ArgumentOutOfRangeException(nameof(seconds));
        IntervalSeconds = seconds;
        if (SettingsVersion >= long.MaxValue) throw new InvalidOperationException("device_sync_settings_limit");
        SettingsVersion++;
        SettingsWriter = DeviceId;
    }

    public bool AcceptInterval(int seconds, ulong version, string writer)
    {
        if ((seconds != 0 && seconds is not (>= 60 and <= 86400)) || writer.Length > 64 || version > long.MaxValue) return false;
        if (version < SettingsVersion || (version == SettingsVersion && string.CompareOrdinal(writer, SettingsWriter) <= 0)) return false;
        IntervalSeconds = seconds;
        SettingsVersion = version;
        SettingsWriter = writer;
        return true;
    }
}

public static class DeviceSyncCadence
{
    public static bool IsDue(int intervalSeconds, TimeSpan elapsed, bool changed, bool force = false) =>
        force || (changed && elapsed >= TimeSpan.FromSeconds(intervalSeconds == 0 ? 2 : intervalSeconds));
}

/// <summary>Pairing keys stay outside synchronized vault data, protected by the OS credential service.</summary>
public sealed class DeviceSyncPreferenceStore(ISecretProtector protector, string? path = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = path ?? MonicaAppDataPaths.GetPath("device-sync/peers.protected");

    public async Task<DeviceSyncPreferences> LoadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!File.Exists(_path)) return new DeviceSyncPreferences();
            var protectedText = await File.ReadAllTextAsync(_path, token);
            var plain = await protector.UnprotectAsync(protectedText, token);
            var settings = JsonSerializer.Deserialize<DeviceSyncPreferences>(plain) ?? throw new InvalidDataException("device_sync_preferences_invalid");
            if (settings.Version != 1 || settings.Peers.Count > 8 || settings.DeviceId.Length > 64 ||
                (settings.IntervalSeconds != 0 && settings.IntervalSeconds is not (>= 60 and <= 86400)))
                throw new InvalidDataException("device_sync_preferences_invalid");
            foreach (var peer in settings.Peers)
            {
                if (peer.DeviceId.Length is < 1 or > 64 || peer.PairId.Length is < 1 or > 64 ||
                    LanPairingInvitation.Parse($"monica-lan:1:127.0.0.1:1:{peer.TrustedKey}") is null)
                    throw new InvalidDataException("device_sync_preferences_invalid");
            }
            return settings;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(DeviceSyncPreferences settings, CancellationToken token = default)
    {
        var protectedText = await protector.ProtectAsync(JsonSerializer.Serialize(settings), token);
        await _gate.WaitAsync(token);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.WriteAllTextAsync(temporary, protectedText, token);
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); _gate.Release(); }
    }
}
