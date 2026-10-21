using System.Security.Cryptography;
using System.Text;

namespace Rewind.OpenApi;

/// <summary>What TM's side of Rewind looks like (spec §9): one account, and the fake application credentials.</summary>
public sealed record Credentials(string ClientId, string ClientSecret)
{
    public const long AccountId = 9_000_001;
    public const string TokenPrefix = "rewind-";

    /// <summary>Tokens are worthless anywhere else; Rewind accepts any it could have issued.</summary>
    public static bool IsToken(string? token) => token is { Length: > 20 } && token.StartsWith(TokenPrefix, StringComparison.Ordinal);

    public static string NewToken() => TokenPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
}

/// <summary>
/// Writes ~/rewind/tm/ on first start (spec §9): secrets.env for TM (CTRADER_ENV=replay, localhost, fake credentials,
/// mode 600), tm.json (the Rewind account and its symbols; TM's rule values stay at their defaults) and run-tm.sh.
/// Never touches anything of the Trader project.
/// </summary>
public static class TmFiles
{
    public static Credentials Ensure(string dir, int port, IEnumerable<string> symbols, Action<string> log)
    {
        Directory.CreateDirectory(dir);
        var secrets = Path.Combine(dir, "secrets.env");
        if (!File.Exists(secrets))
        {
            var text = $"""
                # TM's secrets for Rewind (docs/spec.md §9) — fake values, accepted only by Rewind on this computer.
                # Used by run-tm.sh through TRADER_SECRETS_FILE; TM writes refreshed tokens back here.
                CTRADER_CLIENT_ID=rewind-client
                CTRADER_CLIENT_SECRET={Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}
                CTRADER_ACCESS_TOKEN={Credentials.NewToken()}
                CTRADER_REFRESH_TOKEN={Credentials.NewToken()}
                CTRADER_ENV=replay
                CTRADER_HOST=localhost
                CTRADER_PORT={port}

                """;
            WritePrivate(secrets, text);
            log($"Made TM's replay secrets in {secrets}");
        }

        var tm = Path.Combine(dir, "tm.json");
        if (!File.Exists(tm))
        {
            File.WriteAllText(tm, $$"""
                {
                  // TM's config for Rewind (docs/spec.md §9): the Rewind account. Rule values are TM's defaults
                  // (see config/tm.json in the Trader repo); copy any you want to change in here.
                  "accounts": [
                    { "broker": "rewind", "accountId": {{Credentials.AccountId}}, "symbols": [{{string.Join(", ", symbols.Select(s => $"\"{s}\""))}}] }
                  ]
                }

                """);
            log($"Made TM's replay config {tm}");
        }

        var run = Path.Combine(dir, "run-tm.sh");
        if (!File.Exists(run))
        {
            File.WriteAllText(run, """
                #!/usr/bin/env bash
                # Runs TM from the Trader repo against Rewind (docs/spec.md §9). Start Rewind first.
                # TRADER_REPO overrides where the Trader repo is (default ~/code/Trader).
                set -euo pipefail
                here="$(cd "$(dirname "$0")" && pwd)"
                export TRADER_SECRETS_FILE="$here/secrets.env"
                export SSL_CERT_FILE="$here/../certs/bundle.pem"
                exec dotnet run --project "${TRADER_REPO:-$HOME/code/Trader}/src/Trader.TM" -- --config-dir "$here" "$@"

                """);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(run, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return Read(secrets);
    }

    /// <summary>Client ID and secret from secrets.env (TM may have rewritten its tokens; those Rewind does not check one by one).</summary>
    private static Credentials Read(string path)
    {
        var values = File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('='))
            .ToDictionary(l => l[..l.IndexOf('=')].Trim(), l => l[(l.IndexOf('=') + 1)..].Trim());
        return new Credentials(values["CTRADER_CLIENT_ID"], values["CTRADER_CLIENT_SECRET"]);
    }

    private static void WritePrivate(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
    }
}
