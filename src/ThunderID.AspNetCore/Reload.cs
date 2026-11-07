using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace ThunderID.AspNetCore;

// The settings AddThunderID(IConfiguration) applies. A reload replaces them only when it passes the
// checks below, and only then are the cached OpenID Connect options rebuilt: a rebuild runs the
// options validators, so an invalid value applied blindly would fail every request.
internal sealed class ReloadableSettings(IConfiguration configuration, ThunderIDOptions initial)
{
    private volatile ThunderIDOptions _current = initial;
    private ConfigurationReloadToken _changed = new();

    public IConfiguration Configuration => configuration;

    public ThunderIDOptions Current => _current;

    public IChangeToken GetChangeToken() => _changed;

    public void Apply(ThunderIDOptions next)
    {
        _current = next;
        Interlocked.Exchange(ref _changed, new ConfigurationReloadToken()).OnReload();
    }

    // Messages name the setting, never its value.
    public static ThunderIDOptions Bind(IConfiguration configuration, out string? failure)
    {
        var options = new ThunderIDOptions();
        try
        {
            configuration.Bind(options);
            failure = null;
        }
        catch (InvalidOperationException)
        {
            // The binder's message quotes the value. RefreshBeforeExpiry is the only setting that's converted.
            failure = "RefreshBeforeExpiry must be a time span, such as 00:01:00.";
        }

        return options;
    }
}

// Watches the configuration section for as long as the app runs.
internal sealed class SettingsReloadWatcher : IDisposable
{
    private readonly ReloadableSettings _settings;
    private readonly ILogger<SettingsReloadWatcher> _logger;
    private readonly IDisposable _subscription;

    public SettingsReloadWatcher(ReloadableSettings settings, ILogger<SettingsReloadWatcher> logger)
    {
        _settings = settings;
        _logger = logger;
        _subscription = ChangeToken.OnChange(settings.Configuration.GetReloadToken, Reload);
    }

    public IChangeToken GetChangeToken() => _settings.GetChangeToken();

    public void Dispose() => _subscription.Dispose();

    internal void Reload()
    {
        var current = _settings.Current;
        var next = ReloadableSettings.Bind(_settings.Configuration, out var bindFailure);

        // Compared as written: the issuer pin, the logout token check and every session are bound to them.
        var immutable = new List<string>();
        if (!string.Equals(next.Authority, current.Authority, StringComparison.Ordinal))
        {
            immutable.Add(nameof(ThunderIDOptions.Authority));
        }

        if (!string.Equals(next.ClientId, current.ClientId, StringComparison.Ordinal))
        {
            immutable.Add(nameof(ThunderIDOptions.ClientId));
        }

        if (immutable.Count > 0)
        {
            Reject(ThunderIDErrorCodes.ReloadImmutableSetting, $"{string.Join(" and ", immutable)} can't change while the app runs; restart the app to use a new value.");
            return;
        }

        var failures = new List<string>();
        if (bindFailure is not null)
        {
            failures.Add(bindFailure);
        }

        SettingsChecks.RefreshBeforeExpiry(failures, next.RefreshBeforeExpiry);
        if (failures.Count > 0)
        {
            Reject(ThunderIDErrorCodes.InvalidSettings, SettingsChecks.Prefix + string.Join(" ", failures));
            return;
        }

        if (Same(current, next))
        {
            return;
        }

        _settings.Apply(next);
        _logger.LogInformation("Applied the reloaded ThunderID settings");
    }

    private void Reject(string code, string reason) =>
        _logger.LogWarning("The reloaded ThunderID settings weren't applied ({Code}): {Reason} The previous settings stay.", code, reason);

    // The configuration root signals any change, so a reload that leaves these alone keeps the cached options.
    private static bool Same(ThunderIDOptions a, ThunderIDOptions b) =>
        a.ClientSecret == b.ClientSecret
        && a.Resource == b.Resource
        && a.RefreshBeforeExpiry == b.RefreshBeforeExpiry
        && a.Scopes.SequenceEqual(b.Scopes, StringComparer.Ordinal);
}

internal sealed class SettingsChangeTokenSource<TOptions>(SettingsReloadWatcher watcher, string name) : IOptionsChangeTokenSource<TOptions>
{
    public string Name => name;

    public IChangeToken GetChangeToken() => watcher.GetChangeToken();
}
