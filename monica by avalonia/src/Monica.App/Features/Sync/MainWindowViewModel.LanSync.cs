using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monica.App.Services;
using Monica.Core.Models;
using Monica.Data.Repositories;
using Monica.Platform.Services;
using QRCoder;

namespace Monica.App.ViewModels;

public sealed record LanPeerDisplay(string Name, string Address, int Port)
{
    public string Display => Name == Address ? $"{Address}:{Port}" : $"{Name} · {Address}";
}

public sealed record DeviceSyncFrequencyChoice(int Seconds, string Label);
public sealed record DeviceSyncModeChoice(DeviceSyncMode Mode, string Label);

public sealed partial class DeviceSyncPeerDisplay(string deviceId) : ObservableObject
{
    public string DeviceId { get; } = deviceId;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _lastSynced = "";
}

public sealed partial class MainWindowViewModel
{
    private DeviceSyncCoordinator? _deviceSync;
    private bool _applyingLanInvitation;
    private bool _applyingDeviceSyncState;
    private bool _deviceRefreshPending;
    private bool _deviceRefreshing;
    private int _displayedDeviceSyncInterval = -1;
    [ObservableProperty] private bool _isLanBusy;
    [ObservableProperty] private LanPeerDisplay? _selectedLanPeer;
    [ObservableProperty] private string _lanInvitation = "";
    [ObservableProperty] private string _lanPairingCode = "";
    [ObservableProperty] private bool _lanPairingAutoFilled;
    [ObservableProperty] private string _lanHostCode = "";
    [ObservableProperty] private string _lanStatusText = "";
    [ObservableProperty] private string _lanErrorText = "";
    [ObservableProperty] private string _lanDiagnosticText = "";
    [ObservableProperty] private string _lanSelectedHostLink = "";
    [ObservableProperty] private Bitmap? _lanQrCode;
    [ObservableProperty] private double _lanProgress;
    [ObservableProperty] private bool _lanProgressIndeterminate = true;
    [ObservableProperty] private bool _deviceSyncEnabled = true;
    [ObservableProperty] private DeviceSyncFrequencyChoice? _selectedDeviceSyncFrequency;
    [ObservableProperty] private DeviceSyncModeChoice? _selectedDeviceSyncMode;
    [ObservableProperty] private bool _isDeviceSyncPairing;
    [ObservableProperty] private bool _isCustomDeviceSyncInterval;
    [ObservableProperty] private string _deviceSyncCustomMinutes = "10";
    public ObservableCollection<LanPeerDisplay> LanPeers { get; } = [];
    public ObservableCollection<string> LanHostLinks { get; } = [];
    public ObservableCollection<DeviceSyncFrequencyChoice> DeviceSyncFrequencies { get; } = [];
    public ObservableCollection<DeviceSyncModeChoice> DeviceSyncModes { get; } = [];
    public ObservableCollection<DeviceSyncPeerDisplay> SyncedDevices { get; } = [];

    private void InitializeDeviceSync(DeviceSyncCoordinator? coordinator)
    {
        _deviceSync = coordinator;
        if (coordinator is not null)
        {
            coordinator.Changed += (_, state) => _viewModelDispatcher.Post(() => ApplyDeviceSyncState(state));
            coordinator.PasswordsChanged += (_, _) => _viewModelDispatcher.Post(() =>
            {
                _deviceRefreshPending = true;
                _ = RefreshDevicePasswordsAsync();
            });
            coordinator.CanApply = () => !HasActiveVaultEdits() && !OpenNoteTabs.Any(tab => tab.IsDirty);
        }
        _localization.PropertyChanged += (_, _) =>
        {
            RefreshDeviceSyncChoices();
            if (_deviceSync is not null) ApplyDeviceSyncState(_deviceSync.State);
        };
        RefreshDeviceSyncChoices();
    }

