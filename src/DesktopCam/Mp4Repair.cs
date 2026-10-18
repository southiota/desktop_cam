using System.Buffers.Binary;
using System.IO;

namespace DesktopCam;

/// <summary>
/// 녹화 중 PC 가 꺼지거나 프로그램이 강제로 끝나 마무리되지 못한 조각 MP4 를 고친다.
/// 조각(moof+mdat)에는 영상이 다 들어 있지만 전체 길이(mvhd duration)가 0 으로 남아,
/// Windows 등 일부 플레이어가 길이를 0 초로 보거나 앞부분만 재생한다.
///   1) 끝까지 다 써진 조각까지만 남기고 뒤의 반쯤 써진 부분은 잘라 낸다.
///   2) 조각들의 길이를 더해 mvhd 의 duration 칸에 그대로(같은 자리, 같은 크기) 써 넣는다.
/// </summary>
public static class Mp4Repair
{
    /// <summary>고친 뒤 영상 길이(초). 고칠 필요가 없거나 조각 MP4 가 아니면 null.</summary>
    public static double? Repair(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var data = new byte[fs.Length];
        fs.ReadExactly(data);

        long moovOff = -1, moovEnd = -1, lastGoodEnd = 0;
        var fragments = new List<(long moofStart, long moofEnd)>();
        long pendingMoof = -1, pendingMoofEnd = -1;

        foreach (var (type, start, body, end) in Boxes(data, 0, data.Length))
        {
            if (end > data.Length) break;                 // 반쯤 써진 상자: 여기서 멈춤
            switch (type)
            {
                case "moov": moovOff = body; moovEnd = end; lastGoodEnd = end; break;
                case "moof": pendingMoof = body; pendingMoofEnd = end; break;
                case "mdat":
                    if (pendingMoof >= 0) { fragments.Add((pendingMoof, pendingMoofEnd)); pendingMoof = -1; }
                    lastGoodEnd = end;
                    break;
                default: lastGoodEnd = end; break;
            }
        }
        if (moovOff < 0 || fragments.Count == 0) return null;

        // 트랙별 시간 단위(mdhd timescale)와 mvhd 위치
        var timescales = new Dictionary<uint, uint>();
        long mvhd = -1;
        foreach (var (type, _, body, end) in Boxes(data, moovOff, moovEnd))
        {
            if (type == "mvhd") mvhd = body;
            if (type != "trak") continue;
            uint id = 0, ts = 0;
            foreach (var (t2, _, b2, e2) in Boxes(data, body, end))
            {
                if (t2 == "tkhd") id = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)(b2 + (data[b2] == 1 ? 20 : 12))));
                if (t2 == "mdia")
                    foreach (var (t3, _, b3, _) in Boxes(data, b2, e2))
                        if (t3 == "mdhd") ts = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)(b3 + (data[b3] == 1 ? 20 : 12))));
            }
            if (id != 0 && ts != 0) timescales[id] = ts;
        }
        if (mvhd < 0) return null;
        bool v1 = data[mvhd] == 1;
        uint movieScale = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)(mvhd + (v1 ? 20 : 12))));
        ulong current = v1 ? BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan((int)(mvhd + 24)))
                           : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)(mvhd + 16)));

        // 조각마다 표본 길이를 더한다
        var sums = new Dictionary<uint, ulong>();
        foreach (var (moof, moofEnd) in fragments)
            foreach (var (t, _, b, e) in Boxes(data, moof, moofEnd))
            {
                if (t != "traf") continue;
                uint track = 0, defDur = 0;
                foreach (var (t2, _, b2, _) in Boxes(data, b, e))
                {
                    var s = data.AsSpan((int)b2);
                    uint flags = BinaryPrimitives.ReadUInt32BigEndian(s) & 0xFFFFFF;
                    if (t2 == "tfhd")
                    {
                        track = BinaryPrimitives.ReadUInt32BigEndian(s[4..]);
                        int o = 8 + ((flags & 0x1) != 0 ? 8 : 0) + ((flags & 0x2) != 0 ? 4 : 0);
                        if ((flags & 0x8) != 0) defDur = BinaryPrimitives.ReadUInt32BigEndian(s[o..]);
                    }
                    else if (t2 == "trun")
                    {
                        uint count = BinaryPrimitives.ReadUInt32BigEndian(s[4..]);
                        int o = 8 + ((flags & 0x1) != 0 ? 4 : 0) + ((flags & 0x4) != 0 ? 4 : 0);
                        int per = ((flags & 0x100) != 0 ? 4 : 0) + ((flags & 0x200) != 0 ? 4 : 0)
                                + ((flags & 0x400) != 0 ? 4 : 0) + ((flags & 0x800) != 0 ? 4 : 0);
                        ulong sum = 0;
                        if ((flags & 0x100) != 0)
                            for (int i = 0; i < count; i++) sum += BinaryPrimitives.ReadUInt32BigEndian(s[(o + i * per)..]);
                        else sum = (ulong)count * defDur;
                        sums[track] = sums.GetValueOrDefault(track) + sum;
                    }
                }
            }

        double seconds = sums.Where(kv => timescales.ContainsKey(kv.Key))
                             .Select(kv => (double)kv.Value / timescales[kv.Key]).DefaultIfEmpty(0).Max();
        if (seconds <= 0 || (current > 0 && lastGoodEnd == data.Length)) return null;   // 이미 정상

        ulong dur = (ulong)Math.Round(seconds * movieScale);
        if (v1) BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan((int)(mvhd + 24)), dur);
        else BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan((int)(mvhd + 16)), (uint)Math.Min(dur, uint.MaxValue));

        fs.Position = mvhd;
        fs.Write(data, (int)mvhd, v1 ? 32 : 20);
        if (lastGoodEnd < data.Length) fs.SetLength(lastGoodEnd);   // 반쯤 써진 꼬리 잘라 내기
        return seconds;
    }

    /// <summary>[start, end) 안의 상자들: (종류, 상자 시작, 내용 시작, 상자 끝). 끝이 파일 밖이면 그대로 돌려줘 호출한 쪽이 판단.</summary>
    static IEnumerable<(string type, long start, long body, long end)> Boxes(byte[] d, long start, long end)
    {
        long off = start;
        while (off + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan((int)off));
            string type = System.Text.Encoding.ASCII.GetString(d, (int)off + 4, 4);
            int hdr = 8;
            if (size == 1 && off + 16 <= end) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan((int)off + 8)); hdr = 16; }
            else if (size == 0) size = end - off;
            if (size < hdr) yield break;
            yield return (type, off, off + hdr, off + size);
            off += size;
        }
    }
}
