using MagicMouse.Core.Driver;
using MagicMouse.Core.Gestures;
using MagicMouse.Core.Protocol;
using MagicMouse.Core.Settings;
using Xunit;

namespace MagicMouse.Tests;

internal sealed class RecordingSink : IInputSink
{
    public readonly List<string> Events = new();
    public int ScrollV, ScrollH;
    public void Scroll(int v, int h) { ScrollV += v; ScrollH += h; Events.Add($"scroll {v} {h}"); }
    public void Button(MouseButton b, bool down) => Events.Add($"{b} {(down ? "down" : "up")}");
    public void Click(ClickAction a) => Events.Add($"click {a}");
    public void Chord(params VKey[] keys) => Events.Add("chord " + string.Join("+", keys));
    public void Move(int dx, int dy) => Events.Add($"move {dx} {dy}");
    public bool Has(string e) => Events.Contains(e);
}

/// <summary>Builds touch-frame sequences against a fake clock.</summary>
internal sealed class Rig
{
    public readonly RecordingSink Sink = new();
    public readonly GestureEngine Engine;
    public double Now;

    public Rig(AppSettings? s = null, DeviceModel model = DeviceModel.MagicMouse3)
        => Engine = new GestureEngine(s ?? new AppSettings(), Sink, model);

    public void Frame(PhysicalButtons buttons, params (int id, int x, int y)[] touches)
    {
        var list = touches.Select(t => new Touch(t.id, t.x, t.y, 30, 30, 10, 0, 0x40)).ToList();
        Engine.Process(new TouchFrame(0, buttons, 0, 0, list), Now);
        Now += 8;
    }

    public void Frame(params (int id, int x, int y)[] touches) => Frame(PhysicalButtons.None, touches);

    /// <summary>Linear finger motion over <paramref name="frames"/> frames.</summary>
    public void Drag(int id, (int x, int y) from, (int x, int y) to, int frames, (int id, int dx, int dy)? companion = null)
    {
        for (int i = 0; i <= frames; i++)
        {
            int x = from.x + (to.x - from.x) * i / frames;
            int y = from.y + (to.y - from.y) * i / frames;
            if (companion is { } c) Frame((id, x, y), (c.id, x + c.dx, y + c.dy));
            else Frame((id, x, y));
        }
    }

    public void Lift() => Frame();
}

public class GestureEngineTests
{
    private static AppSettings Defaults(Action<AppSettings>? tweak = null)
    {
        var s = new AppSettings();
        tweak?.Invoke(s);
        return s;
    }

    [Fact]
    public void Slow_vertical_drag_toward_user_scrolls_up_naturally()
    {
        var d = new Rig();
        d.Drag(1, (0, -500), (0, 300), frames: 60);
        d.Lift();
        Assert.True(d.Sink.ScrollV > 0);
        Assert.Equal(0, d.Sink.ScrollH);
    }

    [Fact]
    public void Inverse_vertical_flips_direction()
    {
        var d = new Rig(Defaults(s => s.Scrolling.InverseVertical = true));
        d.Drag(1, (0, -500), (0, 300), frames: 60);
        Assert.True(d.Sink.ScrollV < 0);
    }

    [Fact]
    public void Orthogonal_lock_suppresses_cross_axis()
    {
        var d = new Rig(Defaults(s => s.OneFinger.NavigateBackForward = false));
        d.Drag(1, (0, -500), (60, 300), frames: 60);   // mostly vertical with a little sideways drift
        Assert.True(d.Sink.ScrollV > 0);
        Assert.Equal(0, d.Sink.ScrollH);
    }

    [Fact]
    public void Resting_finger_with_jitter_does_not_scroll()
    {
        var d = new Rig();
        var rnd = new Random(1);
        // Finger lands (patch drifts 15 units over the first frames), then wobbles +-10 units.
        for (int i = 0; i < 5; i++) d.Frame((1, 0, -600 + i * 3));
        for (int i = 0; i < 120; i++) d.Frame((1, rnd.Next(-10, 11), -588 + rnd.Next(-10, 11)));
        Assert.Equal(0, d.Sink.ScrollV);
        Assert.Equal(0, d.Sink.ScrollH);
    }

