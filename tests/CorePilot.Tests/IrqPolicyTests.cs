using CorePilot.Models;

namespace CorePilot.Tests;

public class IrqPolicyTests
{
    [Theory]
    [InlineData(IrqPolicy.MachineDefault, 0u)]
    [InlineData(IrqPolicy.AllCloseProcessors, 1u)]
    [InlineData(IrqPolicy.OneCloseProcessor, 2u)]
    [InlineData(IrqPolicy.AllProcessorsInMachine, 3u)]
    [InlineData(IrqPolicy.SpecifiedProcessors, 4u)]
    [InlineData(IrqPolicy.SpreadMessagesAcrossAllProcessors, 5u)]
    public void RegistryValues_MatchWdkEnum(IrqPolicy policy, uint expected)
    {
        // Numeric encoding verified against WDK wdm.h IRQ_DEVICE_POLICY documentation.
        Assert.Equal(expected, (uint)policy);
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(5u, true)]
    [InlineData(6u, false)]   // reserved for system use
    [InlineData(99u, false)]
    public void IsDefined_CoversDocumentedRange(uint value, bool expected)
    {
        Assert.Equal(expected, IrqPolicyInfo.IsDefined(value));
    }

    [Fact]
    public void OnlySpecifiedProcessors_RequiresMask()
    {
        Assert.True(IrqPolicyInfo.RequiresMask(IrqPolicy.SpecifiedProcessors));
        Assert.False(IrqPolicyInfo.RequiresMask(IrqPolicy.MachineDefault));
        Assert.False(IrqPolicyInfo.RequiresMask(IrqPolicy.SpreadMessagesAcrossAllProcessors));
    }

    [Fact]
    public void EveryDefinedPolicy_HasDescription()
    {
        for (uint i = 0; i <= 5; i++)
        {
            var text = IrqPolicyInfo.Describe((IrqPolicy)i);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain("Unknown", text);
        }
    }
}
