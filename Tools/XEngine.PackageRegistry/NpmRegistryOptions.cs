// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Text.Json;

namespace XEngine.PackageRegistry;

/// <summary>
/// Configuration for npm-protocol registry access. Defaults target the public npmjs.com registry;
/// environment variables and the project's <c>.xengine/secrets.json</c> override without code
/// changes so tests, CI, and offline machines can redirect or disable the network path.
///
/// <list type="bullet">
/// <item><c>XENGINE_NPM_REGISTRY</c> — registry base URL (default <c>https://registry.npmjs.org/</c>).</item>
/// <item><c>XENGINE_NPM_TOKEN</c> — bearer token for authenticated operations (publish).</item>
/// <item><c>XENGINE_NPM_OTP</c> — one-time password from the account's authenticator, for accounts
/// that require 2FA to publish (sent as the npm-otp header; valid for its ~30s window).</item>
/// <item><c>XENGINE_NPM_TIMEOUT_SECONDS</c> — per-request timeout (default 15).</item>
/// <item><c>XENGINE_NPM_DISABLE</c> — <c>1</c>/<c>true</c> turns every network call into a
/// no-network error so the editor resolves from local caches/built-ins only.</item>
/// </list>
/// Tokens may also live in <c>{projectRoot}/.xengine/secrets.json</c> under <c>npmToken</c>, with a
/// user-level fallback at <c>%USERPROFILE%\.xengine\secrets.json</c> so editor-level operations
/// (launcher module installs, publishing without an open project) can authenticate too. Precedence:
/// environment variable &gt; project secrets &gt; user secrets.
/// </summary>
public sealed class NpmRegistryOptions
{
    public const string DefaultRegistryUrl = "https://registry.npmjs.org/";
    public const string RegistryEnvVar = "XENGINE_NPM_REGISTRY";
    public const string TokenEnvVar = "XENGINE_NPM_TOKEN";
    public const string OtpEnvVar = "XENGINE_NPM_OTP";
    public const string TimeoutEnvVar = "XENGINE_NPM_TIMEOUT_SECONDS";
    public const string DisableEnvVar = "XENGINE_NPM_DISABLE";

    public string RegistryUrl { get; init; } = DefaultRegistryUrl;

    /// <summary>Bearer token for authenticated operations; null for anonymous (public read).</summary>
    public string? AuthToken { get; init; }

    /// <summary>2FA one-time password for publish (npm-otp header); null when not required.</summary>
    public string? OneTimePassword { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Hard offline switch: when true, network attempts fail fast with a clear error.</summary>
    public bool Disabled { get; init; }

    /// <summary>Project root used to resolve <c>.xengine/secrets.json</c>; null skips that source.</summary>
    public string? ProjectRoot { get; init; }

    /// <summary>Test seam for the user-level secrets path; production resolves
    /// <c>%USERPROFILE%\.xengine\secrets.json</c>.</summary>
    internal static string? UserSecretsPathOverride { get; set; }

    public static NpmRegistryOptions Load(string? projectRoot = null)
    {
        string? registry = Environment.GetEnvironmentVariable(RegistryEnvVar);
        string? token = Environment.GetEnvironmentVariable(TokenEnvVar);
        string? otp = Environment.GetEnvironmentVariable(OtpEnvVar);
        string? timeoutRaw = Environment.GetEnvironmentVariable(TimeoutEnvVar);
        string? disableRaw = Environment.GetEnvironmentVariable(DisableEnvVar);

        // Env beats file for every field; the project file beats the user-level file so a project
        // can pin its own credential (same precedence the AI-gen SecretResolver uses). The
        // user-level file covers editor-level npm work (launcher module installs) where no
        // project is open.
        if (token == null && projectRoot != null)
            token = ReadTokenFromSecrets(projectRoot);
        token ??= ReadTokenFromUserSecrets();

        TimeSpan timeout = TimeSpan.FromSeconds(15);
        if (int.TryParse(timeoutRaw, out int seconds) && seconds > 0)
            timeout = TimeSpan.FromSeconds(seconds);

        return new NpmRegistryOptions
        {
            RegistryUrl = string.IsNullOrWhiteSpace(registry) ? DefaultRegistryUrl : registry.Trim(),
            AuthToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim(),
            OneTimePassword = string.IsNullOrWhiteSpace(otp) ? null : otp.Trim(),
            Timeout = timeout,
            Disabled = IsTruthy(disableRaw),
            ProjectRoot = projectRoot,
        };
    }

    /// <summary>Read <c>npmToken</c> from <c>{projectRoot}/.xengine/secrets.json</c>, if present.</summary>
    public static string? ReadTokenFromSecrets(string projectRoot)
    {
        return ReadTokenFromSecretsFile(Path.Combine(projectRoot, ".xengine", "secrets.json"));
    }

    /// <summary>Read <c>npmToken</c> from the user-level <c>%USERPROFILE%\.xengine\secrets.json</c>,
    /// if present. This is the credential source for editor-level npm operations that run without an
    /// open project (launcher module installs, `package-publish` from the engine repo).</summary>
    public static string? ReadTokenFromUserSecrets()
    {
        string path = UserSecretsPathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".xengine", "secrets.json");
        return ReadTokenFromSecretsFile(path);
    }

    private static string? ReadTokenFromSecretsFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("npmToken", out JsonElement token)
                && token.ValueKind == JsonValueKind.String)
                return token.GetString();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A malformed secrets file must never break resolution; env vars still apply.
        }
        return null;
    }

    internal static bool IsTruthy(string? value)
        => value is "1" or "true" or "TRUE" or "True" or "yes";
}