    [Fact]
    public void Dead_zone_zero_reacts_to_small_moves()
    {
        var d = new Rig(Defaults(s => { s.Engine.ScrollStartOffset = 0; s.OneFinger.NavigateBackForward = false; }));
        d.Drag(1, (0, -600), (0, -560), frames: 20);
        Assert.True(d.Sink.ScrollV > 0);
    }

    [Fact]
    public void Diagonal_stroke_scrolls_vertically()
    {
        // ~40 degrees off vertical: sideways component is large but not dominant.
        var d = new Rig(Defaults(s => s.OneFinger.NavigateBackForward = false));
        d.Drag(1, (-300, -600), (200, 0), frames: 60);
        Assert.True(d.Sink.ScrollV > 0);
        Assert.Equal(0, d.Sink.ScrollH);
    }

    [Fact]
    public void Stroke_that_starts_sideways_then_goes_down_still_scrolls_vertically()
    {
        var d = new Rig(Defaults(s => s.OneFinger.NavigateBackForward = false));
        d.Drag(1, (0, -600), (30, -595), frames: 4);       // first wobble is sideways
        d.Drag(1, (30, -595), (40, 300), frames: 60);      // then a clear downward stroke
        Assert.True(d.Sink.ScrollV > 0);
        Assert.Equal(0, d.Sink.ScrollH);
    }

    [Fact]
    public void Clear_change_of_direction_relocks_axis()
    {
        var d = new Rig(Defaults(s => s.OneFinger.NavigateBackForward = false));
        d.Drag(1, (-600, 0), (200, 0), frames: 60);        // horizontal
        int h = d.Sink.ScrollH;
        Assert.True(h < 0);
        d.Drag(1, (200, 0), (200, 900), frames: 60);       // then vertical, same touch
        Assert.True(d.Sink.ScrollV > 0);
    }

    [Fact]
    public void Non_smooth_scroll_emits_whole_notches()
    {
        var d = new Rig(Defaults(s => s.Scrolling.Smooth = false));
        d.Drag(1, (0, -800), (0, 800), frames: 80);
        Assert.All(d.Sink.Events.Where(e => e.StartsWith("scroll")),
            e => Assert.Equal(0, int.Parse(e.Split(' ')[1]) % 120));
        Assert.True(d.Sink.ScrollV >= 120);
    }

    [Fact]
    public void Scrolling_disabled_emits_nothing()
    {
        var d = new Rig(Defaults(s => s.Scrolling.Enabled = false));
        d.Drag(1, (0, -500), (0, 300), frames: 60);
        Assert.DoesNotContain(d.Sink.Events, e => e.StartsWith("scroll"));
    }

    [Fact]
    public void Fast_one_finger_swipe_right_goes_back()
    {
        var d = new Rig();
        d.Drag(1, (-400, 0), (300, 0), frames: 10);   // 700 units in 80 ms
        d.Lift();
        Assert.True(d.Sink.Has("click Back"));
        Assert.Equal(0, d.Sink.ScrollH);               // held-back horizontal scroll was discarded
    }

    [Fact]
    public void Swapped_swipe_goes_forward()
    {
        var d = new Rig(Defaults(s => s.OneFinger.SwapDirection = true));
        d.Drag(1, (-400, 0), (300, 0), frames: 10);
        Assert.True(d.Sink.Has("click Forward"));
    }

    [Fact]
    public void Slow_horizontal_drag_scrolls_instead_of_navigating()
    {
        var d = new Rig();
        d.Drag(1, (-400, 0), (300, 0), frames: 100);  // 800 ms
        d.Lift();
        Assert.DoesNotContain(d.Sink.Events, e => e.StartsWith("click"));
        Assert.True(d.Sink.ScrollH < 0);               // finger right -> content right -> scroll left
    }

    [Fact]
    public void Two_finger_swipe_left_switches_to_next_desktop()
    {
        var d = new Rig(Defaults(s => s.TwoFinger.SwitchDesktops = true));
        d.Drag(1, (300, 0), (-400, 0), frames: 10, companion: (2, 0, 400));
        Assert.True(d.Sink.Has("chord Control+LWin+Right"));
    }

    [Fact]
    public void Two_finger_swipe_up_toggles_task_view()
    {
        var d = new Rig(Defaults(s => s.TwoFinger.SwipeUpTaskView = true));
        d.Drag(1, (-300, 600), (-300, -200), frames: 10, companion: (2, 500, 0));
        d.Lift();
        d.Now += 500;
        d.Drag(1, (-300, 600), (-300, -200), frames: 10, companion: (2, 500, 0));
        Assert.True(d.Sink.Has("chord LWin+Tab"));
        Assert.True(d.Sink.Has("chord Escape"));
    }

