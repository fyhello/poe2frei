using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace FreiAtlas.Licensing;

public sealed class MembershipDeniedException : Exception
{
    public MembershipDeniedException(string message)
        : base(message)
    {
    }
}

public sealed class MembershipService : IMembershipGate, IAsyncDisposable
{
    private static readonly TimeSpan FreshnessWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OfflineGrace = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LeaseLifetime = TimeSpan.FromHours(2);

    private readonly LicenseBuildConfig _config;
    private readonly bool _isConfigured;
    private readonly LicenseStateStore _stateStore;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly IMachineIdentityProvider _machineIdentity;
    private readonly LicenseTokenVerifier _tokenVerifier;
    private readonly object _gate = new();
    private readonly object _grantGate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<Guid, MembershipGrant> _grants = [];
    private CancellationTokenSource? _shutdown;
    private Task? _heartbeatTask;
    private Task? _expiryTask;
    private Task? _startupRefreshTask;
    private LicensePersistedState? _persisted;
    private MembershipState _state = MembershipState.Unverified;
    private long _lastSuccessMono;
    private bool _hasSuccessfulValidation;
    private long _effectiveDeadlineMono;
    private long _graceDeadlineMono;
    private bool _freshnessBlocked;
    private string? _lastValidationFailure;
    private long _identityEpoch;
    private bool _disposed;

    public MembershipService(
        LicenseBuildConfig config,
        LicenseStateStore? stateStore = null,
        HttpClient? httpClient = null,
        TimeProvider? timeProvider = null,
        IMachineIdentityProvider? machineIdentity = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _isConfigured = config.IsConfigured;
        _stateStore = stateStore ?? new LicenseStateStore();
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        _timeProvider = timeProvider ?? TimeProvider.System;
        _machineIdentity = machineIdentity ?? new WindowsMachineIdentityProvider();
        _tokenVerifier = new LicenseTokenVerifier(config.PublicKeyPem);
        _persisted = _stateStore.Load();
        if (_persisted is not null
            && !string.Equals(_persisted.ProductId, _config.ProductId, StringComparison.Ordinal))
        {
            _persisted = null;
        }

        if (!_isConfigured)
        {
            _state = MembershipState.ConfigurationError;
        }
    }

