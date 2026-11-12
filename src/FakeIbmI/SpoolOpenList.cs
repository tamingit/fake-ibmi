// The "open list" APIs JTOpen's SpooledFileOpenList calls on the remote command server, which is how
// ACS lists printer output: QGYOLSPL builds the list and answers its handle, QGYGTLE returns entries
// from it and QGYCLST drops it. Entries are in format OSPL0300 or OSPL0400 (JTOpen's default); the
// list is a snapshot of the spooled files taken when it opens.

using System.Buffers.Binary;

namespace FakeIbmI;

internal sealed class SpoolOpenList(Func<IReadOnlyList<SpooledFile>> files, Func<string> defaultUser)
{
    private const int RecordLength = 136;
    private const int MaxOpen = 50;

    private readonly Lock _gate = new();
    private readonly Dictionary<int, List<SpooledFile>> _lists = [];
    private int _next;

    /// <summary>
    /// QGYOLSPL: the filter parameter (format OSPF0100 or OSPF0200, only its users are used) and the
    /// entry format name. Returns the 80-byte list information, or null for a format it doesn't know.
    /// </summary>
    public byte[]? Open(byte[] filter, byte[] format)
    {
        var name = Ebcdic.Decode(format).TrimEnd();
        if (name is not ("OSPL0300" or "OSPL0400")) return null;
        var users = Users(filter);
        var selected = files().Where(f => users is null || users.Contains(f.User ?? defaultUser(), StringComparer.OrdinalIgnoreCase)).ToList();
        int handle;
        lock (_gate)
        {
            // A client that never closes its lists doesn't fill the memory: the oldest go.
            if (_lists.Count >= MaxOpen) _lists.Remove(_lists.Keys.Min());
            handle = ++_next;
            _lists[handle] = selected;
        }
        return Information(selected.Count, 0, handle, 0);
    }

    /// <summary>QGYGTLE: up to <paramref name="number"/> entries from the record <paramref name="start"/> (1-based), as many as fit.</summary>
    public (byte[] Receiver, byte[] Information)? Get(byte[] handle, int receiverLength, int number, int start)
    {
        List<SpooledFile>? list;
        var key = BinaryPrimitives.ReadInt32BigEndian(handle);
        lock (_gate) _lists.TryGetValue(key, out list);
        if (list is null) return null;

        var first = Math.Max(start, 1) - 1;
        var count = Math.Clamp(Math.Min(number, receiverLength / RecordLength), 0, Math.Max(list.Count - first, 0));
        var receiver = new byte[count * RecordLength];
        for (var i = 0; i < count; i++) Entry(list[first + i], list[first + i].User ?? defaultUser(), receiver, i * RecordLength);
        return (receiver, Information(list.Count, count, key, receiver.Length, first + 1));
    }

    /// <summary>QGYCLST.</summary>
    public void Close(byte[] handle)
    {
        lock (_gate) _lists.Remove(BinaryPrimitives.ReadInt32BigEndian(handle));
    }

    /// <summary>
    /// The 80-byte list information: total records, records returned, handle, record length, 'C'
    /// (complete), when it was built, status '2' (ready), the length of what came back, the first record in it.
    /// </summary>
    private static byte[] Information(int total, int returned, int handle, int length, int first = 0)
    {
        var info = new byte[80];
        BinaryPrimitives.WriteInt32BigEndian(info, total);
        BinaryPrimitives.WriteInt32BigEndian(info.AsSpan(4), returned);
        BinaryPrimitives.WriteInt32BigEndian(info.AsSpan(8), handle);
        BinaryPrimitives.WriteInt32BigEndian(info.AsSpan(12), RecordLength);
        info[16] = 0xC3;
        var now = DateTime.Now;
        Ebcdic.Encode($"{(now.Year - 1900) / 100}{now.Year % 100:00}{now:MMddHHmmss}").CopyTo(info, 17);
        info[30] = 0xF2;
        BinaryPrimitives.WriteInt32BigEndian(info.AsSpan(32), length);
        BinaryPrimitives.WriteInt32BigEndian(info.AsSpan(36), first);
        return info;
    }

    /// <summary>One OSPL0300 record: job, user, number, file name, number, status (1 = *READY), opened, schedule, system, user data, form, queue, pages ...</summary>
    private static void Entry(SpooledFile f, string user, byte[] receiver, int start)
    {
        var record = receiver.AsSpan(start, RecordLength);
        record.Fill(0x40);
        void Text(int at, int length, string value) => Ebcdic.Encode(value.Length > length ? value[..length] : value.PadRight(length)).CopyTo(receiver, start + at);
        void Number(int at, int value) => BinaryPrimitives.WriteInt32BigEndian(receiver.AsSpan(start + at), value);
        Text(0, 10, f.JobName);
        Text(10, 10, user);
        Text(20, 6, f.JobNumber);
        Text(26, 10, f.Name);
        Number(36, f.Number);
        Number(40, 1);
        Text(44, 7, f.Date);
        Text(51, 6, f.Time);
        Text(57, 1, "2");
        Text(58, 10, "FAKEIBMI");
        Text(68, 10, f.UserData);
        Text(78, 10, "*STD");
        Text(88, 10, f.Queue);
        Text(98, 10, "QUSRSYS");
        Number(108, 1);
        Number(112, Math.Max((f.Data.Length + 1023) / 1024, 1));
        Number(116, 1);
        Number(120, f.Pages);
        Number(124, 1);
        Text(128, 1, "5");
        Text(129, 3, "");
        Number(132, 0);
    }

    /// <summary>The users the filter asks for, or null for all of them (*ALL).</summary>
    private static List<string>? Users(byte[] filter)
    {
        var users = new List<string>();
        if (filter.Length < 16) return null;
        // OSPF0200 starts with the length of its fixed part (106); OSPF0100 with the number of users.
        var extended = BinaryPrimitives.ReadInt32BigEndian(filter) == 106;
        var (at, count, step) = extended
            ? (BinaryPrimitives.ReadInt32BigEndian(filter.AsSpan(4)), BinaryPrimitives.ReadInt32BigEndian(filter.AsSpan(8)), 10)
            : (4, BinaryPrimitives.ReadInt32BigEndian(filter), 12);
        for (var i = 0; i < count && at + 10 <= filter.Length; i++, at += step) users.Add(Ebcdic.Decode(filter.AsSpan(at, 10)).TrimEnd());
        return users.Count == 0 || users.Contains("*ALL") ? null : users;
    }
}