    [Fact]
    public void One_finger_tap_left_zone_clicks_left()
    {
        var d = new Rig(Defaults(s => s.Taps.OneFingerTap = true));
        d.Frame((1, -500, -1000));
        d.Frame((1, -500, -1000));
        d.Lift();
        Assert.True(d.Sink.Has("click LeftClick"));
    }

    [Fact]
    public void One_finger_tap_right_zone_clicks_right()
    {
        var d = new Rig(Defaults(s => s.Taps.OneFingerTap = true));
        d.Frame((1, 800, -1000));
        d.Lift();
        Assert.True(d.Sink.Has("click RightClick"));
    }

    [Fact]
    public void Tap_in_rear_palm_area_is_ignored_with_tap_area()
    {
        var d = new Rig(Defaults(s => s.Taps.OneFingerTap = true));
        d.Frame((1, 0, 1500));
        d.Lift();
        Assert.DoesNotContain(d.Sink.Events, e => e.StartsWith("click"));
    }

    [Fact]
    public void Long_touch_is_not_a_tap()
    {
        var d = new Rig(Defaults(s => s.Taps.OneFingerTap = true));
        for (int i = 0; i < 40; i++) d.Frame((1, -500, -1000));  // 320 ms
        d.Lift();
        Assert.DoesNotContain(d.Sink.Events, e => e.StartsWith("click"));
    }

    [Fact]
    public void Two_and_three_finger_taps()
    {
        var d = new Rig(Defaults(s => { s.Taps.TwoFingerTap = true; s.Taps.ThreeFingerTap = true; }));
        d.Frame((1, -300, -800), (2, 300, -800));
        d.Lift();
        d.Now += 400;
        d.Frame((1, -500, -800), (2, 0, -800), (3, 500, -800));
        d.Lift();
        Assert.True(d.Sink.Has("click RightClick"));
        Assert.True(d.Sink.Has("click MiddleClick"));
    }

    [Fact]
    public void Double_tap_drag_holds_left_button()
    {
        var d = new Rig(Defaults(s => { s.Taps.OneFingerTap = true; s.Taps.DoubleTapDrag = true; }));
        d.Frame((1, -500, -1000));
        d.Lift();
        d.Frame((2, -500, -1000));
        Assert.True(d.Sink.Has("Left down"));
        for (int i = 0; i < 30; i++) d.Frame((2, -500 + i * 10, -1000));
        d.Lift();
        Assert.Equal("Left up", d.Sink.Events.Last());
    }

    [Fact]
    public void Physical_click_without_app_buttons_is_left_to_windows()
    {
        var d = new Rig();
        Assert.False(d.Engine.OwnsButtons);
        d.Frame(PhysicalButtons.Left, (1, -500, -1000));
        d.Frame(PhysicalButtons.None, (1, -500, -1000));
        Assert.DoesNotContain(d.Sink.Events, e => e.StartsWith("Left") || e.StartsWith("Right"));
    }

    [Fact]
    public void Custom_split_decides_left_or_right()
    {
        var d = new Rig(Defaults(s => { s.Buttons.CustomSplit = true; s.Buttons.LeftRightSplitPercent = 70; }));
        // 60% across: left of a 70% split.
        int x = (int)(Surface.MinX + Surface.Width * 0.60);
        d.Frame(PhysicalButtons.Left, (1, x, -1000));
        d.Frame(PhysicalButtons.None, (1, x, -1000));
        Assert.Equal(new[] { "Left down", "Left up" }, d.Sink.Events.Where(e => !e.StartsWith("scroll")));
    }

    [Fact]
    public void Swap_buttons_reverses_physical_click()
    {
        var d = new Rig(Defaults(s => s.Buttons.SwapButtons = true));
        d.Frame(PhysicalButtons.Left, (1, -500, -1000));
        d.Frame(PhysicalButtons.None);
        Assert.True(d.Sink.Has("Right down"));
        Assert.True(d.Sink.Has("Right up"));
    }

