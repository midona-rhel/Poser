using Poser.Game.Scene;

namespace Poser.Game.Tests.Scene;

public sealed class LifecycleSlotOwnerTests
{
    [Fact]
    public void Slot_is_shared_across_capture_remove_restore_and_tracks_the_current_instance()
    {
        var original = new Instance();
        var restored = new Instance();
        var captures = 0;
        void Remove(Slot current) => current.Live = null;
        var owner = new LifecycleSlotOwner<Instance, Slot>(
            instance => new Slot { Live = instance },
            current => current.Live,
            (current, instance) => current.Live = instance,
            current =>
            {
                captures++;
                Remove(current);
                return true;
            },
            current =>
            {
                current.Live = restored;
                return true;
            });

        var slot = owner.SlotFor(original);
        Assert.Same(slot, owner.SlotFor(original));
        Assert.Same(original, owner.CurrentInstance(slot));

        Assert.True(owner.CaptureAndRemove(slot));
        Assert.Equal(1, captures);
        Assert.Null(owner.CurrentInstance(slot));

        Assert.True(owner.Restore(slot));
        Assert.Same(restored, owner.CurrentInstance(slot));
        Assert.Same(slot, owner.SlotFor(restored));
        Assert.False(owner.TryGetSlot(original, out _));
    }

    private sealed class Instance { }

    private sealed class Slot
    {
        public Instance? Live { get; set; }
    }
}
