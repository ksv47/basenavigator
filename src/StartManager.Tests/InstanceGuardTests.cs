using StartManager.Core;
using Xunit;

namespace StartManager.Tests;

public sealed class InstanceGuardTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "instance-tests-" + Guid.NewGuid());
    private string Profile() => Path.Combine(folder, Guid.NewGuid() + ".json");
    public void Dispose() { if (Directory.Exists(folder)) Directory.Delete(folder, true); }

    [Fact] public void WindowsPathCaseUsesSameLease()
    {
        if (!OperatingSystem.IsWindows()) return;
        var profile = Profile();
        using var lease = InstanceGuard.Acquire(profile, true);
        Assert.Throws<IOException>(() => InstanceGuard.Acquire(profile.ToUpperInvariant(), false));
    }

    [Fact] public void ConflictCodesFollowThePlatform()
    {
        var profile = Profile();
        using (InstanceGuard.Acquire(profile, true))
            Assert.True(InstanceGuard.IsConflict(Assert.ThrowsAny<IOException>(() => InstanceGuard.Acquire(profile, false))));
        // Windows: Win32 codes in an HRESULT; elsewhere the raw errno (EWOULDBLOCK, EBUSY of a CIFS share).
        int[] conflicts = OperatingSystem.IsWindows() ? [unchecked((int)0x80070020), unchecked((int)0x80070021)] : [11, 16];
        int[] others = OperatingSystem.IsWindows() ? [unchecked((int)0x8007000B), unchecked((int)0x80070010)] : [32, 33];
        foreach (var code in conflicts) Assert.True(InstanceGuard.IsConflict(new IOException("x", code)));
        foreach (var code in others) Assert.False(InstanceGuard.IsConflict(new IOException("x", code)));
    }

    [Fact] public void SharedInstancesCanCoexist()
    {
        var profile = Profile();
        using var first = InstanceGuard.Acquire(profile, false);
        using var second = InstanceGuard.Acquire(profile, false);
    }

    [Fact] public void ExclusiveInstanceRejectsBothModesAndReleasesAfterClose()
    {
        var profile = Profile();
        using (var first = InstanceGuard.Acquire(profile, true))
        {
            Assert.Throws<IOException>(() => InstanceGuard.Acquire(profile, true));
            Assert.Throws<IOException>(() => InstanceGuard.Acquire(profile, false));
        }
        using var reopened = InstanceGuard.Acquire(profile, true);
    }

    [Fact] public void SharedInstancePreventsExclusiveLaunch()
    {
        var profile = Profile();
        using var first = InstanceGuard.Acquire(profile, false);
        Assert.Throws<IOException>(() => InstanceGuard.Acquire(profile, true));
    }

    [Fact] public void DifferentProfilesAreIndependent()
    {
        using var first = InstanceGuard.Acquire(Profile(), true);
        using var second = InstanceGuard.Acquire(Profile(), true);
    }

    [Fact] public void PreferenceRoundtripsAndDefaultsToDisabled()
    {
        var root = Path.Combine(Path.GetTempPath(), "instance-setting-" + Guid.NewGuid());
        try
        {
            var store = new LibraryStore(Path.Combine(root, "library.json"));
            Assert.False(store.Load().PreventMultipleInstances);
            store.Save(new Library { PreventMultipleInstances = true });
            Assert.True(store.Load().PreventMultipleInstances);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