    [Fact]
    public void Middle_click_in_center_zone()
    {
        var d = new Rig(Defaults(s => s.Buttons.MiddleClick = true));
        d.Frame(PhysicalButtons.Left, (1, 79, -1000));   // ~50% across
        d.Frame(PhysicalButtons.None);
        Assert.True(d.Sink.Has("Middle down"));
    }

    [Fact]
    public void Simultaneous_primary_secondary()
    {
        var d = new Rig(Defaults(s => s.Buttons.SimultaneousPrimarySecondary = true));
        d.Frame(PhysicalButtons.Left, (1, -700, -1000), (2, 900, -1000));
        d.Frame(PhysicalButtons.None);
        Assert.True(d.Sink.Has("Left down"));
        Assert.True(d.Sink.Has("Right down"));
    }

    [Fact]
    public void Momentum_continues_after_flick_and_decays()
    {
        var d = new Rig(Defaults(s => s.OneFinger.NavigateBackForward = false));
        d.Drag(1, (0, -1200), (0, 1200), frames: 20);
        d.Lift();
        int afterLift = d.Sink.ScrollV;
        Assert.True(d.Engine.MomentumActive);
        for (int i = 0; i < 400 && d.Engine.MomentumActive; i++) { d.Engine.Tick(d.Now); d.Now += 8; }
        Assert.True(d.Sink.ScrollV > afterLift);
        Assert.False(d.Engine.MomentumActive);
    }

    [Fact]
    public void New_touch_stops_momentum()
    {
        var d = new Rig(Defaults(s => s.OneFinger.NavigateBackForward = false));
        d.Drag(1, (0, -1200), (0, 1200), frames: 20);
        d.Lift();
        d.Engine.Tick(d.Now);
        d.Frame((3, 0, 0));
        Assert.False(d.Engine.MomentumActive);
    }

    [Fact]
    public void Resting_second_finger_does_not_block_one_finger_scroll()
    {
        var d = new Rig();
        for (int i = 0; i <= 60; i++)
            d.Frame((1, -200, -500 + i * 13), (2, 700, 300));   // finger 2 rests, finger 1 scrolls
        Assert.True(d.Sink.ScrollV > 0);
    }

    [Fact]
    public void Two_moving_fingers_do_not_one_finger_scroll()
    {
        var d = new Rig(Defaults(s => s.TwoFinger.SwipeUpTaskView = false));
        for (int i = 0; i <= 60; i++)
            d.Frame((1, -200, -500 + i * 13), (2, 500, -500 + i * 13));
        Assert.Equal(0, d.Sink.ScrollV);
    }

    [Fact]
    public void Side_touches_are_ignored_for_scrolling()
    {
        var d = new Rig();
        d.Drag(1, (Surface.MinX + 20, -500), (Surface.MinX + 20, 300), frames: 60);
        Assert.Equal(0, d.Sink.ScrollV);
    }

    [Fact]
    public void Grip_on_both_sides_ignores_gestures_when_enabled()
    {
        var d = new Rig(Defaults(s => s.Pointer.IgnoreInputIfLifted = true));
        for (int i = 0; i <= 60; i++)
            d.Frame((1, Surface.MinX + 20, 0), (2, Surface.MaxX - 20, 0), (3, 0, -500 + i * 13));
        Assert.Equal(0, d.Sink.ScrollV);
    }

    [Fact]
    public void Mm1_owns_motion_and_buttons()
    {
        var d = new Rig(model: DeviceModel.MagicMouse1);
        Assert.True(d.Engine.OwnsButtons);
        d.Engine.Process(new TouchFrame(0, PhysicalButtons.Left, 4, -2, new[] { new Touch(1, -500, -1000, 1, 1, 1, 0, 0x40) }), 0);
        Assert.True(d.Sink.Has("move 4 -2"));
        Assert.True(d.Sink.Has("Left down"));
    }

    [Fact]
    public void Settings_round_trip_through_json()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mt-{Guid.NewGuid():N}.json");
        try
        {
            var s = Defaults(x => { x.Scrolling.Speed = 77; x.Taps.TwoFingerAction = ClickAction.Back; });
            s.Save(path);
            var r = AppSettings.Load(path);
            Assert.Equal(77, r.Scrolling.Speed);
            Assert.Equal(ClickAction.Back, r.Taps.TwoFingerAction);
        }
        finally { File.Delete(path); }
    }
}