    public MembershipSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return BuildSnapshotNoLock();
            }
        }
    }

    public event EventHandler<MembershipSnapshot>? Changed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_shutdown is not null)
            {
                return;
            }

            _shutdown = new CancellationTokenSource();
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_shutdown.Token));
            _expiryTask = Task.Run(() => ExpiryLoopAsync(_shutdown.Token));
            if (_isConfigured
                && _persisted is not null
                && !string.IsNullOrWhiteSpace(_persisted.RenewalSecret))
            {
                var token = _shutdown.Token;
                _startupRefreshTask = Task.Run(() => StartupRefreshAsync(token));
            }
        }
    }

    public MembershipDecision Check(MembershipFeature feature)
    {
        if (feature is MembershipFeature.Recovery or MembershipFeature.ReadOnly)
        {
            return new MembershipDecision(true, MembershipState.Active, string.Empty, feature);
        }

        if (!_isConfigured)
        {
            return new MembershipDecision(
                false,
                MembershipState.ConfigurationError,
                "发行包授权配置缺失。",
                feature);
        }

        var snapshot = Current;
        return snapshot.CanStartPaid
            ? new MembershipDecision(true, snapshot.State, string.Empty, feature)
            : new MembershipDecision(false, snapshot.State, snapshot.Reason ?? "需要有效会员授权", feature);
    }

    public async Task<MembershipDecision> EnsureFreshAsync(
        MembershipFeature feature,
        CancellationToken cancellationToken = default)
    {
        if (feature is MembershipFeature.Recovery or MembershipFeature.ReadOnly)
        {
            return new MembershipDecision(true, MembershipState.Active, string.Empty, feature);
        }

        if (!_isConfigured)
        {
            SetConfigurationError();
            return Denied(feature, "发行包授权配置缺失。");
        }

        if (Current.CanStartPaid)
        {
            return new MembershipDecision(true, MembershipState.Active, string.Empty, feature);
        }

        await RefreshAsync(forceOnline: true, cancellationToken).ConfigureAwait(false);
        var snapshot = Current;
        return snapshot.CanStartPaid
            ? new MembershipDecision(true, snapshot.State, string.Empty, feature)
            : new MembershipDecision(false, snapshot.State, snapshot.Reason ?? "需要有效会员授权", feature);
    }

    public async Task<MembershipSnapshot> ActivateAsync(
        string licenseKey,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isConfigured)
        {
            SetConfigurationError();
            return Current;
        }

        var key = (licenseKey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return SetState(MembershipState.ActivationRequired, "请输入会员卡密。");
        }

        var epoch = Interlocked.Increment(ref _identityEpoch);
        var nonce = CreateRequestNonce();
        var machineHash = _machineIdentity.GetMachineHash();
        var request = new
        {
            machine_hash = machineHash,
            license_key = key,
            request_nonce = nonce,
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync(
                BuildUrl($"/p/{_config.ProductId}/v1/activate"),
                request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return HandleNetworkFailure(epoch);
        }

        var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return HandleHardFailure(epoch, response.StatusCode, body);
        }

        if (!TryGetString(body, "token", out var token)
            || !TryGetString(body, "renewal_secret", out var renewalSecret)
            || string.IsNullOrWhiteSpace(renewalSecret))
        {
            return HandleValidationFailure(
                epoch,
                new LicenseValidationException(
                    "invalid_activation_response",
                    "激活响应缺少续期凭证。"));
        }

        LicenseTokenPayload payload;
        try
        {
            payload = _tokenVerifier.Verify(token, _config.ProductId, machineHash, nonce);
        }
        catch (LicenseValidationException exception)
        {
            return HandleValidationFailure(epoch, exception);
        }

        ApplyVerifiedToken(payload, token, renewalSecret, machineHash, epoch, invalidateInFlight: true);
        return Current;
    }

    public async Task<MembershipSnapshot> RefreshAsync(
        bool forceOnline,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_isConfigured)
        {
            SetConfigurationError();
            return Current;
        }

        if (_persisted is null || string.IsNullOrWhiteSpace(_persisted.RenewalSecret))
        {
            return SetStateIfCurrent(
                Interlocked.Read(ref _identityEpoch),
                MembershipState.ActivationRequired,
                "需要输入会员卡密。");
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LicensePersistedState? persistedSnapshot;
            long epoch;
            lock (_gate)
            {
                persistedSnapshot = _persisted;
                epoch = _identityEpoch;
            }

            if (persistedSnapshot is null || string.IsNullOrWhiteSpace(persistedSnapshot.RenewalSecret))
            {
                return SetStateIfCurrent(
                    epoch,
                    MembershipState.ActivationRequired,
                    "需要输入会员卡密。");
            }

            var renewalSecretSnapshot = persistedSnapshot.RenewalSecret;
            var nonce = CreateRequestNonce();
            var machineHash = _machineIdentity.GetMachineHash();
            var request = new
            {
                machine_hash = machineHash,
                renewal_secret = renewalSecretSnapshot,
                request_nonce = nonce,
            };

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.PostAsJsonAsync(
                    BuildUrl($"/p/{_config.ProductId}/v1/session"),
                    request,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                return HandleNetworkFailure(epoch);
            }

            var body = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return HandleHardFailure(epoch, response.StatusCode, body);
            }

            if (!TryGetString(body, "token", out var token))
            {
                return HandleValidationFailure(
                    epoch,
                    new LicenseValidationException(
                        "invalid_response",
                        "授权响应缺少 token。"));
            }

            LicenseTokenPayload payload;
            try
            {
                payload = _tokenVerifier.Verify(token, _config.ProductId, machineHash, nonce);
            }
            catch (LicenseValidationException exception)
            {
                return HandleValidationFailure(epoch, exception);
            }

            var renewalSecret = TryGetString(body, "renewal_secret", out var rotatedSecret)
                && !string.IsNullOrWhiteSpace(rotatedSecret)
                ? rotatedSecret
                : renewalSecretSnapshot;
            ApplyVerifiedToken(payload, token, renewalSecret, machineHash, epoch);
            return Current;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<ClientPatchBatchLease> CreateClientPatchBatchLeaseAsync(
        Guid patcherInstanceId,
        ImmutableHashSet<ClientPatchModule> requestedModules,
        CancellationToken cancellationToken = default)
    {
        if (requestedModules.Count == 0)
        {
            throw new MembershipDeniedException("no requested modules");
        }

        var decision = await EnsureFreshAsync(MembershipFeature.ClientPatchBatch, cancellationToken)
            .ConfigureAwait(false);
        if (!decision.Allowed)
        {
            throw new MembershipDeniedException(decision.Message);
        }

        var now = NowMono;
        var grant = new MembershipGrant(
            Guid.NewGuid(),
            patcherInstanceId,
            requestedModules,
            now,
            AddSeconds(now, GrantLifetime.TotalSeconds));

        lock (_grantGate)
        {
            _grants[grant.GrantId] = grant;
        }

        return CreateLeaseFromGrant(grant.GrantId, patcherInstanceId, requestedModules);
    }

    private ClientPatchBatchLease CreateLeaseFromGrant(
        Guid grantId,
        Guid patcherInstanceId,
        ImmutableHashSet<ClientPatchModule> requestedModules)
    {
        MembershipGrant grant;
        lock (_grantGate)
        {
            if (!_grants.TryGetValue(grantId, out var found)
                || found.PatcherInstanceId != patcherInstanceId
                || NowMono > found.ExpiresAtMonotonic)
            {
                throw new MembershipDeniedException("batch grant is invalid or expired");
            }

            if (!requestedModules.IsSubsetOf(found.AllowedModules))
            {
                throw new MembershipDeniedException("requested modules exceed grant");
            }

            _grants.Remove(grantId);
            grant = found;
        }

        var allowed = requestedModules.ToImmutableHashSet();
        return new ClientPatchBatchLease(
            Guid.NewGuid(),
            patcherInstanceId,
            allowed,
            AddSeconds(NowMono, LeaseLifetime.TotalSeconds));
    }

    private bool ApplyVerifiedToken(
        LicenseTokenPayload payload,
        string token,
        string renewalSecret,
        string machineHash,
        long expectedEpoch,
        bool invalidateInFlight = false)
    {
        var now = NowMono;
        var effectiveSeconds = ComputeEffectiveSeconds(payload);
        if (effectiveSeconds <= 0)
        {
            SetStateIfCurrent(expectedEpoch, MembershipState.Expired, "授权已到期。");
            return false;
        }

        if (payload.Status == "trial")
        {
            SetStateIfCurrent(expectedEpoch, MembershipState.Blocked, "当前发行版本未开启试用。");
            return false;
        }

        lock (_gate)
        {
            if (expectedEpoch != _identityEpoch)
            {
                return false;
            }

            if (invalidateInFlight)
            {
                Interlocked.Increment(ref _identityEpoch);
            }

            _persisted = new LicensePersistedState(
                _config.ProductId,
                token,
                renewalSecret,
                machineHash,
                payload.LicenseExpiresAt,
                payload.TrialEndsAt,
                payload.ExpiresAt,
                payload.ServerTime,
                _timeProvider.GetUtcNow().ToUnixTimeSeconds(),
                string.Empty,
                string.Empty,
                string.Empty);
            _stateStore.Save(_persisted);
            _lastSuccessMono = now;
            _hasSuccessfulValidation = true;
            _effectiveDeadlineMono = AddSeconds(now, effectiveSeconds);
            _graceDeadlineMono = long.MaxValue;
            _freshnessBlocked = false;
            _lastValidationFailure = null;
            _state = MembershipState.Active;
        }

        RaiseChanged();
        return true;
    }

    private long ComputeEffectiveSeconds(LicenseTokenPayload payload)
    {
        var tokenRemaining = payload.ExpiresAt - payload.ServerTime;
        if (tokenRemaining <= 0)
        {
            return 0;
        }

        if (payload.Status == "active")
        {
            if (payload.LicenseExpiresAt <= 0)
            {
                return tokenRemaining;
            }

            return Math.Min(tokenRemaining, payload.LicenseExpiresAt - payload.ServerTime);
        }

        if (payload.Status == "trial")
        {
            if (payload.TrialEndsAt <= 0)
            {
                return 0;
            }

            return Math.Min(tokenRemaining, payload.TrialEndsAt - payload.ServerTime);
        }

        return 0;
    }

    private MembershipSnapshot HandleNetworkFailure(long expectedEpoch)
    {
        lock (_gate)
        {
            if (expectedEpoch != _identityEpoch)
            {
                return Current;
            }

            if (_hasSuccessfulValidation && _persisted is not null)
            {
                _state = MembershipState.Grace;
                _graceDeadlineMono = AddSeconds(_lastSuccessMono, OfflineGrace.TotalSeconds);
                if (NowMono >= _graceDeadlineMono || NowMono >= _effectiveDeadlineMono)
                {
                    _state = MembershipState.NetworkUnavailable;
                }
            }
            else
            {
                _state = MembershipState.NetworkUnavailable;
            }
        }

        RaiseChanged();
        return Current;
    }

    private MembershipSnapshot HandleValidationFailure(
        long expectedEpoch,
        LicenseValidationException exception)
    {
        lock (_gate)
        {
            if (expectedEpoch != _identityEpoch)
            {
                return Current;
            }

            _freshnessBlocked = true;
            _lastValidationFailure = exception.Code;
            if (_state is MembershipState.Active or MembershipState.Stale or MembershipState.Grace)
            {
                _state = MembershipState.Stale;
            }
        }

        RaiseChanged();
        return Current;
    }

    private MembershipSnapshot HandleHardFailure(
        long expectedEpoch,
        HttpStatusCode statusCode,
        JsonElement body)
    {
        if ((int)statusCode >= 500 || statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            return HandleNetworkFailure(expectedEpoch);
        }

        var status = TryGetString(body, "status", out var value) ? value : string.Empty;
        if (status is "blocked" or "expired" or "product_disabled")
        {
            lock (_gate)
            {
                if (expectedEpoch != _identityEpoch)
                {
                    return Current;
                }

                _state = status == "expired" ? MembershipState.Expired : MembershipState.Blocked;
                _freshnessBlocked = false;
                _lastValidationFailure = status;
                _persisted = _persisted is null
                    ? null
                    : _persisted with
                    {
                        Token = string.Empty,
                        LastHardRejectionCode = status,
                        LastHardRejectionMessage = TryGetString(body, "message", out var message) ? message : string.Empty,
                    };
                if (_persisted is not null)
                {
                    _stateStore.Save(_persisted);
                }
                Interlocked.Increment(ref _identityEpoch);
            }

            RaiseChanged();
            return Current;
        }

        if (status is "invalid_renewal_credential" or "renewal_required")
        {
            lock (_gate)
            {
                if (expectedEpoch != _identityEpoch)
                {
                    return Current;
                }

                _state = MembershipState.Blocked;
                _freshnessBlocked = false;
                _lastValidationFailure = status;
                _persisted = null;
                _stateStore.Clear();
                Interlocked.Increment(ref _identityEpoch);
            }

            RaiseChanged();
            return Current;
        }

        if (status == "activation_required")
        {
            return SetStateIfCurrent(expectedEpoch, MembershipState.ActivationRequired, "需要输入会员卡密。");
        }

        return SetStateIfCurrent(
            expectedEpoch,
            MembershipState.Blocked,
            $"授权请求失败（HTTP {(int)statusCode}）。");
    }

    private MembershipSnapshot SetStateIfCurrent(
        long expectedEpoch,
        MembershipState state,
        string message)
    {
        lock (_gate)
        {
            if (expectedEpoch != _identityEpoch)
            {
                return Current;
            }

            _state = state;
            _freshnessBlocked = state is not (MembershipState.Active or MembershipState.Stale or MembershipState.Grace);
            if (state is MembershipState.Blocked or MembershipState.Expired)
            {
                Interlocked.Increment(ref _identityEpoch);
            }
        }

        RaiseChanged(message);
        return Current;
    }

    private MembershipSnapshot SetConfigurationError()
    {
        return SetState(MembershipState.ConfigurationError, "发行包授权配置缺失。");
    }

    private MembershipSnapshot SetState(MembershipState state, string message)
    {
        lock (_gate)
        {
            _state = state;
            _freshnessBlocked = state is not (MembershipState.Active or MembershipState.Stale or MembershipState.Grace);
            if (state is MembershipState.Blocked or MembershipState.Expired)
            {
                Interlocked.Increment(ref _identityEpoch);
            }
        }

        RaiseChanged(message);
        return Current;
    }

    private MembershipDecision Denied(MembershipFeature feature, string message)
    {
        return new MembershipDecision(false, _state, message, feature);
    }

    private MembershipSnapshot BuildSnapshotNoLock()
    {
        var now = NowMono;
        var displayState = _state;
        if (displayState is MembershipState.Active or MembershipState.Stale or MembershipState.Grace
            && _hasSuccessfulValidation)
        {
            if (now >= _effectiveDeadlineMono)
            {
                displayState = MembershipState.Expired;
            }
            else if (now > AddSeconds(_lastSuccessMono, OfflineGrace.TotalSeconds))
            {
                displayState = MembershipState.NetworkUnavailable;
            }
            else if (displayState == MembershipState.Active
                     && now > AddSeconds(_lastSuccessMono, FreshnessWindow.TotalSeconds))
            {
                displayState = MembershipState.Stale;
            }
        }

        var canStartPaid = displayState == MembershipState.Active
            && !_freshnessBlocked
            && now < _effectiveDeadlineMono
            && now <= AddSeconds(_lastSuccessMono, FreshnessWindow.TotalSeconds);

        var continueDeadline = Math.Min(
            _effectiveDeadlineMono == 0 ? long.MaxValue : _effectiveDeadlineMono,
            _hasSuccessfulValidation ? AddSeconds(_lastSuccessMono, OfflineGrace.TotalSeconds) : 0);
        var canContinueOverlay = displayState is MembershipState.Active or MembershipState.Stale or MembershipState.Grace
            && now < continueDeadline;

        var reason = _freshnessBlocked
            ? "授权响应校验失败，需要重新联网验证。"
            : displayState switch
            {
                MembershipState.Active => null,
                MembershipState.Stale => "授权需要刷新，请联网验证。",
                MembershipState.Grace => "网络异常，已进入离线宽限。",
                MembershipState.Expired => "会员已到期。",
                MembershipState.Blocked => _lastValidationFailure switch
                {
                    "invalid_renewal_credential" => "续期凭证已失效，请重新输入会员卡密。",
                    "product_disabled" => "该产品已停用。",
                    _ => _persisted?.LastHardRejectionMessage ?? "授权已被封禁。",
                },
                MembershipState.NetworkUnavailable => "网络不可用，授权已停止。",
                MembershipState.ConfigurationError => "发行包授权配置缺失。",
                MembershipState.ActivationRequired => "需要输入会员卡密。",
                _ => "授权尚未验证。",
            };

        return new MembershipSnapshot(
            displayState,
            _freshnessBlocked,
            canStartPaid,
            canContinueOverlay,
            reason,
            _lastValidationFailure,
            _persisted?.LicenseExpiresAt ?? 0,
            _persisted?.TrialEndsAt ?? 0,
            _persisted?.TokenExpiresAt ?? 0,
            _effectiveDeadlineMono == 0
                ? 0
                : _timeProvider.GetUtcNow().ToUnixTimeSeconds() + (long)Math.Ceiling(_timeProvider.GetElapsedTime(now, _effectiveDeadlineMono).TotalSeconds),
            _graceDeadlineMono == 0 || _graceDeadlineMono == long.MaxValue
                ? 0
                : _timeProvider.GetUtcNow().ToUnixTimeSeconds() + (long)Math.Ceiling(_timeProvider.GetElapsedTime(now, _graceDeadlineMono).TotalSeconds),
            _hasSuccessfulValidation ? _persisted?.LastSuccessUtcSeconds ?? 0 : 0,
            _config.ProductId);
    }

    private async Task StartupRefreshAsync(CancellationToken cancellationToken)
    {
        var epoch = Interlocked.Read(ref _identityEpoch);
        try
        {
            await RefreshAsync(forceOnline: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            SetStateIfCurrent(
                epoch,
                MembershipState.NetworkUnavailable,
                "启动授权刷新失败，请检查网络后重试。");
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60), _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_state is MembershipState.Active or MembershipState.Stale or MembershipState.Grace)
            {
                await RefreshAsync(forceOnline: false, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ExpiryLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            EvaluateExpiry();
        }
    }

    internal void EvaluateExpiry()
    {
        var changed = false;
        lock (_gate)
        {
            var now = NowMono;
            if (_state is MembershipState.Active or MembershipState.Stale or MembershipState.Grace
                && _hasSuccessfulValidation
                && now >= _effectiveDeadlineMono)
            {
                _state = MembershipState.Expired;
                changed = true;
            }
            else if (_state is MembershipState.Active or MembershipState.Stale or MembershipState.Grace
                     && _hasSuccessfulValidation
                     && now > AddSeconds(_lastSuccessMono, OfflineGrace.TotalSeconds))
            {
                _state = MembershipState.NetworkUnavailable;
                changed = true;
            }
            else if (_state == MembershipState.Active
                     && _hasSuccessfulValidation
                     && now > AddSeconds(_lastSuccessMono, FreshnessWindow.TotalSeconds))
            {
                _state = MembershipState.Stale;
                changed = true;
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private void RaiseChanged(string? message = null)
    {
        var snapshot = Current;
        Changed?.Invoke(this, snapshot);
    }

    private string BuildUrl(string path)
    {
        return _config.ServerUrl.TrimEnd('/') + path;
    }

    private static string CreateRequestNonce()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static async Task<JsonElement> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static bool TryGetString(JsonElement root, string property, out string value)
    {
        value = string.Empty;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(property, out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    private long NowMono => _timeProvider.GetTimestamp();

    private long AddSeconds(long timestamp, double seconds)
    {
        return timestamp + (long)Math.Round(seconds * _timeProvider.TimestampFrequency);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancellationTokenSource? shutdown;
        Task? heartbeat;
        Task? expiry;
        Task? startupRefresh;
        lock (_gate)
        {
            shutdown = _shutdown;
            heartbeat = _heartbeatTask;
            expiry = _expiryTask;
            startupRefresh = _startupRefreshTask;
            _shutdown = null;
            _heartbeatTask = null;
            _expiryTask = null;
            _startupRefreshTask = null;
        }

        shutdown?.Cancel();
        foreach (var task in new[] { heartbeat, expiry, startupRefresh })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        shutdown?.Dispose();
        _refreshGate.Dispose();
        _httpClient.Dispose();
    }
}
