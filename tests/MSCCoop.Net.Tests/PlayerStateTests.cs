using MSCCoop.Net;

namespace MSCCoop.Net.Tests;

public class PlayerStateTests
{
    [Fact]
    public void RoundTripWithinQuantizationError()
    {
        var state = new PlayerState
        {
            Slot = 3,
            Time = 4321.5,
            Position = new Vec3(-1234.5f, 12.25f, 987.75f),
            Velocity = new Vec3(3.5f, -1f, 0.25f),
            Yaw = -45,
            Pitch = 30,
            Flags = PlayerFlags.Crouching | PlayerFlags.InVehicle,
        };
        var buffer = new byte[64];
        var w = new BitWriter(buffer);
        state.Write(w);
        Assert.True(w.Length <= 24);

        var read = PlayerState.Read(new BitReader(buffer, w.Length));
        Assert.Equal(3, read.Slot);
        Assert.Equal(4321.5, read.Time, 3);
        Assert.True((read.Position - state.Position).Magnitude < 0.002f);
        Assert.True((read.Velocity - state.Velocity).Magnitude < 0.05f);
        Assert.Equal(315, read.Yaw, 0);
        Assert.Equal(30, read.Pitch, 0);
        Assert.Equal(state.Flags, read.Flags);
    }

    [Fact]
    public void YawRotationMatchesUnityConvention()
    {
        var q = PlayerState.YawRotation(90);
        Assert.Equal(0.7071f, q.Y, 3);
        Assert.Equal(0.7071f, q.W, 3);
    }
}
