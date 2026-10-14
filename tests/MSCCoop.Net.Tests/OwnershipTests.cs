using System.Collections.Generic;
using MSCCoop.Net;

namespace MSCCoop.Net.Tests;

public class OwnershipTests
{
    const uint Box = 42;

    [Fact]
    public void FirstClaimWinsSecondIsDenied()
    {
        var table = new Ownership();
        Assert.True(table.TryClaim(Box, 1, held: false, out var first));
        Assert.False(table.TryClaim(Box, 2, held: false, out var current));
        Assert.Equal(1, current.Owner);
        Assert.Equal(first.Seq, current.Seq);
    }

    [Fact]
    public void PickupTakesASimulatedObject()
    {
        var table = new Ownership();
        table.TryClaim(Box, 1, held: false, out _);
        Assert.True(table.TryClaim(Box, 2, held: true, out var info));
        Assert.Equal(2, info.Owner);
        Assert.True(info.Held);
    }

    [Fact]
    public void HeldObjectCannotBeTaken()
    {
        var table = new Ownership();
        table.TryClaim(Box, 1, held: true, out _);
        Assert.False(table.TryClaim(Box, 2, held: true, out var info));
        Assert.Equal(1, info.Owner);
    }

    [Fact]
    public void OwnerCanChangeHeldFlag()
    {
        var table = new Ownership();
        table.TryClaim(Box, 1, held: true, out var a);
        Assert.True(table.TryClaim(Box, 1, held: false, out var b));
        Assert.False(b.Held);
        Assert.True(Seq.IsNewer(b.Seq, a.Seq));
    }

    [Fact]
    public void OnlyOwnerCanRelease()
    {
        var table = new Ownership();
        table.TryClaim(Box, 1, held: false, out _);
        Assert.False(table.Release(Box, 2, out _));
        Assert.True(table.Release(Box, 1, out var info));
        Assert.False(info.IsOwned);
        Assert.True(table.TryClaim(Box, 2, held: false, out _));
    }

    [Fact]
    public void LeavingPlayerReleasesEverything()
    {
        var table = new Ownership();
        table.TryClaim(1, 1, held: true, out _);
        table.TryClaim(2, 1, held: false, out _);
        table.TryClaim(3, 2, held: false, out _);
        var changed = new List<KeyValuePair<uint, OwnerInfo>>();
        table.ReleaseAll(1, changed);

        Assert.Equal(2, changed.Count);
        Assert.False(table.Get(1).IsOwned);
        Assert.False(table.Get(2).IsOwned);
        Assert.Equal(2, table.Get(3).Owner);
    }

    [Fact]
    public void SlotThreeIsARealOwner()
    {
        var table = new Ownership();
        table.TryClaim(Box, 3, held: true, out var info);
        Assert.True(info.IsOwned);
        var buffer = new byte[16];
        var w = new BitWriter(buffer);
        ObjectMessages.WriteOwner(w, Box, info);
        var read = ObjectMessages.ReadOwner(new BitReader(buffer, w.Length), out var id);
        Assert.Equal(Box, id);
        Assert.Equal(3, read.Owner);
        Assert.True(read.IsOwned);
    }

    [Fact]
    public void ObjectStateFitsBudget()
    {
        var buffer = new byte[64];
        var w = new BitWriter(buffer);
        new ObjectState
        {
            Id = 0xDEADBEEF,
            Position = new Vec3(100, 5, -200),
            Rotation = new Quat(0.1f, 0.7f, 0.1f, 0.7f).Normalized,
            Velocity = new Vec3(1, 2, 3),
            AngularVelocity = new Vec3(-1, 0, 1),
        }.Write(w);
        Assert.Equal(ObjectMessages.MaxStateBits, w.BitPosition);

        var s = ObjectState.Read(new BitReader(buffer, w.Length));
        Assert.Equal(0xDEADBEEFu, s.Id);
        Assert.True((s.Position - new Vec3(100, 5, -200)).Magnitude < 0.002f);
    }
}
