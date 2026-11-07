using KAST.Infrastructure.Services;
using KAST.Infrastructure.Steam;
using SteamKit2;

namespace KAST.Tests;

public class ProcessHuntingTests
{
    [Theory]
    [InlineData("arma3server_x64 -port=2302 -nosplash -world=empty -name=server_12 \"-profiles=C:\\x\"", 12, null)]
    [InlineData("/opt/a3/arma3server_x64 -client -connect=127.0.0.1 -name=server_3_hc2 -nosound", 3, 2)]
    [InlineData("-name=server_7", 7, null)]
    [InlineData("x \"-name=server_8\"", 8, null)]
    public void TryParseInstanceTag_RecognisesKastTags(string commandLine, int expectedId, int? expectedHc)
    {
        Assert.True(ProcessNative.TryParseInstanceTag(commandLine, out var id, out var hc));
        Assert.Equal(expectedId, id);
        Assert.Equal(expectedHc, hc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("arma3server_x64 -port=2302 -name=myserver")]
    [InlineData("arma3server_x64 -name=server_1x")]
    [InlineData("arma3server_x64 -othername=server_1")]
    public void TryParseInstanceTag_RejectsForeignProcesses(string? commandLine)
    {
        Assert.False(ProcessNative.TryParseInstanceTag(commandLine, out _, out _));
    }

    [Fact]
    public void TryParseInstanceTag_DoesNotConfuseInstanceOneWithTen()
    {
        Assert.True(ProcessNative.TryParseInstanceTag("-name=server_10", out var id, out _));
        Assert.Equal(10, id);
    }

    [Fact]
    public void ReadsOwnCommandLine()
    {
        // Exercises the native path (NtQueryInformationProcess / procfs) against
        // a process we are guaranteed to be able to read.
        var cmd = ProcessNative.TryGetCommandLine(Environment.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(cmd));

        var exe = ProcessNative.TryGetExecutablePath(Environment.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(exe));
        Assert.True(File.Exists(exe));
    }

    [Theory]
    [InlineData(EResult.FileNotFound, 0u, false, 0u, null, 107410u, "FileNotFound")]
    [InlineData(EResult.OK, 0u, true, 0u, null, 107410u, "banned")]
    [InlineData(EResult.OK, 2u, false, 0u, null, 107410u, "collection")]
    [InlineData(EResult.OK, 0u, false, 0u, null, 4000u, "app 4000")]
    [InlineData(EResult.OK, 0u, false, 2u, null, 107410u, "private")]
    [InlineData(EResult.OK, 0u, false, 0u, "http://cdn/file.zip", 107410u, "legacy")]
    [InlineData(EResult.OK, 0u, false, 0u, null, 107410u, "no published content")]
    public void DescribeMissingContent_GivesActionableReason(EResult result, uint fileType, bool banned,
        uint visibility, string? fileUrl, uint appId, string expected)
    {
        var message = SteamClientService.DescribeMissingContent(3717819316, result, fileType, banned, visibility, fileUrl, appId);

        Assert.Contains("3717819316", message);
        Assert.Contains(expected, message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hcontent_file", message);
    }
}
