using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FreiAtlas.Licensing;

namespace FreiAtlas.Licensing.Tests;

public sealed class LicenseBuildConfigTests
{
    [Fact]
    public void EmbeddedPublicKeyMakesDefaultFreiBuildConfigured()
    {
        var config = LicenseBuildConfig.FromEnvironment();
        Assert.Equal("frei", config.ProductId);
        Assert.True(config.IsConfigured);
    }

    [Fact]
    public void StrictBuildRejectsProductServerAndPublicKeyReplacement()
    {
        var baseline = LicenseBuildConfig.FromEnvironment();
        Assert.True(baseline.IsConfigured);

        Assert.False(new LicenseBuildConfig(
            "mf",
            LicenseBuildConfig.DefaultServerUrl,
            baseline.PublicKeyPem,
            StrictBuild: true).IsConfigured);

        Assert.False(new LicenseBuildConfig(
            LicenseBuildConfig.DefaultProductId,
            "https://attacker.test",
            baseline.PublicKeyPem,
            StrictBuild: true).IsConfigured);

        Assert.False(new LicenseBuildConfig(
            LicenseBuildConfig.DefaultProductId,
            LicenseBuildConfig.DefaultServerUrl,
            "-----BEGIN PUBLIC KEY-----\nMIIB\n-----END PUBLIC KEY-----",
            StrictBuild: true).IsConfigured);
    }
}

public sealed class LicenseTokenVerifierTests
{
    [Fact]
    public void VerifiesServerStyleTokenAndRejectsReplayedNonce()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
        var verifier = new LicenseTokenVerifier(publicKey);
        var payload = BuildPayload("active", "frei", "nonce-current", "machine-1", 100, 200, 0, 200);
        var token = SignToken(payload, rsa);

        var verified = verifier.Verify(token, "frei", "machine-1", "nonce-current");
        Assert.Equal("active", verified.Status);
        Assert.Equal("frei", verified.Product);

        var replay = Assert.Throws<LicenseValidationException>(
            () => verifier.Verify(token, "frei", "machine-1", "nonce-new"));
        Assert.Equal("invalid_response_nonce", replay.Code);
    }

    [Fact]
    public void RejectsWrongProductAndTamperedSignature()
    {
        using var rsa = RSA.Create(2048);
        var verifier = new LicenseTokenVerifier(rsa.ExportSubjectPublicKeyInfoPem());
        var payload = BuildPayload("active", "mf", "nonce-1", "machine-1", 100, 200, 0, 200);
        var token = SignToken(payload, rsa);

        var wrongProduct = Assert.Throws<LicenseValidationException>(
            () => verifier.Verify(token, "frei", "machine-1", "nonce-1"));
        Assert.Equal("wrong_product", wrongProduct.Code);

        var tampered = token[..^2] + (token[^2] == 'A' ? "BB" : "AA");
        var badSignature = Assert.Throws<LicenseValidationException>(
            () => verifier.Verify(tampered, "mf", "machine-1", "nonce-1"));
        Assert.Equal("invalid_signature", badSignature.Code);
    }

    internal static string BuildPayload(
        string status,
        string product,
        string nonce,
        string machine,
        long serverTime,
        long expiresAt,
        long trialEndsAt,
        long licenseExpiresAt)
    {
        return JsonSerializer.Serialize(new
        {
            status,
            product,
            request_nonce = nonce,
            machine_hash = machine,
            license_id = "license-1",
            issued_at = serverTime,
            expires_at = expiresAt,
            trial_ends_at = trialEndsAt,
            license_expires_at = licenseExpiresAt,
            server_time = serverTime,
        });
    }

    internal static string SignToken(string payloadJson, RSA privateKey)
    {
        var payloadSegment = Base64Url(Encoding.UTF8.GetBytes(payloadJson));
        var signature = privateKey.SignData(
            Encoding.ASCII.GetBytes(payloadSegment),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return payloadSegment + "." + Base64Url(signature);
    }

    internal static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

public sealed class ClientPatchBatchLeaseTests
{
    [Fact]
    public void LeaseCanOnlyBeClaimedOnceAndOnlyByItsPatcher()
    {
        var patcher = Guid.NewGuid();
        var lease = new ClientPatchBatchLease(
            Guid.NewGuid(),
            patcher,
            ImmutableHashSet.Create(ClientPatchModule.Translation),
            long.MaxValue);

        Assert.False(lease.TryClaim(Guid.NewGuid(), 0));
        Assert.True(lease.TryClaim(patcher, 0));
        Assert.False(lease.TryClaim(patcher, 1));

        lease.Release();
        Assert.False(lease.TryClaim(patcher, 2));
    }

    [Fact]
    public void ExpiredLeaseCannotBeClaimed()
    {
        var patcher = Guid.NewGuid();
        var lease = new ClientPatchBatchLease(
            Guid.NewGuid(),
            patcher,
            ImmutableHashSet.Create(ClientPatchModule.Translation),
            expiresAtMonotonic: 10);

        Assert.False(lease.TryClaim(patcher, 11));
    }
}

internal sealed class FakeTimeProvider : TimeProvider
{
    private long _timestamp;

    public FakeTimeProvider(DateTimeOffset now)
    {
        UtcNow = now;
    }

    public DateTimeOffset UtcNow { get; private set; }

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public override long GetTimestamp() => _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan duration)
    {
        UtcNow += duration;
        _timestamp += (long)(duration.TotalSeconds * TimestampFrequency);
    }
}

