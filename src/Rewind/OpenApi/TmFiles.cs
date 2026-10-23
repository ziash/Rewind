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
/// mode 600, with CTRADER_CA_FILE naming Rewind's certificate authority), tm.json (the Rewind account and its symbols;
/// TM's rule values stay at their defaults), run-tm.sh (Linux) and run-tm.ps1 (Windows).
/// Never touches anything of the Trader project.
/// </summary>
public static class TmFiles
{
    public const string CaFileKey = "CTRADER_CA_FILE";

    public static Credentials Ensure(string dir, int port, string caFile, IEnumerable<string> symbols, Action<string> log)
    {
        Directory.CreateDirectory(dir);
        var secrets = Path.Combine(dir, "secrets.env");
        if (!File.Exists(secrets))
        {
            var text = $"""
                # TM's secrets for Rewind (docs/spec.md §9) — fake values, accepted only by Rewind on this computer.
                # Used by run-tm.sh / run-tm.ps1 through TRADER_SECRETS_FILE; TM writes refreshed tokens back here.
                CTRADER_CLIENT_ID=rewind-client
                CTRADER_CLIENT_SECRET={Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}
                CTRADER_ACCESS_TOKEN={Credentials.NewToken()}
                CTRADER_REFRESH_TOKEN={Credentials.NewToken()}
                CTRADER_ENV=replay
                CTRADER_HOST=localhost
                CTRADER_PORT={port}
                {CaFileKey}={caFile}

                """;
            WritePrivate(secrets, text);
            log($"Made TM's replay secrets in {secrets}");
        }
        else if (!File.ReadAllLines(secrets).Any(l => l.TrimStart().StartsWith(CaFileKey + "=", StringComparison.Ordinal)))
        {
            // Made by an older Rewind: add the authority, so TM trusts Rewind without SSL_CERT_FILE (and on Windows).
            var text = File.ReadAllText(secrets);
            WritePrivate(secrets, text + (text.EndsWith('\n') ? "" : "\n") + $"{CaFileKey}={caFile}\n");
            log($"Added {CaFileKey} to TM's replay secrets in {secrets}");
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
        else
        {
            // Never rewritten (the owner may have changed it), but say so when TM would miss symbols that now have history.
            var text = File.ReadAllText(tm);
            var missing = symbols.Where(sym => !text.Contains($"\"{sym}\"", StringComparison.Ordinal)).ToList();
            if (missing.Count > 0)
                log($"TM's replay config {tm} does not list {string.Join(", ", missing)} (history arrived later): add them to its \"symbols\" for TM to manage trades there");
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

        var ps1 = Path.Combine(dir, "run-tm.ps1");
        if (!File.Exists(ps1))
        {
            // Windows PowerShell runs no scripts by default; -ExecutionPolicy Bypass allows this one run only.
            // CRLF and UTF-8 with BOM: Windows PowerShell 5.1 reads a file without a BOM in the local code page.
            File.WriteAllText(ps1, """
                # Runs TM from the Trader repo against Rewind (docs/spec.md §9). Start Rewind first, then:
                #   powershell -ExecutionPolicy Bypass -File "$HOME\rewind\tm\run-tm.ps1"
                # TRADER_REPO overrides where the Trader repo is (default ~\code\Trader). Ctrl+C stops TM.
                $ErrorActionPreference = 'Stop'
                $env:TRADER_SECRETS_FILE = Join-Path $PSScriptRoot 'secrets.env'
                $repo = if ($env:TRADER_REPO) { $env:TRADER_REPO } else { Join-Path (Join-Path $HOME 'code') 'Trader' }
                dotnet run --project (Join-Path (Join-Path $repo 'src') 'Trader.TM') -- --config-dir $PSScriptRoot @args
                exit $LASTEXITCODE

                """.ReplaceLineEndings("\r\n"), new UTF8Encoding(true));
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
