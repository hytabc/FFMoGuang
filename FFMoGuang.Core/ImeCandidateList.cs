using System.Buffers.Binary;
using System.Text;

namespace FFMoGuang.Core;

public static class ImeCandidateList
{
    // Win32 CANDIDATELIST: validate every offset before decoding the current numeric-selection page.
    public static string[] Parse(byte[] data)
    {
        if (data.Length < 24) return [];
        uint Read(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        var size = Read(0); var count = Read(8); var start = Read(16); var pageSize = Read(20);
        if (size > data.Length || size < 24 || count > 4096 || 24 + count * 4 > size
            || start >= count || pageSize == 0) return [];
        var result = new List<string>();
        for (var index = start; index < Math.Min(count, start + Math.Min(pageSize, 10)); index++)
        {
            var offset = Read(24 + (int)index * 4);
            if (offset < 24 + count * 4 || offset % 2 != 0 || offset > size - 2) return [];
            var end = (int)offset;
            while (end + 2 <= size && end - offset < 512 && (data[end] != 0 || data[end + 1] != 0)) end += 2;
            if (end + 2 > size || end - offset >= 512) return [];
            result.Add(Encoding.Unicode.GetString(data, (int)offset, end - (int)offset));
        }
        return result.ToArray();
    }
}