    private void RefreshDeviceSyncChoices()
    {
        _applyingDeviceSyncState = true;
        try
        {
            DeviceSyncFrequencies.Clear();
            DeviceSyncModes.Clear();
            DeviceSyncModes.Add(new(DeviceSyncMode.Bidirectional, L.Get("DeviceSyncBidirectional")));
            DeviceSyncModes.Add(new(DeviceSyncMode.SendOnly, L.Get("DeviceSyncSendOnly")));
            DeviceSyncModes.Add(new(DeviceSyncMode.ReceiveOnly, L.Get("DeviceSyncReceiveOnly")));
            SelectedDeviceSyncMode = DeviceSyncModes.First(item => item.Mode == (_deviceSync?.State.Mode ?? DeviceSyncMode.Bidirectional));
            foreach (var seconds in new[] { 0, 60, 300, 900, 1800, -1 })
                DeviceSyncFrequencies.Add(new(seconds, seconds == 0 ? L.Get("DeviceSyncRealtime") :
                    seconds == -1 ? L.Get("DeviceSyncCustom") : L.Format("DeviceSyncEveryMinutes", seconds / 60)));
            var current = _deviceSync?.State.IntervalSeconds ?? 0;
            _displayedDeviceSyncInterval = current;
            SelectedDeviceSyncFrequency = DeviceSyncFrequencies.FirstOrDefault(item => item.Seconds == current) ?? DeviceSyncFrequencies.Last();
            IsCustomDeviceSyncInterval = SelectedDeviceSyncFrequency.Seconds == -1;
        }
        finally { _applyingDeviceSyncState = false; }
    }

    internal void ApplyDeviceSyncState(DeviceSyncState state)
    {
        _applyingDeviceSyncState = true;
        try
        {
            DeviceSyncEnabled = state.Enabled;
            IsDeviceSyncPairing = state.Pairing;
            SelectedDeviceSyncMode = DeviceSyncModes.First(item => item.Mode == state.Mode);
            if (_displayedDeviceSyncInterval != state.IntervalSeconds || SelectedDeviceSyncFrequency is null)
            {
                _displayedDeviceSyncInterval = state.IntervalSeconds;
                SelectedDeviceSyncFrequency = DeviceSyncFrequencies.FirstOrDefault(item => item.Seconds == state.IntervalSeconds) ?? DeviceSyncFrequencies.LastOrDefault();
                IsCustomDeviceSyncInterval = SelectedDeviceSyncFrequency?.Seconds == -1;
                if (IsCustomDeviceSyncInterval) DeviceSyncCustomMinutes = (state.IntervalSeconds / 60).ToString();
            }
            foreach (var removed in SyncedDevices.Where(item => state.Peers.All(peer => peer.DeviceId != item.DeviceId)).ToArray()) SyncedDevices.Remove(removed);
            foreach (var peer in state.Peers)
            {
                var display = SyncedDevices.FirstOrDefault(item => item.DeviceId == peer.DeviceId);
                if (display is null) { display = new(peer.DeviceId); SyncedDevices.Add(display); }
                display.Name = peer.Name;
                display.Status = L.Get(peer.Status switch
                {
                    "syncing" => "DeviceSyncSyncing", "online" => "DeviceSyncOnline", "scheduled" => "DeviceSyncScheduled", "paused" => "DeviceSyncPaused",
                    "locked" => "DeviceSyncLocked", "waiting" => "DeviceSyncWaiting", "first_sync_pending" => "DeviceSyncFirstPending",
                    "preparation_failed" => "DeviceSyncPreparationFailed", _ => "DeviceSyncOffline"
                });
                display.LastSynced = peer.LastSyncedAt is { } time ? L.Format("DeviceSyncLast", time.ToLocalTime().ToString("g")) : "";
            }
            IsLanBusy = state.Pairing || state.Phase is "preparing" or "sending" or "receiving" or "transferring" or "applying" or "authenticating" or "connecting" or "checking" or "confirming";
            var progress = state.Progress;
            var total = progress?.TotalBytes > 0 ? progress.TotalBytes : progress?.Total ?? 0;
            var completed = progress?.TotalBytes > 0 ? progress.Bytes : progress?.Completed ?? 0;
            LanProgressIndeterminate = total <= 0;
            LanProgress = total > 0 ? Math.Clamp(100.0 * completed / total, 0, 100) : 0;
            LanHostCode = IsUnlocked ? state.Code : "";
            var links = IsUnlocked ? state.Invitations ?? [] : [];
            if (!LanHostLinks.SequenceEqual(links))
            {
                ReplaceItems(LanHostLinks, links);
                LanSelectedHostLink = links.FirstOrDefault() ?? "";
            }
            LanStatusText = L.Get(state.Phase switch
            {
                "waiting" when state.Pairing => "DeviceSyncScanOther",
                "completed" => "DeviceSyncPaired", "locked" => "DeviceSyncLocked", "paused" => "DeviceSyncPaused",
                "idle" => "", "failed" => "", "cancelled" => "LanCancelled", _ => "DeviceSyncSyncing"
            });
            if (IsLanBusy && progress is not null)
                LanStatusText = L.Get(progress.Stage switch { "preparing" => "DeviceSyncPreparing", "applying" => "DeviceSyncApplying",
                    "sending" => "DeviceSyncSending", "receiving" => "DeviceSyncReceiving", "transferring" => "DeviceSyncTransferring", _ => "DeviceSyncSyncing" }) +
                    (total <= 0 ? "" : progress.TotalBytes > 0 ? $" · {completed / 1048576.0:F1} / {total / 1048576.0:F1} MB" : $" · {completed:N0} / {total:N0}");
            LanErrorText = state.Error is null ? "" : DeviceSyncError(state.Error, state.FailureStage);
            LanDiagnosticText = state.Error is null ? "" : L.Format("DeviceSyncDiagnosticFormat", state.FailureStage, state.Error,
                state.FailureKind, state.ConnectionAttempts, string.IsNullOrEmpty(state.FailureOperation) ? "-" : state.FailureOperation,
                state.FailureRequestBytes);
            if (state.Error is not null) LanDiagnosticText = L.Format("DeviceSyncDirectionDiagnostic", state.Mode.ToWire()) + Environment.NewLine + LanDiagnosticText;
            if (state.Phase is "locked" or "completed") { LanInvitation = ""; LanPairingCode = ""; }
        }
        finally { _applyingDeviceSyncState = false; }
    }

