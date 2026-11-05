using ThunderID.AspNetCore.BackchannelLogout;

namespace ThunderID.AspNetCore.Tests;

public class InMemoryLogoutSessionStoreTests
{
    private static readonly DateTimeOffset T0 = LogoutTokens.Now;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    private static LogoutToken Token(string? sid, string? sub, string jti = "jti-1", DateTimeOffset? issuedAt = null)
    {
        var iat = issuedAt ?? T0;
        return new LogoutToken(LogoutTokens.Issuer, sub, sid, jti, iat, iat.AddSeconds(120));
    }

    [Fact]
    public async Task Sid_logout_ends_that_session()
    {
        var store = new InMemoryLogoutSessionStore(Retention, new ManualTimeProvider(T0));

        Assert.True(await store.TryRecordLogoutAsync(Token("sid-a", "alice")));

        Assert.True(await store.IsSessionEndedAsync("sid-a", "alice", T0.AddMinutes(-30)));
    }

    [Fact]
    public async Task Sid_logout_leaves_the_subjects_other_sessions_alone()
    {
        var store = new InMemoryLogoutSessionStore(Retention, new ManualTimeProvider(T0));

        await store.TryRecordLogoutAsync(Token("sid-a", "alice"));

        Assert.False(await store.IsSessionEndedAsync("sid-b", "alice", T0.AddMinutes(-30)));
    }

    [Fact]
    public async Task Sub_only_logout_ends_sessions_that_began_before_it()
    {
        var store = new InMemoryLogoutSessionStore(Retention, new ManualTimeProvider(T0));

        await store.TryRecordLogoutAsync(Token(null, "alice"));

        Assert.True(await store.IsSessionEndedAsync("sid-a", "alice", T0.AddMinutes(-30)));
        Assert.True(await store.IsSessionEndedAsync(null, "alice", T0.AddSeconds(-1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(119)]
    [InlineData(120)]
    public async Task Sub_only_logout_ends_sessions_that_began_within_the_clock_tolerance(int secondsAfterIat)
    {
        var store = new InMemoryLogoutSessionStore(Retention, new ManualTimeProvider(T0));

        await store.TryRecordLogoutAsync(Token(null, "alice"));

        Assert.True(await store.IsSessionEndedAsync("sid-new", "alice", T0.AddSeconds(secondsAfterIat)));
    }

    [Fact]
    public async Task Sub_only_logout_leaves_a_sign_in_after_the_clock_tolerance_alone()
    {
        var store = new InMemoryLogoutSessionStore(Retention, new ManualTimeProvider(T0));

        await store.TryRecordLogoutAsync(Token(null, "alice"));

        Assert.False(await store.IsSessionEndedAsync("sid-new", "alice", T0 + LogoutTokenValidator.DefaultClockSkew + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Sub_only_logout_uses_the_configured_clock_tolerance()
    {
        var store = new InMemoryLogoutSessionStore(Retention, new ManualTimeProvider(T0), TimeSpan.FromSeconds(30));

        await store.TryRecordLogoutAsync(Token(null, "alice"));

        Assert.True(await store.IsSessionEndedAsync("sid-new", "alice", T0.AddSeconds(30)));
        Assert.False(await store.IsSessionEndedAsync("sid-new", "alice", T0.AddSeconds(31)));
    }

    [Fact]
    public async Task Sub_only_logout_without_auth_time_ends_the_session()
    {
        var store = new InMemoryLogoutSessionStore(Retention, new ManualTimeProvider(T0));

        await store.TryRecordLogoutAsync(Token(null, "alice"));

        Assert.True(await store.IsSessionEndedAsync("sid-a", "alice", authTime: null));
    }

    [Fact]
    public async Task Sub_only_logout_does_not_touch_other_subjects()
    {
        var store = new InMemoryLogoutSessionStore(Retention, new ManualTimeProvider(T0));

        await store.TryRecordLogoutAsync(Token(null, "alice"));

        Assert.False(await store.IsSessionEndedAsync("sid-b", "bob", T0.AddMinutes(-30)));
    }

    [Fact]
    public async Task Older_sub_only_logout_does_not_shorten_a_newer_one()
    {
        var store = new InMemoryLogoutSessionStore(Retention, new ManualTimeProvider(T0));

        await store.TryRecordLogoutAsync(Token(null, "alice", "jti-new", T0));
        await store.TryRecordLogoutAsync(Token(null, "alice", "jti-old", T0.AddMinutes(-10)));

        Assert.True(await store.IsSessionEndedAsync(null, "alice", T0.AddMinutes(-5)));
    }

    [Fact]
    public async Task Unknown_session_is_not_ended()
    {
        var store = new InMemoryLogoutSessionStore(Retention, new ManualTimeProvider(T0));

        Assert.False(await store.IsSessionEndedAsync("sid-x", "alice", T0));
        Assert.False(await store.IsSessionEndedAsync(null, null, null));
    }

    [Fact]
    public async Task Same_jti_twice_is_a_replay()
    {
        var store = new InMemoryLogoutSessionStore(Retention, new ManualTimeProvider(T0));

        Assert.True(await store.TryRecordLogoutAsync(Token("sid-a", "alice", "jti-1")));
        Assert.False(await store.TryRecordLogoutAsync(Token("sid-b", "alice", "jti-1")));

        Assert.False(await store.IsSessionEndedAsync("sid-b", "alice", T0.AddMinutes(-30)));
    }

    [Fact]
    public async Task Different_jtis_are_both_recorded()
    {
        var store = new InMemoryLogoutSessionStore(Retention, new ManualTimeProvider(T0));

        Assert.True(await store.TryRecordLogoutAsync(Token("sid-a", "alice", "jti-1")));
        Assert.True(await store.TryRecordLogoutAsync(Token("sid-b", "alice", "jti-2")));
    }

    [Fact]
    public async Task Jti_is_forgotten_after_the_token_expires()
    {
        var time = new ManualTimeProvider(T0);
        var store = new InMemoryLogoutSessionStore(Retention, time);
        await store.TryRecordLogoutAsync(Token("sid-a", "alice", "jti-1"));

        time.Advance(TimeSpan.FromMinutes(10));

        Assert.True(await store.TryRecordLogoutAsync(Token("sid-a", "alice", "jti-1", time.GetUtcNow())));
    }

    [Fact]
    public async Task Ended_session_is_remembered_for_the_retention_period()
    {
        var time = new ManualTimeProvider(T0);
        var store = new InMemoryLogoutSessionStore(Retention, time);
        await store.TryRecordLogoutAsync(Token("sid-a", "alice"));

        time.Advance(Retention - TimeSpan.FromMinutes(1));
        Assert.True(await store.IsSessionEndedAsync("sid-a", "alice", T0.AddMinutes(-30)));

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(await store.IsSessionEndedAsync("sid-a", "alice", T0.AddMinutes(-30)));
    }

    [Fact]
    public void Retention_must_be_positive()
    {
        var ex = Assert.Throws<ThunderIDRuntimeException>(() => new InMemoryLogoutSessionStore(TimeSpan.Zero));

        Assert.Equal(ThunderIDErrorCodes.SessionStoreRetentionInvalid, ex.Code);
    }
}
