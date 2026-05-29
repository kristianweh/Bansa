using System.Collections.Generic;

namespace Flow.Services;

/// <summary>
/// Tiny circular buffer of recent (down, up) samples for the tray popup chart.
/// </summary>
public sealed class RateHistory
{
    private readonly int _capacity;
    private readonly long[] _down;
    private readonly long[] _up;
    private int _head;        // index of the next slot to write
    private int _count;

    public RateHistory(int capacity = 60)
    {
        _capacity = capacity;
        _down = new long[capacity];
        _up = new long[capacity];
    }

    public int Count => _count;
    public int Capacity => _capacity;

    public void Push(long downBps, long upBps)
    {
        _down[_head] = downBps;
        _up[_head] = upBps;
        _head = (_head + 1) % _capacity;
        if (_count < _capacity) _count++;
    }

    /// <summary>
    /// Returns samples in chronological order: oldest first, newest last.
    /// Output length == Count.
    /// </summary>
    public IReadOnlyList<(long Down, long Up)> Snapshot()
    {
        var list = new List<(long, long)>(_count);
        int start = _count < _capacity ? 0 : _head;
        for (int i = 0; i < _count; i++)
        {
            int idx = (start + i) % _capacity;
            list.Add((_down[idx], _up[idx]));
        }
        return list;
    }
}