    partial void OnDeviceSyncEnabledChanged(bool value)
    {
        if (!_applyingDeviceSyncState && _deviceSync is not null) _ = RunDeviceActionAsync(() => _deviceSync.SetEnabledAsync(value));
    }

    partial void OnSelectedDeviceSyncModeChanged(DeviceSyncModeChoice? value)
    {
        if (!_applyingDeviceSyncState && value is not null && _deviceSync is not null)
            _ = RunDeviceActionAsync(() => _deviceSync.SetModeAsync(value.Mode));
    }

    partial void OnSelectedDeviceSyncFrequencyChanged(DeviceSyncFrequencyChoice? value)
    {
        IsCustomDeviceSyncInterval = value?.Seconds == -1;
        if (!_applyingDeviceSyncState && value is { Seconds: >= 0 } && _deviceSync is not null)
            _ = RunDeviceActionAsync(() => _deviceSync.SetIntervalAsync(value.Seconds));
    }

    [RelayCommand]
    private Task ApplyDeviceSyncIntervalAsync()
    {
        if (!int.TryParse(DeviceSyncCustomMinutes, out var minutes) || minutes is < 1 or > 1440)
        { LanErrorText = L.Get("DeviceSyncMinutesHint"); return Task.CompletedTask; }
        return RunDeviceActionAsync(() => _deviceSync!.SetIntervalAsync(minutes * 60));
    }

    [RelayCommand]
    private Task SyncDevicesNowAsync() => RunDeviceActionAsync(() => _deviceSync!.SyncNowAsync());

    [RelayCommand]
    private async Task ForgetSyncDeviceAsync(DeviceSyncPeerDisplay? peer)
    {
        if (peer is null) return;
        if (await _confirmationDialogService.ConfirmAsync(L.Get("DeviceSyncForget"), L.Get("DeviceSyncForgetHint"), L.Get("DeviceSyncForget"), L.Cancel))
            await RunDeviceActionAsync(() => _deviceSync!.ForgetAsync(peer.DeviceId));
    }

