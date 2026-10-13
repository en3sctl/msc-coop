using System;

namespace MSCCoop.Net;

public struct MotionState
{
    public Vec3 Position;
    public Vec3 Velocity;
    public Quat Rotation;
    public Vec3 AngularVelocity;
}

public sealed class SnapshotBuffer
{
    const int Capacity = 32;
    const int GapWindow = 20;
    const double MinDelay = 0.05;
    const double MaxDelay = 0.25;
    const double DelaySlewPerSecond = 0.05;
    public const double MaxExtrapolation = 0.25;

    readonly double[] times = new double[Capacity];
    readonly MotionState[] states = new MotionState[Capacity];
    readonly double[] gaps = new double[GapWindow];
    int count;
    int head;
    int gapCount;
    int gapNext;
    double lastArrival = double.NaN;
    double targetDelay = 0.1;

    public double Delay { get; private set; } = 0.1;
    public int Count => count;

    public void Clear()
    {
        count = 0;
        head = 0;
        gapCount = 0;
        lastArrival = double.NaN;
    }

    public void Add(double sampleTime, double arrivalTime, in MotionState state)
    {
        if (count > 0 && sampleTime <= times[Index(count - 1)])
            return;

        if (!double.IsNaN(lastArrival))
            RecordGap(arrivalTime - lastArrival);
        lastArrival = arrivalTime;

        if (count == Capacity)
        {
            head = (head + 1) % Capacity;
            count--;
        }
        var i = Index(count);
        times[i] = sampleTime;
        states[i] = state;
        count++;
    }

    public void AdvanceDelay(double deltaTime)
    {
        var step = DelaySlewPerSecond * deltaTime;
        Delay += MathUtil.Clamp(targetDelay - Delay, -step, step);
    }

    public bool Sample(double renderTime, out MotionState result)
    {
        result = default;
        if (count == 0)
            return false;

        var first = Index(0);
        if (renderTime <= times[first])
        {
            result = states[first];
            return true;
        }

        for (var n = 0; n < count - 1; n++)
        {
            var a = Index(n);
            var b = Index(n + 1);
            if (renderTime > times[b])
                continue;
            var dt = (float)(times[b] - times[a]);
            var t = (float)((renderTime - times[a]) / (times[b] - times[a]));
            result = Interpolate(states[a], states[b], dt, t);
            return true;
        }

        var last = states[Index(count - 1)];
        var ahead = (float)Math.Min(renderTime - times[Index(count - 1)], MaxExtrapolation);
        result = last;
        result.Position = last.Position + last.Velocity * ahead;
        result.Rotation = (Quat.FromAngularVelocity(last.AngularVelocity, ahead) * last.Rotation).Normalized;
        return true;
    }

    static MotionState Interpolate(in MotionState a, in MotionState b, float dt, float t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        var h00 = 2 * t3 - 3 * t2 + 1;
        var h10 = t3 - 2 * t2 + t;
        var h01 = -2 * t3 + 3 * t2;
        var h11 = t3 - t2;

        MotionState s;
        s.Position = a.Position * h00 + a.Velocity * (h10 * dt) + b.Position * h01 + b.Velocity * (h11 * dt);
        s.Velocity = a.Velocity * (1 - t) + b.Velocity * t;
        s.Rotation = Quat.Slerp(a.Rotation, b.Rotation, t);
        s.AngularVelocity = a.AngularVelocity * (1 - t) + b.AngularVelocity * t;
        return s;
    }

    void RecordGap(double gap)
    {
        gaps[gapNext] = gap;
        gapNext = (gapNext + 1) % GapWindow;
        if (gapCount < GapWindow)
            gapCount++;

        var worst = 0.0;
        for (var i = 0; i < gapCount; i++)
            worst = Math.Max(worst, gaps[i]);
        targetDelay = MathUtil.Clamp(worst * 2, MinDelay, MaxDelay);
    }

    int Index(int n) => (head + n) % Capacity;
}
