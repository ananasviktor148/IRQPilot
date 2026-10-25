using CorePilot.Models;

namespace CorePilot.Tests;

public class GroupAffinityMaskTests
{
    [Fact]
    public void Add_And_Contains_TrackProcessorsPerGroup()
    {
        var mask = new GroupAffinityMask();
        mask.Add(0, 3);
        mask.Add(1, 63);

        Assert.True(mask.Contains(0, 3));
        Assert.True(mask.Contains(1, 63));
        Assert.False(mask.Contains(0, 2));
        Assert.False(mask.Contains(2, 3));
        Assert.Equal(2, mask.ProcessorCount);
    }

    [Fact]
    public void Add_RejectsProcessorIndexBeyond64()
    {
        var mask = new GroupAffinityMask();
        Assert.Throws<ArgumentOutOfRangeException>(() => mask.Add(0, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => mask.Add(0, -1));
    }

    [Fact]
    public void Remove_DropsEmptyGroups()
    {
        var mask = new GroupAffinityMask();
        mask.Add(2, 5);
        mask.Remove(2, 5);
        Assert.True(mask.IsEmpty);
    }

    [Fact]
    public void Encode_SmallGroup0Mask_UsesDword()
    {
        var mask = new GroupAffinityMask();
        mask.Add(0, 0);
        mask.Add(0, 1);

        var encoded = mask.Encode();

        Assert.Equal(AffinityEncodingKind.Dword, encoded.Kind);
        Assert.Equal(0b11u, encoded.DwordValue);
    }

    [Fact]
    public void Encode_LargeGroup0Mask_UsesQword()
    {
        var mask = new GroupAffinityMask();
        mask.Add(0, 40);

        var encoded = mask.Encode();

        Assert.Equal(AffinityEncodingKind.Qword, encoded.Kind);
        Assert.Equal(1UL << 40, encoded.QwordValue);
    }

    [Fact]
    public void Encode_MultiGroup_UsesGroupAffinityRecordArray()
    {
        var mask = new GroupAffinityMask();
        mask.Add(0, 1);
        mask.Add(1, 5);

        var encoded = mask.Encode();

        Assert.Equal(AffinityEncodingKind.Binary, encoded.Kind);
        Assert.Equal(2 * GroupAffinityMask.GroupAffinityRecordSize, encoded.BinaryValue.Length);

        // Record 0: mask, then group, then reserved zeros.
        Assert.Equal(2UL, BitConverter.ToUInt64(encoded.BinaryValue, 0));
        Assert.Equal((ushort)0, BitConverter.ToUInt16(encoded.BinaryValue, 8));
        Assert.Equal(1UL << 5, BitConverter.ToUInt64(encoded.BinaryValue, 16));
        Assert.Equal((ushort)1, BitConverter.ToUInt16(encoded.BinaryValue, 24));
        for (var i = 10; i < 16; i++) Assert.Equal(0, encoded.BinaryValue[i]);
    }

    [Fact]
    public void Encode_EmptyMask_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new GroupAffinityMask().Encode());
    }

    [Theory]
    [InlineData(0x0u)]
    [InlineData(0x1u)]
    [InlineData(0xFFFFFFFFu)]
    public void Dword_RoundTrips(uint value)
    {
        var mask = GroupAffinityMask.FromDword(value);
        if (value == 0)
        {
            Assert.True(mask.IsEmpty);
            return;
        }
        var encoded = mask.Encode();
        Assert.Equal(AffinityEncodingKind.Dword, encoded.Kind);
        Assert.Equal(value, encoded.DwordValue);
    }

    [Theory]
    [InlineData(1UL << 63)]
    [InlineData(1UL << 40)]
    [InlineData(0xFFFF_FFFF_FFFF_FFFEUL)]
    public void Qword_RoundTrips(ulong value)
    {
        var mask = GroupAffinityMask.FromQword(value);
        var encoded = mask.Encode();
        Assert.Equal(AffinityEncodingKind.Qword, encoded.Kind);
        Assert.Equal(value, encoded.QwordValue);
    }

    [Fact]
    public void Qword_WithinDwordRange_EncodesAsDword()
    {
        // Encoding always uses the smallest documented scalar form.
        var mask = GroupAffinityMask.FromQword(0xFFUL);
        var encoded = mask.Encode();
        Assert.Equal(AffinityEncodingKind.Dword, encoded.Kind);
        Assert.Equal(0xFFu, encoded.DwordValue);
    }

    [Fact]
    public void FromBinary_DecodesLegacyKaffinityLittleEndian()
    {
        // 4-byte binary mask 0x0A (cores 1 and 3 of group 0).
        var mask = GroupAffinityMask.FromBinary(new byte[] { 0x0A, 0x00, 0x00, 0x00 });
        Assert.True(mask.Contains(0, 1));
        Assert.True(mask.Contains(0, 3));
        Assert.Equal(2, mask.ProcessorCount);
    }

    [Fact]
    public void FromBinary_Decodes8ByteMaskAsGroup0()
    {
        var bytes = new byte[8];
        BitConverter.GetBytes(1UL << 62).CopyTo(bytes, 0);
        var mask = GroupAffinityMask.FromBinary(bytes);
        Assert.True(mask.Contains(0, 62));
        Assert.Single(mask.Groups);
    }

    [Fact]
    public void Binary_MultiGroup_RoundTrips()
    {
        var original = new GroupAffinityMask();
        original.Add(0, 0);
        original.Add(0, 63);
        original.Add(1, 7);
        original.Add(3, 33);

        var encoded = original.Encode();
        var decoded = GroupAffinityMask.FromBinary(encoded.BinaryValue);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void FromBinary_RejectsOversizedMask()
    {
        Assert.Throws<ArgumentException>(() => GroupAffinityMask.FromBinary(new byte[12]));
    }

    [Fact]
    public void FromBinary_Empty_ReturnsEmptyMask()
    {
        Assert.True(GroupAffinityMask.FromBinary(Array.Empty<byte>()).IsEmpty);
    }

    [Fact]
    public void Equality_IgnoresInsertionOrder()
    {
        var a = new GroupAffinityMask();
        a.Add(1, 2);
        a.Add(0, 1);
        var b = new GroupAffinityMask();
        b.Add(0, 1);
        b.Add(1, 2);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Assignment_StringForm_RoundTrips()
    {
        var mask = new GroupAffinityMask();
        mask.Add(0, 2);
        mask.Add(1, 10);

        var assignment = AffinityAssignment.FromMask(IrqPolicy.SpecifiedProcessors, IrqPriority.High, mask);
        var restored = assignment.ToMask();

        Assert.Equal(mask, restored);
        Assert.Equal(IrqPolicy.SpecifiedProcessors, assignment.Policy);
        Assert.Equal(IrqPriority.High, assignment.Priority);
    }
}