    private async Task RunDeviceActionAsync(Func<Task> action)
    {
        if (!IsUnlocked || _deviceSync is null) { LanErrorText = L.Get("LanUnavailable"); return; }
        try { LanErrorText = ""; await action(); ApplyDeviceSyncState(_deviceSync.State); }
        catch (OperationCanceledException) { }
        catch (Exception error) { LanErrorText = DeviceSyncError(error is LanSyncException lan ? lan.Code : "device_sync_failed"); }
    }

    partial void OnLanInvitationChanged(string value)
    {
        if (_applyingLanInvitation) return;
        _applyingLanInvitation = true;
        try
        {
            var parsed = LanPairingInvitation.Parse(value);
            if (parsed is null)
            {
                SelectedLanPeer = null; LanPairingCode = ""; LanPairingAutoFilled = false;
                return;
            }
            var peer = LanPeers.FirstOrDefault(item => parsed.Addresses.Contains(item.Address) && item.Port == parsed.Port);
            if (peer is null) { peer = new(parsed.Address, parsed.Address, parsed.Port); LanPeers.Add(peer); }
            SelectedLanPeer = peer; LanPairingCode = parsed.PairingCode; LanPairingAutoFilled = true;
            LanInvitation = parsed.Link; LanErrorText = "";
        }
        finally { _applyingLanInvitation = false; }
    }

    partial void OnSelectedLanPeerChanged(LanPeerDisplay? value)
    {
        if (_applyingLanInvitation) return;
        LanPairingAutoFilled = false; LanInvitation = ""; LanPairingCode = "";
    }

    partial void OnLanSelectedHostLinkChanged(string value)
    {
        var previous = LanQrCode; LanQrCode = null; previous?.Dispose();
        if (string.IsNullOrWhiteSpace(value)) return;
        using var data = QRCodeGenerator.GenerateQrCode(value, QRCodeGenerator.ECCLevel.M);
        using var qr = new PngByteQRCode(data);
        using var stream = new MemoryStream(qr.GetGraphic(6));
        LanQrCode = new Bitmap(stream);
    }

    [RelayCommand]
    private Task HostLanSyncAsync() => RunDeviceActionAsync(() => _deviceSync!.PairAsync());

    [RelayCommand]
    private Task ConnectLanSyncAsync()
    {
        var parsed = LanPairingInvitation.Parse(LanInvitation);
        if (parsed?.Version != 2) { LanErrorText = L.Get("DeviceSyncQrInvalid"); return Task.CompletedTask; }
        return RunDeviceActionAsync(() => _deviceSync!.PairAsync(parsed.Link));
    }

    [RelayCommand]
    private Task CancelLanSyncAsync() => RunDeviceActionAsync(() => _deviceSync!.CancelPairingAsync());

    [RelayCommand]
    private Task CopyLanLinkAsync() => string.IsNullOrWhiteSpace(LanSelectedHostLink)
        ? Task.CompletedTask : _clipboardService.SetSensitiveTextAsync(LanSelectedHostLink);

    [RelayCommand]
    private Task CopyLanDiagnosticsAsync() => string.IsNullOrEmpty(LanDiagnosticText)
        ? Task.CompletedTask : _clipboardService.SetSensitiveTextAsync(LanDiagnosticText);

    [RelayCommand]
    private async Task ReadLanQrImageAsync()
    {
        if (IsLanBusy) return;
        try
        {
            var file = await _fileSystemPickerService.OpenBinaryFileAsync(L.Get("LanReadQr"), TotpQrImageFileTypes);
            if (file is null) return;
            string? payload;
            try { payload = await Task.Run(() => TotpQrCodeDecoder.TryDecode(file.Content)); }
            finally { CryptographicOperations.ZeroMemory(file.Content); }
            var invitation = LanPairingInvitation.Parse(payload);
            if (invitation?.Version != 2) { LanErrorText = L.Get("DeviceSyncQrInvalid"); return; }
            LanInvitation = invitation.Link;
            await ConnectLanSyncAsync();
        }
        catch { LanErrorText = L.Get("DeviceSyncQrInvalid"); }
    }

    private void CancelLanSyncAndClearSecrets()
    {
        LanHostCode = ""; LanHostLinks.Clear(); LanSelectedHostLink = "";
        LanInvitation = ""; LanPairingCode = ""; LanPeers.Clear();
    }

