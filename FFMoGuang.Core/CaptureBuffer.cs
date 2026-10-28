namespace FFMoGuang.Core;

public sealed record CapturedValue(string Type, string Value, string? RawBase64 = null);
public sealed record CapturedEvent(long Milliseconds, string Addon, string Event, CapturedValue[] Values);

/// <summary>Keep state changes rather than per-frame layout refreshes. Send observations are never deduplicated.</summary>
public sealed class CaptureBuffer(int capacity = 2048)
{
    private readonly Queue<CapturedEvent> events = new();
    private readonly Dictionary<string, CapturedEvent> latest = [];
    public int Count => events.Count;
    public int Received { get; private set; }
    public int Duplicates { get; private set; }
    public int Layout { get; private set; }
    public int Throttled { get; private set; }
    public int Dropped { get; private set; }
    public void Clear()
    {
        events.Clear(); latest.Clear(); Received = Duplicates = Layout = Throttled = Dropped = 0;
    }
    public void Add(CapturedEvent item)
    {
        Received++;
        if (item.Event == "PreRefresh")
        {
            var values = item.Values;
            // Both schemas are confirmed layout-only in the real 2026-10-28 capture.
            if (values.Length == 4 && values[0] == new CapturedValue("Int", "3")
                && values[1].Type == "Int" && values[2].Type == "Int" && values[3].Type == "Float"
                || item.Addon == "KTGPlayerName" && values.Length == 5 && values[0] == new CapturedValue("Int", "2")
                && values[1].Type == "Int" && values.Skip(2).All(v => v.Type == "Float"))
            { Layout++; return; }
            var command = values.FirstOrDefault()?.Value ?? "";
            // Only confirmed persistent state is deduplicated. Word commands 2/3 occur around submissions;
            // identical values may be separate observations and must remain in the capture.
            var persistent = item.Addon == "KTGPlayerName" && command == "0"
                || item.Addon == "KTGInput" && command == "6"
                || item.Addon == "KTGHudScore" && command == "1";
            if (!persistent) { Retain(item); return; }
            var slot = item.Addon == "KTGPlayerName" && values.Length > 1 ? values[1].Value : "";
            var key = item.Addon + "/" + command + "/" + slot;
            if (latest.TryGetValue(key, out var previous))
            {
                if (previous.Values.SequenceEqual(values)) { Duplicates++; return; }
                if (item.Addon == "KTGHudScore" && command == "1" && item.Milliseconds - previous.Milliseconds < 100)
                { Throttled++; return; }
            }
            if (latest.Count >= 128 && !latest.ContainsKey(key)) latest.Remove(latest.Keys.First());
            latest[key] = item;
        }
        Retain(item);
    }
    private void Retain(CapturedEvent item)
    {
        if (events.Count >= Math.Max(1, capacity)) { events.Dequeue(); Dropped++; }
        events.Enqueue(item);
    }
    public CapturedEvent[] Snapshot() => events.ToArray();
}
