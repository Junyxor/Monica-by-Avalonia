using System.Net;
using System.Text;
using System.Text.Json;
using Monica.Core.Bitwarden;
using Monica.Platform.Bitwarden;

namespace Monica.Tests;

public sealed class BitwardenMutationHttpTransportTests
{
    [Fact]
    public async Task Create_SendsEncryptedPayloadAndReturnsServerIdentity()
    {
        var handler = new CaptureHandler(_ => Json(HttpStatusCode.OK, new
        {
            Id = "server-id",
            RevisionDate = "2026-07-22T11:00:00Z"
        }));
        using var transport = CreateTransport(handler);

        var response = await transport.SendAsync(Request(
            BitwardenMutationOperationType.Create,
            "local-id",
            null));

        Assert.True(response.Succeeded);
        Assert.Equal("server-id", response.RemoteCipherId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("idempotency-key", request.IdempotencyKey);
        Assert.Equal("{\"type\":1,\"name\":\"encrypted\"}", request.Body);
    }

    [Fact]
    public async Task Update_PreflightsRevisionBeforeWriting()
    {
        var calls = 0;
        var handler = new CaptureHandler(request => ++calls == 1
            ? Json(HttpStatusCode.OK, new { Id = "cipher-id", RevisionDate = "rev-1" })
            : Json(HttpStatusCode.OK, new { Id = "cipher-id", RevisionDate = "rev-2" }));
        using var transport = CreateTransport(handler);

        var response = await transport.SendAsync(Request(
            BitwardenMutationOperationType.Update,
            "cipher-id",
            "rev-1"));

        Assert.True(response.Succeeded);
        Assert.Equal("rev-2", response.RemoteRevision);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal(HttpMethod.Get, request.Method),
            request => Assert.Equal(HttpMethod.Put, request.Method));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_CarriesTheEditedRevisionSoAServerChangeAfterPreflightIsRejected(bool vaultwardenShape)
    {
        const string editedRevision = "2026-10-09T01:00:00Z";
        const string staleMessage = "The client copy of this cipher is out of date. Resync the client and try again.";
        var handler = new CaptureHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, new { id = "cipher-id", revisionDate = editedRevision });
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            // The remote item changed between GET and PUT. Both reference servers only
            // perform their stale check when this field accompanies the update.
            if (body.RootElement.TryGetProperty("lastKnownRevisionDate", out var revision) && revision.GetString() == editedRevision)
                return vaultwardenShape
                    ? Json(HttpStatusCode.BadRequest, new { ErrorModel = new { Message = staleMessage }, secret = "must-not-leak" })
                    : Json(HttpStatusCode.BadRequest, new { error = staleMessage, secret = "must-not-leak" });
            return Json(HttpStatusCode.OK, new { id = "cipher-id", revisionDate = "2026-10-09T01:00:10Z" });
        });
        using var transport = CreateTransport(handler);
        var response = await transport.SendAsync(Request(BitwardenMutationOperationType.Update, "cipher-id", editedRevision));
        Assert.False(response.Succeeded);
        Assert.Equal((int)HttpStatusCode.BadRequest, response.HttpStatusCode);
        Assert.Equal(BitwardenFailureClass.Conflict, response.FailureClass);
        Assert.DoesNotContain("must-not-leak", response.ErrorMessage!);
    }

    [Fact]
    public async Task Update_UsesQueueRevisionRatherThanAGuardLeftInThePayload()
    {
        var handler = new CaptureHandler(request => Json(HttpStatusCode.OK, new { id = "cipher-id", revisionDate = "rev-1" }));
        using var transport = CreateTransport(handler);
        const string originalPayload = "{\"type\":1,\"name\":\"encrypted-name\",\"futureField\":{\"keep\":true},\"LastKnownRevisionDate\":\"stale-payload-guard\",\"lastKnownRevisionDate\":\"another-old-guard\"}";
        var request = Request(BitwardenMutationOperationType.Update, "cipher-id", "rev-1") with
        {
            PayloadJson = originalPayload
        };
        await transport.SendAsync(request);
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.True(body.RootElement.TryGetProperty("lastKnownRevisionDate", out var guard));
        Assert.Equal("rev-1", guard.GetString());
        Assert.Equal("encrypted-name", body.RootElement.GetProperty("name").GetString());
        Assert.True(body.RootElement.GetProperty("futureField").GetProperty("keep").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("LastKnownRevisionDate", out _));
        Assert.Single(body.RootElement.EnumerateObject(), property =>
            property.Name.Equals("lastKnownRevisionDate", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(originalPayload, request.PayloadJson);
    }

    [Fact]
    public async Task Update_RejectsPayloadThatExceedsTheLimitAfterAddingTheRevision()
    {
        const int maximumBytes = 2 * 1024 * 1024;
        var handler = new CaptureHandler(_ => throw new InvalidOperationException("No request should leave the device."));
        using var transport = CreateTransport(handler);
        var request = Request(BitwardenMutationOperationType.Update, "cipher-id", "rev-1") with
        {
            PayloadJson = "{\"name\":\"" + new string('a', maximumBytes - 11) + "\"}"
        };
        Assert.Equal(maximumBytes, Encoding.UTF8.GetByteCount(request.PayloadJson));

        await Assert.ThrowsAsync<BitwardenProtocolException>(() => transport.SendAsync(request));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Update_RejectsAMissingRevisionBeforeSending(string? revision)
    {
        var handler = new CaptureHandler(_ => throw new InvalidOperationException("No request should leave the device."));
        using var transport = CreateTransport(handler);

        await Assert.ThrowsAsync<BitwardenProtocolException>(() => transport.SendAsync(
            Request(BitwardenMutationOperationType.Update, "cipher-id", revision)));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Update_RejectsAnOversizedRevisionBeforeSending()
    {
        var handler = new CaptureHandler(_ => throw new InvalidOperationException("No request should leave the device."));
        using var transport = CreateTransport(handler);

        await Assert.ThrowsAsync<BitwardenProtocolException>(() => transport.SendAsync(
            Request(BitwardenMutationOperationType.Update, "cipher-id", new string('r', 129))));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("{\"error\":\"must-not-leak\"}")]
    [InlineData("{\"error\":{\"detail\":\"must-not-leak\"}}")]
    [InlineData("{\"ErrorModel\":{\"Message\":\"Invalid cipher type: must-not-leak\"}}")]
    [InlineData("{\"ErrorModel\":{\"Message\":\"The client copy of this cipher is out of date. Resync the client and try again. must-not-leak\"}}")]
    [InlineData("{\"unrelatedField\":\"The client copy of this cipher is out of date. Resync the client and try again.\"}")]
    [InlineData("must-not-leak")]
    [InlineData("[]")]
    public async Task Update_OtherBadRequestsKeepTheirValidationSemantics(string errorBody)
    {
        var handler = new CaptureHandler(request => request.Method == HttpMethod.Get
            ? Json(HttpStatusCode.OK, new { id = "cipher-id", revisionDate = "rev-1" })
            : new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(errorBody, Encoding.UTF8, "application/json")
            });
        using var transport = CreateTransport(handler);

        var response = await transport.SendAsync(Request(BitwardenMutationOperationType.Update, "cipher-id", "rev-1"));

        Assert.False(response.Succeeded);
        Assert.Equal((int)HttpStatusCode.BadRequest, response.HttpStatusCode);
        Assert.Null(response.FailureClass);
        Assert.Equal("Bitwarden mutation failed with HTTP 400.", response.ErrorMessage);
    }

    [Theory]
    [InlineData(BitwardenMutationOperationType.Create, HttpStatusCode.BadRequest, false)]
    [InlineData(BitwardenMutationOperationType.SoftDelete, HttpStatusCode.BadRequest, false)]
    [InlineData(BitwardenMutationOperationType.Update, HttpStatusCode.BadRequest, true)]
    [InlineData(BitwardenMutationOperationType.Update, HttpStatusCode.ServiceUnavailable, false)]
    public async Task StaleTextOnlyClassifiesAnUpdateBadRequestAsAConflict(
        BitwardenMutationOperationType operation,
        HttpStatusCode status,
        bool failsPreflight)
    {
        var handler = new CaptureHandler(request => request.Method == HttpMethod.Get && !failsPreflight
            ? Json(HttpStatusCode.OK, new { id = "cipher-id", revisionDate = "rev-1" })
            : Json(status, new { error = "The client copy of this cipher is out of date. Resync the client and try again." }));
        using var transport = CreateTransport(handler);

        var response = await transport.SendAsync(Request(
            operation, "cipher-id", operation == BitwardenMutationOperationType.Create ? null : "rev-1"));

        Assert.False(response.Succeeded);
        Assert.Equal((int)status, response.HttpStatusCode);
        Assert.Null(response.FailureClass);
    }

    [Fact]
    public async Task Update_StopsWhenRemoteRevisionChanged()
    {
        var handler = new CaptureHandler(_ => Json(HttpStatusCode.OK, new
        {
            Id = "cipher-id",
            RevisionDate = "remote-rev"
        }));
        using var transport = CreateTransport(handler);

        var response = await transport.SendAsync(Request(
            BitwardenMutationOperationType.Update,
            "cipher-id",
            "expected-rev"));

        Assert.False(response.Succeeded);
        Assert.Equal((int)HttpStatusCode.Conflict, response.HttpStatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Delete_PreflightsThenSendsDelete()
    {
        var handler = new CaptureHandler(request => request.Method == HttpMethod.Get
            ? Json(HttpStatusCode.OK, new { Id = "cipher-id", RevisionDate = "rev-1" })
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        using var transport = CreateTransport(handler);

        var response = await transport.SendAsync(Request(
            BitwardenMutationOperationType.Delete,
            "cipher-id",
            "rev-1"));

        Assert.True(response.Succeeded);
        Assert.Equal(HttpMethod.Delete, handler.Requests[1].Method);
        Assert.Equal("https://api.example.test/ciphers/cipher-id", handler.Requests[1].Uri.AbsoluteUri);
        Assert.Null(handler.Requests[1].Body);
    }

    // Monica's own trash is recoverable, so what a local delete owes the server is the route that moves
    // the cipher to the server's trash. Erasing it outright would take away an undo the user can still
    // reach here, and no local action asks for that yet.
    [Fact]
    public async Task SoftDelete_PreflightsThenWritesTheTrashRouteWithoutABody()
    {
        var handler = new CaptureHandler(request => request.Method == HttpMethod.Get
            ? Json(HttpStatusCode.OK, new { Id = "cipher-id", RevisionDate = "rev-1" })
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        using var transport = CreateTransport(handler);

        var response = await transport.SendAsync(Request(
            BitwardenMutationOperationType.SoftDelete,
            "cipher-id",
            "rev-1"));

        Assert.True(response.Succeeded);
        Assert.Equal("rev-1", response.RemoteRevision);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Equal("https://api.example.test/ciphers/cipher-id/delete", handler.Requests[1].Uri.AbsoluteUri);
        Assert.Null(handler.Requests[1].Body);
    }

    // A recycle-bin restore is decided by its route, like a trash is. The difference is the answer: the
    // server hands back the revived cipher, and the revision in it is what tells the following pull that
    // the local row and the remote copy are the same state rather than a change to back up.
    [Fact]
    public async Task Restore_WritesNoBodyButReadsTheRevivedCipherRevision()
    {
        var handler = new CaptureHandler(request => request.Method == HttpMethod.Get
            ? Json(HttpStatusCode.OK, new { Id = "cipher-id", RevisionDate = "rev-1" })
            : Json(HttpStatusCode.OK, new
            {
                Id = "cipher-id",
                RevisionDate = "rev-2",
                DeletedDate = (string?)null
            }));
        using var transport = CreateTransport(handler);

        var response = await transport.SendAsync(Request(
            BitwardenMutationOperationType.Restore,
            "cipher-id",
            "rev-1"));

        Assert.True(response.Succeeded);
        Assert.Equal("rev-2", response.RemoteRevision);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Equal("https://api.example.test/ciphers/cipher-id/restore", handler.Requests[1].Uri.AbsoluteUri);
        Assert.Null(handler.Requests[1].Body);
    }

    [Fact]
    public async Task Restore_StopsWhenRemoteRevisionChanged()
    {
        var handler = new CaptureHandler(_ => Json(HttpStatusCode.OK, new
        {
            Id = "cipher-id",
            RevisionDate = "remote-rev"
        }));
        using var transport = CreateTransport(handler);

        var response = await transport.SendAsync(Request(
            BitwardenMutationOperationType.Restore,
            "cipher-id",
            "expected-rev"));

        Assert.False(response.Succeeded);
        Assert.Equal((int)HttpStatusCode.Conflict, response.HttpStatusCode);
        // The guard is the whole point: a restore that would revive a cipher another client already
        // changed has to stop before the route is written.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RateLimitResponse_ExposesRetryAfterWithoutErrorBodySecrets()
    {
        var handler = new CaptureHandler(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, new { secret = "server detail" });
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });
        using var transport = CreateTransport(handler);

        var response = await transport.SendAsync(Request(
            BitwardenMutationOperationType.Create,
            "local-id",
            null));

        Assert.False(response.Succeeded);
        Assert.Equal(TimeSpan.FromSeconds(30), response.RetryAfter);
        Assert.DoesNotContain("server detail", response.ErrorMessage, StringComparison.Ordinal);
    }

    private static IBitwardenOwnedMutationTransport CreateTransport(CaptureHandler handler)
    {
        using var secrets = new BitwardenAccountSecrets(
            Encoding.UTF8.GetBytes("access-token"),
            Encoding.UTF8.GetBytes("refresh-token"),
            new byte[32],
            new byte[32],
            new byte[32]);
        return new BitwardenMutationTransportFactory(new FakeFactory(handler))
            .Create(CreateAccount(), secrets);
    }

    private static BitwardenMutationRequest Request(
        BitwardenMutationOperationType operation,
        string cipherId,
        string? revision) => new(
        1,
        7,
        cipherId,
        operation,
        revision,
        "{\"type\":1,\"name\":\"encrypted\"}",
        "idempotency-key");

    private static BitwardenAccount CreateAccount()
    {
        var endpoints = new BitwardenEndpointSet(
            new Uri("https://vault.example.test/"),
            new Uri("https://identity.example.test/"),
            new Uri("https://api.example.test/"));
        return new BitwardenAccount
        {
            Id = 7,
            Email = "test@example.com",
            AccountKey = BitwardenAccountIdentity.CreateAccountKey("test@example.com", endpoints),
            Endpoints = endpoints,
            Kdf = new BitwardenKdfParameters(BitwardenKdfAlgorithm.Pbkdf2Sha256, 100_000),
            IsConnected = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object payload) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
    };

    private sealed class FakeFactory(CaptureHandler handler) : IBitwardenHttpClientFactory
    {
        public HttpClient Create(BitwardenTlsOptions tls, string? clientCertificatePassword = null) =>
            new(handler, disposeHandler: false);
    }

    private sealed class CaptureHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri!,
                body,
                request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null));
            return responder(request);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Body,
        string? IdempotencyKey);
}
