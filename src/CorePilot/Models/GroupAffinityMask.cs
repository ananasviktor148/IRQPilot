namespace CorePilot.Models;

/// <summary>
/// A processor-group-aware affinity mask: an ordered set of GROUP_AFFINITY
/// entries (group number + 64-bit KAFFINITY mask within that group).
///
/// Registry encoding for AssignmentSetOverride (verified against WDK docs):
///   - REG_DWORD: 32-bit KAFFINITY mask for group 0.
///   - REG_QWORD: 64-bit KAFFINITY mask for group 0.
///   - REG_BINARY, length &lt;= 8: little-endian KAFFINITY mask for group 0.
/// Multi-group selection has no documented scalar form, so we use a REG_BINARY
/// array of GROUP_AFFINITY records (Mask:8 bytes LE, Group:2 bytes LE,
/// Reserved:6 bytes zero = 16 bytes per record). Decoders accept all forms.
/// </summary>
public sealed class GroupAffinityMask : IEquatable<GroupAffinityMask>
{
    public const int MaxProcessorsPerGroup = 64;
    public const int GroupAffinityRecordSize = 16;

    private readonly Dictionary<ushort, ulong> _groups = new();

    public IReadOnlyDictionary<ushort, ulong> Groups => _groups;

    public bool IsEmpty => _groups.Count == 0;

    public int ProcessorCount => _groups.Values.Sum(m => System.Numerics.BitOperations.PopCount(m));

    public bool Contains(ushort group, int processorInGroup)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)processorInGroup, (uint)MaxProcessorsPerGroup);
        return _groups.TryGetValue(group, out var mask) && (mask & (1UL << processorInGroup)) != 0;
    }

    public void Add(ushort group, int processorInGroup)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)processorInGroup, (uint)MaxProcessorsPerGroup);
        _groups.TryGetValue(group, out var mask);
        _groups[group] = mask | (1UL << processorInGroup);
    }

    public void Remove(ushort group, int processorInGroup)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)processorInGroup, (uint)MaxProcessorsPerGroup);
        if (!_groups.TryGetValue(group, out var mask)) return;
        mask &= ~(1UL << processorInGroup);
        if (mask == 0) _groups.Remove(group);
        else _groups[group] = mask;
    }

    public void Clear() => _groups.Clear();

    public GroupAffinityMask Clone()
    {
        var clone = new GroupAffinityMask();
        foreach (var (g, m) in _groups) clone._groups[g] = m;
        return clone;
    }

    public IEnumerable<(ushort Group, int Processor)> EnumerateProcessors()
    {
        foreach (var (group, mask) in _groups.OrderBy(kv => kv.Key))
        {
            for (var i = 0; i < MaxProcessorsPerGroup; i++)
            {
                if ((mask & (1UL << i)) != 0)
                    yield return (group, i);
            }
        }
    }

    // ---- Encoding ----

    /// <summary>
    /// Encodes for the AssignmentSetOverride value.
    /// Returns (kind, dword/qword payload or binary payload).
    /// Single group-0 mask -> smallest documented scalar form.
    /// Anything else -> GROUP_AFFINITY record array (REG_BINARY).
    /// </summary>
    public EncodedAffinity Encode()
    {
        if (IsEmpty)
            throw new InvalidOperationException("Cannot encode an empty affinity mask.");

        if (_groups.Count == 1 && _groups.TryGetValue(0, out var m))
        {
            if (m <= uint.MaxValue)
                return EncodedAffinity.Dword((uint)m);
            return EncodedAffinity.Qword(m);
        }

        var bytes = new byte[_groups.Count * GroupAffinityRecordSize];
        var offset = 0;
        foreach (var (group, mask) in _groups.OrderBy(kv => kv.Key))
        {
            BitConverter.GetBytes(mask).CopyTo(bytes, offset);
            BitConverter.GetBytes(group).CopyTo(bytes, offset + 8);
            // bytes offset+10..offset+15 remain zero (Reserved)
            offset += GroupAffinityRecordSize;
        }
        return EncodedAffinity.Binary(bytes);
    }

    // ---- Decoding ----

    public static GroupAffinityMask FromDword(uint value)
    {
        var mask = new GroupAffinityMask();
        if (value != 0) mask._groups[0] = value;
        return mask;
    }

    public static GroupAffinityMask FromQword(ulong value)
    {
        var mask = new GroupAffinityMask();
        if (value != 0) mask._groups[0] = value;
        return mask;
    }

    public static GroupAffinityMask FromBinary(byte[] data)
    {
        if (data.Length == 0)
            return new GroupAffinityMask();

        // GROUP_AFFINITY record array (multi-group form we write).
        if (data.Length >= GroupAffinityRecordSize && data.Length % GroupAffinityRecordSize == 0)
        {
            var looksLikeRecords = true;
            var probe = new GroupAffinityMask();
            for (var offset = 0; offset < data.Length && looksLikeRecords; offset += GroupAffinityRecordSize)
            {
                var group = BitConverter.ToUInt16(data, offset + 8);
                // Reserved bytes must be zero for the record form.
                for (var i = 10; i < 16; i++)
                    if (data[offset + i] != 0) { looksLikeRecords = false; break; }
                if (!looksLikeRecords) break;
                var maskValue = BitConverter.ToUInt64(data, offset);
                if (maskValue == 0 || group >= 512) { looksLikeRecords = false; break; }
                probe._groups[group] = maskValue;
            }
            if (looksLikeRecords && !probe.IsEmpty)
                return probe;
        }

        // Plain KAFFINITY (<= 8 bytes, little endian), group 0.
        if (data.Length > 8)
            throw new ArgumentException($"REG_BINARY KAFFINITY mask must be <= 8 bytes, got {data.Length}.", nameof(data));

        ulong value = 0;
        for (var i = data.Length - 1; i >= 0; i--)
            value = (value << 8) | data[i];
        return FromQword(value);
    }

    public bool Equals(GroupAffinityMask? other)
    {
        if (other is null) return false;
        if (_groups.Count != other._groups.Count) return false;
        foreach (var (g, m) in _groups)
        {
            if (!other._groups.TryGetValue(g, out var om) || om != m) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as GroupAffinityMask);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (g, m) in _groups.OrderBy(kv => kv.Key)) hash.Add((g, m));
        return hash.ToHashCode();
    }

    public override string ToString() =>
        IsEmpty ? "(empty)" :
        string.Join(", ", _groups.OrderBy(kv => kv.Key).Select(kv => $"G{kv.Key}:0x{kv.Value:X}"));
}

public enum AffinityEncodingKind { Dword, Qword, Binary }

public sealed record EncodedAffinity(AffinityEncodingKind Kind, uint DwordValue, ulong QwordValue, byte[] BinaryValue)
{
    public static EncodedAffinity Dword(uint value) => new(AffinityEncodingKind.Dword, value, 0, Array.Empty<byte>());
    public static EncodedAffinity Qword(ulong value) => new(AffinityEncodingKind.Qword, 0, value, Array.Empty<byte>());
    public static EncodedAffinity Binary(byte[] value) => new(AffinityEncodingKind.Binary, 0, 0, value);
}
