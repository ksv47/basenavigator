using System.Buffers.Binary;
using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class PlatformAuditTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "platform-audit-" + Guid.NewGuid().ToString("N"));
    public PlatformAuditTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private static BaseEntry Entry => new() { Name = "Audit", Kind = ConnectionKind.Server, Location = "server\\base" };

    [Theory]
    [InlineData(3, PlatformArchitecture.X86)]
    [InlineData(62, PlatformArchitecture.X64)]
    [InlineData(183, PlatformArchitecture.Arm64)]
    [InlineData(999, PlatformArchitecture.Any)]
    public void ElfMachineSupportsBothByteOrders(ushort machine, PlatformArchitecture expected)
    {
        foreach (var little in new[] { true, false })
        {
            var bytes = new byte[64]; bytes[0] = 0x7f; bytes[1] = (byte)'E'; bytes[2] = (byte)'L'; bytes[3] = (byte)'F';
            bytes[5] = little ? (byte)1 : (byte)2;
            if (little) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), machine);
            else BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(18), machine);
            var path = Path.Combine(directory, "elf"); File.WriteAllBytes(path, bytes);
            Assert.Equal(expected, PlatformCatalog.ReadArchitecture(path));
        }
    }

    [Theory]
    [InlineData(0x14c, PlatformArchitecture.X86)]
    [InlineData(0x8664, PlatformArchitecture.X64)]
    [InlineData(0xaa64, PlatformArchitecture.Arm64)]
    [InlineData(999, PlatformArchitecture.Any)]
    public void PeMachineAndCorruptSignatures(ushort machine, PlatformArchitecture expected)
    {
        var path = Path.Combine(directory, "pe");
        var bytes = new byte[128]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), 64);
        bytes[64] = (byte)'P'; bytes[65] = (byte)'E'; BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), machine);
        File.WriteAllBytes(path, bytes); Assert.Equal(expected, PlatformCatalog.ReadArchitecture(path));
        foreach (var index in new[] { 0, 1, 64, 65, 66, 67 })
        {
            var corrupted = (byte[])bytes.Clone(); corrupted[index] ^= 0xff; File.WriteAllBytes(path, corrupted);
            Assert.Equal(PlatformArchitecture.Any, PlatformCatalog.ReadArchitecture(path));
        }
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), 4); File.WriteAllBytes(path, bytes);
        Assert.Equal(PlatformArchitecture.Any, PlatformCatalog.ReadArchitecture(path));
    }

    [Theory]
    [InlineData(PlatformArchitecture.X86, "x86")]
    [InlineData(PlatformArchitecture.X64, "x64")]
    [InlineData(PlatformArchitecture.Arm64, "ARM64")]
    [InlineData(PlatformArchitecture.Any, "")]
    public void MissingRuleStillDisplaysRequestedVersionAndArchitecture(PlatformArchitecture architecture, string label)
    {
        var rule = new PlatformRule { Version = "8.3.99", Architecture = architecture };
        foreach (var individual in new[] { true, false })
        {
            var result = BaseInfo.Read(Entry with { PlatformRule = individual ? rule : null },
                new Library { PlatformRule = individual ? null : rule }, []);
            Assert.True(result.PlatformMissing); Assert.Equal("8.3.99", result.Platform);
            Assert.Equal(label, result.ArchitectureLabel); Assert.Contains("не найдена", result.Note);
        }
    }

    [Theory]
    [InlineData(PlatformArchitecture.X86, "x86")]
    [InlineData(PlatformArchitecture.X64, "x64")]
    [InlineData(PlatformArchitecture.Arm64, "ARM64")]
    [InlineData(PlatformArchitecture.Any, "разрядность неизвестна")]
    public void ManualClientDisplaysDetectedArchitecture(PlatformArchitecture architecture, string label)
    {
        var path = Path.Combine(directory, "8.3.27.1", "bin", "client");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture");
        var installation = new PlatformInstallation(path, new(8, 3, 27, 1), architecture);
        var result = BaseInfo.Read(Entry with { Executable = path, PlatformVersion = "8.3" }, new(), [installation]);
        Assert.False(result.PlatformMissing); Assert.Equal("8.3.27.1", result.Platform); // the client's own release, not Version= (F4)
        Assert.Equal(architecture == PlatformArchitecture.Any ? "" : label, result.ArchitectureLabel);
        Assert.Contains(label, result.Note);
        File.Delete(path);
        Assert.True(BaseInfo.Read(Entry with { Executable = path }, new(), []).PlatformMissing);
    }

    [Fact]
    public void InvalidVersionRejectedAndWindowsArmCanSelectX64()
    {
        Assert.Throws<ArgumentException>(() => new PlatformRule { Version = "8.3.*" }.Validate());
        var x64 = new PlatformInstallation("fixture", new(8, 3, 27, 1), PlatformArchitecture.X64);
        Assert.Equal(x64, PlatformCatalog.Select(new(), [x64], PlatformArchitecture.Arm64, true));
        Assert.Throws<InvalidOperationException>(() => PlatformCatalog.Select(new(), [x64], PlatformArchitecture.Arm64, false));
    }

    [Fact]
    public void DefaultRootsAreAbsoluteAndScanUsesOnlyRequestedDirectory()
    {
        Assert.All(PlatformCatalog.DefaultRoots(), x => Assert.True(Path.IsPathFullyQualified(x)));
        var scan = PlatformCatalog.Scan([directory]);
        Assert.Empty(scan.Installations);
        Assert.All(scan.Installations, x => Assert.True(Path.IsPathFullyQualified(x.Executable)));
        Assert.Equal(scan.Installations.Count, scan.Installations.Select(x => x.Executable).Distinct().Count());
        Assert.All(scan.Installations, x => Assert.True(File.Exists(x.Executable)));
    }

    [Fact]
    public void ScanDepthIsBoundedAndPartialVersionFolderIsNotARelease()
    {
        var parent = Path.Combine(directory, "a", "b", "c", "d", "8.3.27.1");
        Directory.CreateDirectory(parent);
        var name = OperatingSystem.IsWindows() ? "1cv8.exe" : "1cv8";
        File.WriteAllBytes(Path.Combine(parent, name), [1, 2, 3]);
        Assert.Empty(PlatformCatalog.Scan([directory]).Installations);
        var partial = Path.Combine(directory, "8.3", name); Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
        File.WriteAllBytes(partial, [1, 2, 3]);
        Assert.Null(PlatformCatalog.Read(partial));
    }
}