internal sealed class FixedHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(responder(request));
    }
}

internal sealed class AsyncHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return responder(request);
    }
}

public sealed class MembershipServiceTests
{
    [Fact]
    public async Task TokenExpiredImmediatelyBlocksPaidStartEvenWhenLastSuccessIsRecent()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var stateStore = new LicenseStateStore(Path.Combine(Path.GetTempPath(), $"frei-license-{Guid.NewGuid():N}.bin"));
        var handler = new FixedHttpMessageHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var nonce = doc.RootElement.GetProperty("request_nonce").GetString()!;
            var serverTime = time.GetUtcNow().ToUnixTimeSeconds();
            var payload = LicenseTokenVerifierTests.BuildPayload(
                "active", "frei", nonce, "machine-1", serverTime, serverTime + 10, 0, serverTime + 10);
            var token = LicenseTokenVerifierTests.SignToken(payload, rsa);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { ok = true, token, renewal_secret = "secret-1" }),
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var service = new MembershipService(
            new LicenseBuildConfig("frei", "https://example.test", publicKey, StrictBuild: false),
            stateStore,
            new HttpClient(handler),
            time,
            new StaticMachineIdentityProvider("machine-1"));

        await service.ActivateAsync("FA-TEST-0001-TEST-0001");
        Assert.True(service.Current.CanStartPaid);

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.False(service.Current.CanStartPaid);
        Assert.Equal(MembershipState.Expired, service.Current.State);

        await service.DisposeAsync();
        stateStore.Clear();
    }

    [Fact]
    public async Task NonceMismatchBlocksPaidStartWithoutChangingTokenDeadline()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var stateStore = new LicenseStateStore(Path.Combine(Path.GetTempPath(), $"frei-license-{Guid.NewGuid():N}.bin"));
        var handler = new FixedHttpMessageHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var path = request.RequestUri!.AbsolutePath;
            var nonce = doc.RootElement.GetProperty("request_nonce").GetString()!;
            var serverTime = time.GetUtcNow().ToUnixTimeSeconds();
            var effectiveNonce = path.EndsWith("/activate") ? nonce : "old-replayed-nonce";
            var payload = LicenseTokenVerifierTests.BuildPayload(
                "active", "frei", effectiveNonce, "machine-1", serverTime, serverTime + 3600, 0, serverTime + 3600);
            var token = LicenseTokenVerifierTests.SignToken(payload, rsa);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { ok = true, token, renewal_secret = "secret-1" }),
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var service = new MembershipService(
            new LicenseBuildConfig("frei", "https://example.test", publicKey, StrictBuild: false),
            stateStore,
            new HttpClient(handler),
            time,
            new StaticMachineIdentityProvider("machine-1"));

        await service.ActivateAsync("FA-TEST-0002-TEST-0002");
        Assert.True(service.Current.CanStartPaid);

        await service.RefreshAsync(forceOnline: true);
        Assert.False(service.Current.CanStartPaid);
        Assert.True(service.Current.FreshnessBlocked);
        Assert.Equal("invalid_response_nonce", service.Current.LastValidationFailure);

        await service.DisposeAsync();
        stateStore.Clear();
    }

    [Fact]
    public async Task StartPerformsInitialOnlineValidationForPersistedRenewalCredential()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var stateStore = new LicenseStateStore(Path.Combine(Path.GetTempPath(), $"frei-license-{Guid.NewGuid():N}.bin"));
        var handler = new FixedHttpMessageHandler(request => CreateTokenResponse(rsa, time, request, 3600));
        var config = new LicenseBuildConfig("frei", "https://example.test", publicKey, StrictBuild: false);

        var first = new MembershipService(config, stateStore, new HttpClient(handler), time, new StaticMachineIdentityProvider("machine-1"));
        await first.ActivateAsync("FA-TEST-0003-TEST-0003");
        await first.DisposeAsync();

        var second = new MembershipService(config, stateStore, new HttpClient(handler), time, new StaticMachineIdentityProvider("machine-1"));
        second.Start();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline
                   && !(second.Current.State == MembershipState.Active && second.Current.CanStartPaid))
            {
                await Task.Delay(20);
            }

            Assert.Equal(MembershipState.Active, second.Current.State);
            Assert.True(second.Current.CanStartPaid);
        }
        finally
        {
            await second.DisposeAsync();
            stateStore.Clear();
        }
    }

    [Fact]
    public async Task ExpiryTransitionRaisesChangedForStaleStateAndStopsOverlay()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var stateStore = new LicenseStateStore(Path.Combine(Path.GetTempPath(), $"frei-license-{Guid.NewGuid():N}.bin"));
        var handler = new FixedHttpMessageHandler(request => CreateTokenResponse(rsa, time, request, 120));
        var service = new MembershipService(
            new LicenseBuildConfig("frei", "https://example.test", publicKey, StrictBuild: false),
            stateStore,
            new HttpClient(handler),
            time,
            new StaticMachineIdentityProvider("machine-1"));

        var snapshots = new List<MembershipSnapshot>();
        service.Changed += (_, snapshot) => snapshots.Add(snapshot);
        await service.ActivateAsync("FA-TEST-0004-TEST-0004");
        time.Advance(TimeSpan.FromSeconds(61));
        service.EvaluateExpiry();
        Assert.Equal(MembershipState.Stale, service.Current.State);

        time.Advance(TimeSpan.FromSeconds(60));
        service.EvaluateExpiry();

        Assert.Equal(MembershipState.Expired, service.Current.State);
        Assert.False(service.Current.CanContinueOverlay);
        Assert.NotEmpty(snapshots);
        Assert.False(snapshots[^1].CanContinueOverlay);

        await service.DisposeAsync();
        stateStore.Clear();
    }

    [Fact]
    public async Task ActivationValidationFailurePreservesExistingOverlayButBlocksNewPaidOperations()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var stateStore = new LicenseStateStore(Path.Combine(Path.GetTempPath(), $"frei-license-{Guid.NewGuid():N}.bin"));
        var activationCount = 0;
        var handler = new FixedHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/activate"))
            {
                activationCount += 1;
                return activationCount == 1
                    ? CreateTokenResponse(rsa, time, request, 3600)
                    : CreateTokenResponse(rsa, time, request, 3600, "wrong-nonce");
            }

            return CreateTokenResponse(rsa, time, request, 3600);
        });
        var service = new MembershipService(
            new LicenseBuildConfig("frei", "https://example.test", publicKey, StrictBuild: false),
            stateStore,
            new HttpClient(handler),
            time,
            new StaticMachineIdentityProvider("machine-1"));

        await service.ActivateAsync("FA-TEST-0005-TEST-0005");
        Assert.True(service.Current.CanStartPaid);
        await service.ActivateAsync("FA-TEST-0005-TEST-0005");

        Assert.False(service.Current.CanStartPaid);
        Assert.True(service.Current.FreshnessBlocked);
        Assert.True(service.Current.CanContinueOverlay);
        Assert.Equal(MembershipState.Stale, service.Current.State);

        await service.DisposeAsync();
        stateStore.Clear();
    }

    [Fact]
    public async Task ServiceUnavailableFallsBackToOfflineGraceInsteadOfHardBlock()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var stateStore = new LicenseStateStore(Path.Combine(Path.GetTempPath(), $"frei-license-{Guid.NewGuid():N}.bin"));
        var failRefresh = false;
        var handler = new FixedHttpMessageHandler(request =>
        {
            if (failRefresh && request.RequestUri!.AbsolutePath.EndsWith("/session"))
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return CreateTokenResponse(rsa, time, request, 3600);
        });
        var service = new MembershipService(
            new LicenseBuildConfig("frei", "https://example.test", publicKey, StrictBuild: false),
            stateStore,
            new HttpClient(handler),
            time,
            new StaticMachineIdentityProvider("machine-1"));

        await service.ActivateAsync("FA-TEST-0006-TEST-0006");
        failRefresh = true;
        await service.RefreshAsync(forceOnline: true);

        Assert.NotEqual(MembershipState.Blocked, service.Current.State);
        Assert.True(service.Current.CanContinueOverlay);
        Assert.False(service.Current.FreshnessBlocked);

        await service.DisposeAsync();
        stateStore.Clear();
    }

    [Fact]
    public async Task StaleGenerationResponseCannotMutateNewerAuthorization()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var stateStore = new LicenseStateStore(Path.Combine(Path.GetTempPath(), $"frei-license-{Guid.NewGuid():N}.bin"));
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activationCount = 0;
        var handler = new AsyncHttpMessageHandler(async request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/activate"))
            {
                return CreateTokenResponse(rsa, time, request, 3600);
            }

            var current = Interlocked.Increment(ref activationCount);
            if (current == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return CreateTokenResponse(rsa, time, request, 3600);
        });
        var service = new MembershipService(
            new LicenseBuildConfig("frei", "https://example.test", publicKey, StrictBuild: false),
            stateStore,
            new HttpClient(handler),
            time,
            new StaticMachineIdentityProvider("machine-1"));

        var first = service.ActivateAsync("FA-TEST-0007-TEST-0007");
        await firstStarted.Task;
        await service.ActivateAsync("FA-TEST-0007-TEST-0007");
        Assert.True(service.Current.CanStartPaid);

        releaseFirst.SetResult();
        await first;

        Assert.True(service.Current.CanStartPaid);
        Assert.False(service.Current.FreshnessBlocked);
        Assert.Equal(MembershipState.Active, service.Current.State);

        await service.DisposeAsync();
        stateStore.Clear();
    }

    [Fact]
    public async Task InFlightRefreshWithOldCredentialCannotClearNewActivationCredential()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var stateStore = new LicenseStateStore(Path.Combine(Path.GetTempPath(), $"frei-license-{Guid.NewGuid():N}.bin"));
        var activationCount = 0;
        var activationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActivation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AsyncHttpMessageHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/activate"))
            {
                var current = Interlocked.Increment(ref activationCount);
                if (current == 1)
                {
                    return CreateTokenResponse(rsa, time, request, 3600, renewalSecret: "old-secret");
                }

                activationStarted.SetResult();
                await releaseActivation.Task;
                return CreateTokenResponse(rsa, time, request, 3600, renewalSecret: "new-secret");
            }

            refreshStarted.SetResult();
            await releaseRefresh.Task;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        ok = false,
                        status = "invalid_renewal_credential",
                        message = "invalid renewal credential",
                    }),
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var service = new MembershipService(
            new LicenseBuildConfig("frei", "https://example.test", publicKey, StrictBuild: false),
            stateStore,
            new HttpClient(handler),
            time,
            new StaticMachineIdentityProvider("machine-1"));

        await service.ActivateAsync("FA-TEST-0008-TEST-0008");
        var activation = service.ActivateAsync("FA-TEST-0008-TEST-0008");
        await activationStarted.Task;
        var refresh = service.RefreshAsync(forceOnline: true);
        await refreshStarted.Task;
        releaseActivation.SetResult();
        await activation;
        releaseRefresh.SetResult();
        await refresh;

        Assert.True(service.Current.CanStartPaid);
        Assert.Equal("new-secret", stateStore.Load()?.RenewalSecret);

        await service.DisposeAsync();
        stateStore.Clear();
    }

    [Fact]
    public async Task SecondQueuedRefreshDoesNotDereferenceClearedCredential()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var stateStore = new LicenseStateStore(Path.Combine(Path.GetTempPath(), $"frei-license-{Guid.NewGuid():N}.bin"));
        var refreshCount = 0;
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AsyncHttpMessageHandler(async request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/session"))
            {
                return CreateTokenResponse(rsa, time, request, 3600);
            }

            var current = Interlocked.Increment(ref refreshCount);
            if (current == 1)
            {
                refreshStarted.SetResult();
                await releaseFirstRefresh.Task;
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new
                        {
                            ok = false,
                            status = "invalid_renewal_credential",
                            message = "invalid renewal credential",
                        }),
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var service = new MembershipService(
            new LicenseBuildConfig("frei", "https://example.test", publicKey, StrictBuild: false),
            stateStore,
            new HttpClient(handler),
            time,
            new StaticMachineIdentityProvider("machine-1"));

        await service.ActivateAsync("FA-TEST-0009-TEST-0009");
        var first = service.RefreshAsync(forceOnline: true);
        await refreshStarted.Task;
        var second = service.RefreshAsync(forceOnline: true);
        releaseFirstRefresh.SetResult();

        await first;
        await second;
        Assert.Equal(MembershipState.ActivationRequired, service.Current.State);

        await service.DisposeAsync();
        stateStore.Clear();
    }

    private static HttpResponseMessage CreateTokenResponse(
        RSA rsa,
        FakeTimeProvider time,
        HttpRequestMessage request,
        long lifetimeSeconds,
        string? forcedNonce = null,
        string renewalSecret = "secret-1")
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(body);
        var nonce = forcedNonce ?? doc.RootElement.GetProperty("request_nonce").GetString()!;
        var serverTime = time.GetUtcNow().ToUnixTimeSeconds();
        var payload = LicenseTokenVerifierTests.BuildPayload(
            "active",
            "frei",
            nonce,
            "machine-1",
            serverTime,
            serverTime + lifetimeSeconds,
            0,
            serverTime + lifetimeSeconds);
        var token = LicenseTokenVerifierTests.SignToken(payload, rsa);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { ok = true, token, renewal_secret = renewalSecret }),
                Encoding.UTF8,
                "application/json"),
        };
    }
}
