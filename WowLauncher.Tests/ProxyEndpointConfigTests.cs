using System;
using System.IO;
using WowLauncher.Services.Platform;
using Xunit;

namespace WowLauncher.Tests;

/// <summary>
/// Reading the upstream endpoint out of the JimsProxy config, for the fail-closed endpoint check. Proves
/// the value is extracted from the real XML shape, and that a missing key / missing file / malformed XML
/// all map to null (which the launcher treats as "refuse"), rather than throwing.
/// </summary>
public sealed class ProxyEndpointConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "proxy-cfg-" + Guid.NewGuid().ToString("N"));

    public ProxyEndpointConfigTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string Write(string contents)
    {
        var path = Path.Combine(_dir, "HermesProxy.config");
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void ReadsTheServerAddress_FromTheRealConfigShape()
    {
        var path = Write(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration><appSettings>\n" +
            "  <add key=\"ServerAddress\" value=\"play.stonetavern.app\" />\n" +
            "  <add key=\"ServerPort\" value=\"3724\" />\n</appSettings></configuration>");

        Assert.Equal("play.stonetavern.app", ProxyEndpointConfig.ReadServerAddress(path));
    }

    [Fact]
    public void ReturnsNull_WhenThereIsNoServerAddressKey()
    {
        var path = Write(
            "<configuration><appSettings><add key=\"ServerPort\" value=\"3724\" /></appSettings></configuration>");

        Assert.Null(ProxyEndpointConfig.ReadServerAddress(path));
    }

    [Fact]
    public void ReturnsNull_WhenTheFileIsMissing() =>
        Assert.Null(ProxyEndpointConfig.ReadServerAddress(Path.Combine(_dir, "nope.config")));

    [Fact]
    public void ReturnsNull_OnMalformedXml_NotAThrow()
    {
        var path = Write("<configuration><appSettings><add key=\"ServerAddress\" value=");
        Assert.Null(ProxyEndpointConfig.ReadServerAddress(path));
    }
}
