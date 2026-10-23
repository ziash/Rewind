using System.Text;
using Rewind.OpenApi;

namespace Rewind.Tests;

/// <summary>spec §9: TM's files in ~/rewind/tm/ — the authority TM trusts, and a launcher for Linux and Windows.</summary>
public sealed class TmFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rewind-tm-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Secrets => Path.Combine(_dir, "secrets.env");

    [Fact]
    public void New_secrets_name_the_certificate_authority()
    {
        TmFiles.Ensure(_dir, 5036, "/home/owner/rewind/certs/ca.pem", ["EURUSD"], _ => { });

        Assert.Contains("CTRADER_CA_FILE=/home/owner/rewind/certs/ca.pem", File.ReadAllLines(Secrets));
    }

    [Fact]
    public void Older_secrets_get_the_authority_added_and_keep_everything_else()
    {
        var old = "CTRADER_CLIENT_ID=rewind-client\nCTRADER_CLIENT_SECRET=abc\nCTRADER_ACCESS_TOKEN=rewind-x\nCTRADER_REFRESH_TOKEN=rewind-y\nCTRADER_ENV=replay\nCTRADER_HOST=localhost\nCTRADER_PORT=5036";
        File.WriteAllText(Secrets, old);
        var log = new List<string>();

        TmFiles.Ensure(_dir, 5036, @"C:\Users\Owner\rewind\certs\ca.pem", ["EURUSD"], log.Add);
        TmFiles.Ensure(_dir, 5036, @"C:\Users\Owner\rewind\certs\ca.pem", ["EURUSD"], log.Add);

        Assert.Equal(old + "\nCTRADER_CA_FILE=C:\\Users\\Owner\\rewind\\certs\\ca.pem\n", File.ReadAllText(Secrets));
        Assert.Single(log, m => m.Contains("Added CTRADER_CA_FILE"));
    }

    [Fact]
    public void Windows_launcher_is_written_for_Windows_PowerShell()
    {
        TmFiles.Ensure(_dir, 5036, "ca.pem", ["EURUSD"], _ => { });

        var bytes = File.ReadAllBytes(Path.Combine(_dir, "run-tm.ps1"));
        Assert.Equal(Encoding.UTF8.Preamble.ToArray(), bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
        Assert.Contains("$env:TRADER_SECRETS_FILE = Join-Path $PSScriptRoot 'secrets.env'", text);
        Assert.Contains("-- --config-dir $PSScriptRoot @args", text);
    }
}
