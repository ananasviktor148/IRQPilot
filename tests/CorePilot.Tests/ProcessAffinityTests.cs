using CorePilot.Services;

namespace CorePilot.Tests;

public class ProcessAffinityTests
{
    [Fact]
    public void CurrentProcess_AffinityCanBeReadAndChanged()
    {
        var service = new ProcessAffinityService();
        var self = service.Enumerate().Single(p => p.Pid == Environment.ProcessId);

        Assert.True(self.CanQuery);
        Assert.True(self.CanSet);
        Assert.NotEqual(0UL, self.AffinityMask);
        Assert.NotEqual(0UL, self.SystemMask);

        var firstAvailable = self.SystemMask & (~self.SystemMask + 1);
        Assert.NotEqual(0UL, firstAvailable);

        try
        {
            service.SetAffinity(Environment.ProcessId, firstAvailable);

            var after = service.Enumerate().Single(p => p.Pid == Environment.ProcessId);
            Assert.Equal(firstAvailable, after.AffinityMask);
        }
        finally
        {
            service.SetAffinity(Environment.ProcessId, self.AffinityMask);
        }
    }

    [Fact]
    public void EmptyMask_IsRejected()
    {
        var service = new ProcessAffinityService();
        Assert.Throws<ProcessAffinityException>(
            () => service.SetAffinity(Environment.ProcessId, 0));
    }
}
