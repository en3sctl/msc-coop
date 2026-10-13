using System;
using MSCCoop.Net;

namespace MSCCoop.Net.Tests;

public class TimingTests
{
    [Fact]
    public void ClockOffsetConvergesUnderJitter()
    {
        var random = new Random(5);
        var clock = new ClockSync();
        const double trueOffset = 1234.567;
        const double oneWay = 0.04;
        for (var i = 0; i < 100; i++)
        {
            var sent = i * 0.5;
            var up = oneWay + random.NextDouble() * 0.03;
            var down = oneWay + random.NextDouble() * 0.03;
            var remote = sent + up + trueOffset;
            clock.AddSample(sent, remote, sent + up + down);
        }
        Assert.InRange(clock.Offset, trueOffset - 0.01, trueOffset + 0.01);
        Assert.InRange(clock.Rtt, 0.08, 0.14);
    }

    [Fact]
    public void ClockSnapsOnLargeJump()
    {
        var clock = new ClockSync();
        clock.AddSample(0, 10.05, 0.1);
        clock.AddSample(1, 1000.05, 1.1);
        Assert.InRange(clock.Offset, 998.9, 999.1);
    }

    static MotionState Linear(double t) => new()
    {
        Position = new Vec3((float)(t * 10), 0, 0),
        Velocity = new Vec3(10, 0, 0),
        Rotation = Quat.Identity,
    };

    [Fact]
    public void HermiteReproducesConstantVelocityExactly()
    {
        var buffer = new SnapshotBuffer();
        for (var i = 0; i < 10; i++)
            buffer.Add(i * 0.05, i * 0.05, Linear(i * 0.05));

        for (var t = 0.0; t <= 0.45; t += 0.013)
        {
            Assert.True(buffer.Sample(t, out var s));
            Assert.Equal(t * 10, s.Position.X, 3);
        }
    }

    [Fact]
    public void ExtrapolationIsCapped()
    {
        var buffer = new SnapshotBuffer();
        buffer.Add(0, 0, Linear(0));
        buffer.Add(0.05, 0.05, Linear(0.05));
        Assert.True(buffer.Sample(5, out var s));
        Assert.Equal((0.05 + SnapshotBuffer.MaxExtrapolation) * 10, s.Position.X, 3);
    }

    [Fact]
    public void OutOfOrderSnapshotIsIgnored()
    {
        var buffer = new SnapshotBuffer();
        buffer.Add(0.1, 0.1, Linear(0.1));
        buffer.Add(0.05, 0.12, Linear(0.05));
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void DelayAdaptsToArrivalGaps()
    {
        var buffer = new SnapshotBuffer();
        for (var i = 0; i < 20; i++)
            buffer.Add(i * 0.1, i * 0.1, Linear(i * 0.1));
        for (var i = 0; i < 200; i++)
            buffer.AdvanceDelay(0.05);
        Assert.Equal(0.2, buffer.Delay, 3);
    }
}
