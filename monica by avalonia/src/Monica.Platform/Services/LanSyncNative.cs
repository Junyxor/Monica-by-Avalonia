using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Monica.Platform.Services;

public sealed class LanSyncException(string code, string operation = "", int requestBytes = 0) : IOException($"LAN synchronization failed ({code}).")
{
    public string Code { get; } = code;
    public string Operation { get; } = operation;
    public int RequestBytes { get; } = requestBytes;
}

/// <summary>Only bounded transport metadata crosses FFI. Vault artifacts stay in private files.</summary>
public static class LanSyncNative
{
    public const int MaxRequestBytes = 8 * 1024 * 1024;

    [DllImport("monica_crypto", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr monica_lan_request(byte[] input, nuint length);

    [DllImport("monica_crypto", CallingConvention = CallingConvention.Cdecl)]
    private static extern void monica_lan_free(IntPtr value);

    public static JsonElement Request(object request)
    {
        var input = JsonSerializer.SerializeToUtf8Bytes(request);
        var response = IntPtr.Zero;
        try
        {
            if (input.Length > MaxRequestBytes) throw RequestFailure("request_too_large", input);
            try { response = monica_lan_request(input, (nuint)input.Length); }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { throw RequestFailure("native_unavailable", input); }
            if (response == IntPtr.Zero) throw RequestFailure("native_unavailable", input);
            using var document = JsonDocument.Parse(Marshal.PtrToStringUTF8(response) ?? "{}");
            var root = document.RootElement;
            if (!root.GetProperty("ok").GetBoolean()) throw RequestFailure(root.GetProperty("error").GetString() ?? "failed", input);
            return root.GetProperty("data").Clone();
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(input);
            if (response != IntPtr.Zero) monica_lan_free(response);
        }
    }

    private static LanSyncException RequestFailure(string code, byte[] input) =>
        new(code, ReadOperation(input), input.Length);

    private static string ReadOperation(ReadOnlySpan<byte> input)
    {
        var reader = new Utf8JsonReader(input);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return "unknown";
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isOperation = reader.ValueTextEquals("operation");
            if (!reader.Read()) break;
            if (isOperation && reader.TokenType == JsonTokenType.String)
            {
                // Only application-owned operation names may enter diagnostics.
                var operation = reader.GetString();
                return operation is "start" or "connect" or "discover" or "progress" or "acknowledge" or "cancel" or "release" or
                    "replica_open" or "replica_close" or "replica_scan_begin" or "replica_scan_batch" or "replica_scan_end" or
                    "replica_revision" or "replica_export" or "replica_merge" or "replica_pending" or "replica_applied" or "replica_applied_batch"
                    ? operation : "unknown";
            }
            reader.Skip();
        }
        return "unknown";
    }
}