    private string DeviceSyncError(string error, string stage = "") => L.Get(error switch
    {
        "connection_failed" or "listen_failed" or "discovery_unavailable" => "DeviceSyncConnectionFailed",
        "pairing_rejected" or "authentication_failed" or "different_device" => "DeviceSyncPairingFailed",
        "peer_merge_failed" => "LanPeerFailed",
        "native_unavailable" or "device_sync_unavailable" => "LanUnavailable",
        "unsupported_protocol" or "invalid_invitation" => "DeviceSyncQrInvalid",
        "pairing_expired" => "LanExpired", "same_device" => "DeviceSyncSameDevice", "too_many_devices" => "DeviceSyncLimit",
        "invalid_password_payload" or "invalid_record" => "DeviceSyncInvalidData",
        "sync_direction_conflict" => "DeviceSyncDirectionConflict",
        "sync_direction_unsupported" => "DeviceSyncDirectionUnsupported",
        "request_too_large" => "DeviceSyncRequestTooLarge",
        _ when error.StartsWith("storage_", StringComparison.Ordinal) => "DeviceSyncStorageFailed",
        _ when stage == "preparing" => "DeviceSyncPrepareFailed",
        _ when stage is "applying" or "received" or "remembering" => "DeviceSyncApplyFailed",
        _ => "DeviceSyncFailed"
    });

    private async Task RefreshDevicePasswordsAsync()
    {
        if (_deviceRefreshing) return;
        _deviceRefreshing = true;
        var token = _vaultSessionService.SessionCancellationToken;
        try
        {
            while (_deviceRefreshPending && IsUnlocked)
            {
                if (IsLoadingVault || EditPasswordCommand.IsRunning || AddPasswordCommand.IsRunning)
                { await Task.Delay(300, token); continue; }
                _deviceRefreshPending = false;
                var version = _vaultLoadVersion;
                var rows = await Task.Run(async () =>
                {
                    try
                    {
                        var passwords = (await _repository.GetPasswordsAsync(includeDeleted: true, includeArchived: true, cancellationToken: token))
                            .Select(item => item.CreateDetachedCopy()).ToArray();
                        var categories = await _repository.GetCategoriesAsync(token);
                        return (passwords, categories);
                    }
                    finally { (_repository as ITransientVaultReadCache)?.ReleaseVaultItemSnapshots(); }
                }, token);
                if (!IsUnlocked || token.IsCancellationRequested) return;
                if (IsLoadingVault || version != _vaultLoadVersion || EditPasswordCommand.IsRunning)
                { _deviceRefreshPending = true; continue; }
                var selected = Passwords.Concat(ArchivedPasswords).Concat(DeletedPasswords).Where(item => item.IsSelected).Select(item => item.Id).ToHashSet();
                var openRow = SelectedVaultRow?.Key;
                using (SuppressVaultTreeRefresh())
                {
                    _passwordCustomFields = new Dictionary<long, IReadOnlyList<CustomField>>();
                    foreach (var row in rows.passwords)
                    {
                        row.IsSelected = selected.Contains(row.Id);
                        TrackPasswordSelection(row);
                        RefreshPasswordTotpDisplay(row);
                    }
                    ReplaceItems(Passwords, rows.passwords.Where(item => !item.IsDeleted && !item.IsArchived));
                    ReplaceItems(ArchivedPasswords, rows.passwords.Where(item => !item.IsDeleted && item.IsArchived));
                    ReplaceItems(DeletedPasswords, rows.passwords.Where(item => item.IsDeleted && item.DeletedAt != DateTimeOffset.UnixEpoch));
                    ReplaceItems(Categories, rows.categories);
                    RefreshPasswordSelectionStateFromPasswords();
                    RaisePasswordCountState();
                }
                ReopenVaultRowAfterReload(openRow);
                if (!string.IsNullOrWhiteSpace(VaultSearchText)) QueuePasswordSearchQuery(VaultSearchText);
                InvalidateSecurityAnalysis();
                await ReloadVaultKeepingSelectionAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch { LanErrorText = L.Get("DeviceSyncRefreshFailed"); }
        finally { _deviceRefreshing = false; }
    }
}
