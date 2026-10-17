using MagicMouse.Core.Driver;
using MagicMouse.Core.Protocol;
using MagicMouse.Core.Settings;

namespace MagicMouse.Core.Gestures;

/// <summary>
/// Turns touch frames into scrolling, swipes, taps and clicks. Not thread-safe: the host serializes
/// <see cref="Process"/> and <see cref="Tick"/>. Time is in milliseconds on the host's clock.
/// </summary>
public sealed class GestureEngine
{
    private sealed class Track
    {
        public int Id;
        public double StartX, StartY, LastX, LastY;
        public double StartTime;
        public double MaxDistance;
        public bool Ignored;
    }

    private enum Axis { None, Vertical, Horizontal }

    private readonly IInputSink _sink;
    private readonly DeviceModel _model;
    private AppSettings _s;

    private readonly Dictionary<int, Track> _tracks = new();

    // Session = from first finger down until all fingers are up.
    private bool _sessionActive;
    private double _sessionStart;
    private int _sessionMaxFingers;
    private bool _sessionConsumed;      // a swipe fired or the session was rejected
    private bool _sessionHadClick;
    private double _sessionSensorMotion;
    private double _sessionTapX, _sessionTapY;

    // Scrolling
    private bool _scrolling;
    private Axis _axis;
    private int _scrollFingerCount;
    private double _prevCx, _prevCy, _scrollOriginX, _scrollOriginY;
    private double _accV, _accH, _heldH, _heldV;
    private double _startDx, _startDy, _recentDx, _recentDy, _undecidedH;   // axis lock
    private double _velV, _velH;        // wheel units per ms
    private double _lastScrollTime;
    private bool _momentum;
    private double _lastTick;

    // Buttons
    private PhysicalButtons _prevButtons;
    private readonly HashSet<MouseButton> _pressed = new();
    private double _blockUntil;
    private double _lastRealClick = double.NegativeInfinity;

    // Taps / drag
    private double _lastLeftTapEnd = double.NegativeInfinity;
    private bool _dragActive;

    // Two-finger toggle (Normal <-> Task View)
    private bool _inTaskView;

    // Session-wide max per-finger movement for tap detection (tracks are dropped when fingers lift).
    private double _maxTrackDistance;

    public GestureEngine(AppSettings settings, IInputSink sink, DeviceModel model)
    {
        _s = settings;
        _sink = sink;
        _model = model;
    }

    public void UpdateSettings(AppSettings settings) => _s = settings;

    /// <summary>App generates button events (driver masks the physical ones). MM1 always: its
    /// multitouch report replaces the mouse report, so Windows sees neither motion nor clicks.</summary>
    public bool OwnsButtons => _model == DeviceModel.MagicMouse1 || _s.AppHandlesButtons;
    public bool OwnsMotion => _model == DeviceModel.MagicMouse1;

    public bool MomentumActive => _momentum;
    public IReadOnlyList<Touch> LastTouches { get; private set; } = Array.Empty<Touch>();

    // ------------------------------------------------------------------ frame processing

    public void Process(TouchFrame f, double now)
    {
        var e = _s.Engine;
        var down = f.Touches.Where(t => t.IsDown).ToList();
        LastTouches = down;

        if (OwnsMotion && (f.Dx != 0 || f.Dy != 0)) _sink.Move(f.Dx, f.Dy);

        HandleButtons(f, down, now);
        UpdateTracks(down, now);

        if (!_s.Enabled) { _prevButtons = f.Buttons; return; }

        if (down.Count > 0 && _momentum) StopMomentum();

        if (down.Count > 0 && !_sessionActive) BeginSession(now);
        if (_sessionActive)
        {
            _sessionMaxFingers = Math.Max(_sessionMaxFingers, ActiveTracks().Count());
            _sessionSensorMotion += Math.Abs(f.Dx) + Math.Abs(f.Dy);
            if (f.Buttons != PhysicalButtons.None) _sessionHadClick = true;
            if (_s.Pointer.IgnoreInputIfLifted && IsGripped()) _sessionConsumed = true;
        }

        bool blocked = now < _blockUntil || f.Buttons != PhysicalButtons.None || _dragActive;
        bool mouseMoving = Math.Abs(f.Dx) + Math.Abs(f.Dy) > e.StopScrollMoveOffset;

        if (down.Count == 0)
        {
            // Lift frame: keep scroll velocity so EndSession can start momentum.
        }
        else if (_sessionActive && !_sessionConsumed && !blocked)
        {
            if (TrySwipes(now)) { /* consumed */ }
            else if (_s.Scrolling.Enabled)
            {
                if (_s.Scrolling.StopOnMouseMove && mouseMoving) ResetScrollOrigin();
                else UpdateScroll(now);
            }
        }
        else if (_scrolling)
        {
            ResetScrollOrigin();
        }

        if (down.Count == 0 && _sessionActive) EndSession(now);
        _prevButtons = f.Buttons;
    }

    /// <summary>Drive momentum scrolling; call every few ms.</summary>
    public void Tick(double now)
    {
        double dt = _lastTick > 0 ? Math.Min(now - _lastTick, 50) : 0;
        _lastTick = now;
        if (!_momentum || dt <= 0) return;

        double decay = Math.Exp(-_s.Engine.MomentumDecayPerSecond * dt / 1000.0);
        _velV *= decay;
        _velH *= decay;
        EmitScroll(_velV * dt, _velH * dt);
        if (Math.Abs(_velV) < 0.02 && Math.Abs(_velH) < 0.02) StopMomentum();
    }

    // ------------------------------------------------------------------ tracks / sessions

    private void UpdateTracks(List<Touch> down, double now)
    {
        var e = _s.Engine;
        var seen = new HashSet<int>();
        foreach (var t in down)
        {
            seen.Add(t.Id);
            double x = Mirror(t.X), y = t.Y;
            if (!_tracks.TryGetValue(t.Id, out var tr))
            {
                tr = new Track
                {
                    Id = t.Id, StartX = x, StartY = y, LastX = x, LastY = y, StartTime = now,
                    Ignored = InIgnoreArea(x, y, e),
                };
                _tracks[t.Id] = tr;
            }
            tr.LastX = x;
            tr.LastY = y;
            tr.MaxDistance = Math.Max(tr.MaxDistance, Math.Abs(x - tr.StartX) + Math.Abs(y - tr.StartY));
        }
        foreach (var id in _tracks.Keys.Where(k => !seen.Contains(k)).ToList()) _tracks.Remove(id);
    }

    private IEnumerable<Track> ActiveTracks() => _tracks.Values.Where(t => !t.Ignored);

    private double Mirror(double x) => _s.Buttons.LeftHanded ? Surface.MinX + Surface.MaxX - x : x;

    private static bool InIgnoreArea(double x, double y, EngineSettings e) =>
        x < Surface.MinX + e.IgnoreSideTouchesLeft ||
        x > Surface.MaxX - e.IgnoreSideTouchesRight ||
        y > Surface.MaxY - e.IgnoreBottomTouches;

    private bool IsGripped()
    {
        var e = _s.Engine;
        bool left = _tracks.Values.Any(t => t.LastX < Surface.MinX + e.IgnoreSideTouchesLeft);
        bool right = _tracks.Values.Any(t => t.LastX > Surface.MaxX - e.IgnoreSideTouchesRight);
        return left && right;
    }

    private void BeginSession(double now)
    {
        _sessionActive = true;
        _sessionStart = now;
        _sessionMaxFingers = 0;
        _sessionConsumed = false;
        _sessionHadClick = false;
        _sessionSensorMotion = 0;
        var first = ActiveTracks().FirstOrDefault();
        _sessionTapX = first?.StartX ?? 0;
        _sessionTapY = first?.StartY ?? 0;
        _heldH = _heldV = 0;
        ResetScrollOrigin();

        // Double-tap drag: a new touch shortly after a left tap holds the left button.
        var e = _s.Engine;
        double since = now - _lastLeftTapEnd;
        if (_s.Taps.OneFingerTap && _s.Taps.DoubleTapDrag && since >= 0 && since <= e.DoubleTapDragMaxDelayMs)
        {
            _dragActive = true;
            _sink.Button(MouseButton.Left, true);
        }
    }

    private void EndSession(double now)
    {
        var e = _s.Engine;
        if (_dragActive)
        {
            _dragActive = false;
            _sink.Button(MouseButton.Left, false);
            _sessionConsumed = true;
            _lastLeftTapEnd = double.NegativeInfinity;
        }

        if (_scrolling && _s.Scrolling.Smooth && _s.Scrolling.Momentum &&
            now - _lastScrollTime < 40 && (Math.Abs(_velV) > 0.15 || Math.Abs(_velH) > 0.15))
        {
            _momentum = true;
        }

        if (!_sessionConsumed && !_scrolling) TryTap(now);

        _sessionActive = false;
        _scrolling = false;
        _tracks.Clear();
    }

    // ------------------------------------------------------------------ swipes

    private bool TrySwipes(double now)
    {
        var e = _s.Engine;
        var active = ActiveTracks().ToList();
        double age = now - _sessionStart;
        if (age > e.SwipeMaxDurationMs || active.Count == 0) return false;

        double dx = active.Average(t => t.LastX - t.StartX);
        double dy = active.Average(t => t.LastY - t.StartY);

        if (active.Count == 1 && _sessionMaxFingers == 1 && _s.OneFinger.NavigateBackForward)
        {
            double threshold = 3 * e.SwipeHorizontalOffset * (1.5 - _s.OneFinger.Sensitivity / 100.0);
            if (Math.Abs(dx) >= threshold && Math.Abs(dx) > 2 * Math.Abs(dy))
            {
                bool back = dx > 0 ^ _s.OneFinger.SwapDirection;
                _sink.Click(back ? ClickAction.Back : ClickAction.Forward);
                ConsumeSwipe(now, e.SwipeNavigationBlockMs);
                return true;
            }
        }

        if (active.Count == 2 && _sessionMaxFingers == 2)
        {
            var tf = _s.TwoFinger;
            double scale = 6 * (1.5 - tf.Sensitivity / 100.0);
            if (tf.SwitchDesktops && Math.Abs(dx) >= e.Swipe2HorizontalOffset * scale && Math.Abs(dx) > 2 * Math.Abs(dy))
            {
                // Fingers moving left reveal the desktop on the right (macOS convention).
                bool next = dx < 0 ^ tf.SwapDesktopDirection;
                _sink.Chord(VKey.Control, VKey.LWin, next ? VKey.Right : VKey.Left);
                ConsumeSwipe(now, e.SwipeOtherBlockMs);
                return true;
            }
            if (Math.Abs(dy) >= e.Swipe2VerticalOffset * scale && Math.Abs(dy) > 2 * Math.Abs(dx))
            {
                if (dy < 0 && tf.SwipeUpTaskView)
                {
                    _sink.Chord(_inTaskView ? new[] { VKey.Escape } : new[] { VKey.LWin, VKey.Tab });
                    _inTaskView = !_inTaskView;
                    ConsumeSwipe(now, e.SwipeOtherBlockMs);
                    return true;
                }
                if (dy > 0 && tf.SwipeDownShowDesktop)
                {
                    _sink.Chord(VKey.LWin, VKey.D);   // Win+D toggles Normal <-> Desktop by itself
                    ConsumeSwipe(now, e.SwipeOtherBlockMs);
                    return true;
                }
            }
        }
        return false;
    }

    private void ConsumeSwipe(double now, int blockMs)
    {
        _sessionConsumed = true;
        _blockUntil = now + blockMs;
        _heldH = _heldV = 0;
        _scrolling = false;
    }

    /// <summary>Whether a swipe could still fire on this axis, so scroll output must be held back.</summary>
    private bool HoldAxis(Axis axis, int fingers, double now)
    {
        if (now - _sessionStart > _s.Engine.SwipeMaxDurationMs) return false;
        if (fingers == 1)
            return axis == Axis.Horizontal && _s.OneFinger.NavigateBackForward;
        if (fingers == 2)
            return axis == Axis.Horizontal ? _s.TwoFinger.SwitchDesktops
                : _s.TwoFinger.SwipeUpTaskView || _s.TwoFinger.SwipeDownShowDesktop;
        return false;
    }

    // ------------------------------------------------------------------ scrolling

    private void ResetScrollOrigin()
    {
        _scrolling = false;
        _axis = Axis.None;
        _scrollFingerCount = 0;
        _accV = _accH = 0;
        _velV = _velH = 0;
    }

    private bool FingerCountScrolls(int n) => _s.Scrolling.Fingers switch
    {
        ScrollFingers.One => n == 1,
        ScrollFingers.Two => n == 2,
        _ => n is 1 or 2,
    };

    private void UpdateScroll(double now)
    {
        var sc = _s.Scrolling;
        var e = _s.Engine;
        var active = ActiveTracks().ToList();
        if (sc.Fingers == ScrollFingers.One && active.Count > 1)
        {
            // A resting finger or thumb must not block one-finger scrolling: follow the one that moves,
            // provided every other finger is essentially still (otherwise it's a multi-finger gesture).
            var mover = active.MaxBy(t => t.MaxDistance)!;
            if (mover.MaxDistance > e.TapOffsetAllowed / 2.0 &&
                active.All(t => t == mover || t.MaxDistance <= e.TapOffsetAllowed / 4.0))
                active = new List<Track> { mover };
        }
        int n = active.Count;
        if (!FingerCountScrolls(n)) { if (_scrolling) ResetScrollOrigin(); return; }

        double cx = active.Average(t => t.LastX), cy = active.Average(t => t.LastY);
        // Restart from the new position (without jumping) when the finger set changes.
        int key = n == 1 ? 100 + active[0].Id : n;
        if (_scrollFingerCount != key)
        {
            _scrollFingerCount = key;
            _prevCx = _scrollOriginX = cx;
            _prevCy = _scrollOriginY = cy;
            _lastScrollTime = now;
            _scrolling = false;
            _axis = Axis.None;
            return;
        }

        if (!_scrolling && now - active.Max(t => t.StartTime) < e.TouchSettleMs)
        {
            // Finger still landing: its centroid drifts without real movement. Keep moving the origin.
            _prevCx = _scrollOriginX = cx;
            _prevCy = _scrollOriginY = cy;
            _lastScrollTime = now;
            return;
        }

        if (!_scrolling)
        {
            double tdx = cx - _scrollOriginX, tdy = cy - _scrollOriginY;
            if (Math.Abs(tdx) + Math.Abs(tdy) < e.ScrollStartOffset) return;
            _scrolling = true;
            _axis = Axis.None;   // decided by UpdateAxisLock once the finger has travelled far enough
            _startDx = _startDy = _recentDx = _recentDy = _undecidedH = 0;
        }

        double dx = cx - _prevCx, dy = cy - _prevCy;
        double dt = Math.Max(now - _lastScrollTime, 1);
        _prevCx = cx;
        _prevCy = cy;
        _lastScrollTime = now;

        bool undecided = false;
        if (sc.NoHorizontal) dx = 0;
        else if (sc.OrthogonalOnly)
        {
            UpdateAxisLock(dx, dy, e);
            if (_axis == Axis.Vertical) dx = 0;
            else if (_axis == Axis.Horizontal) dy = 0;
            else undecided = true;   // scroll vertically right away, hold horizontal until decided
        }

        double gain = 0.1 + sc.Speed / 100.0;
        if (sc.Accelerated)
        {
            double speed = Math.Sqrt(dx * dx + dy * dy) / dt;       // device units per ms
            gain *= 1 + sc.Acceleration / 100.0 * 0.4 * speed;
        }
        gain *= e.ScrollStepFactor / 120.0;

        // Natural direction: content follows the finger.
        double wv = dy * gain * (sc.InverseVertical ? -1 : 1);
        double wh = -dx * gain * (sc.InverseHorizontal ? -1 : 1);
        if (_s.Buttons.LeftHanded) wh = -wh; // undo mirroring for direction

        if (undecided) { _undecidedH += wh; wh = 0; }
        else if (_undecidedH != 0)
        {
            if (_axis == Axis.Horizontal) wh += _undecidedH;   // locked horizontal: release what we held
            _undecidedH = 0;                                   // locked vertical: drop it
        }

        _velV = 0.6 * _velV + 0.4 * (wv / dt);
        _velH = 0.6 * _velH + 0.4 * (wh / dt);

        if (HoldAxis(Axis.Horizontal, n, now)) { _heldH += wh; wh = 0; }
        else if (_heldH != 0) { wh += _heldH; _heldH = 0; }
        if (HoldAxis(Axis.Vertical, n, now)) { _heldV += wv; wv = 0; }
        else if (_heldV != 0) { wv += _heldV; _heldV = 0; }

        EmitScroll(wv, wh);
    }

    /// <summary>
    /// Orthogonal scrolling: pick the axis from the first ~1 mm of travel, biased to vertical (a diagonal
    /// stroke scrolls vertically), and switch later if the finger clearly changes direction.
    /// </summary>
    private void UpdateAxisLock(double dx, double dy, EngineSettings e)
    {
        _startDx += dx;
        _startDy += dy;
        _recentDx = _recentDx * 0.85 + dx;   // ~75 ms window at ~90 Hz
        _recentDy = _recentDy * 0.85 + dy;
        double ratio = e.HorizontalLockRatio;

        if (_axis == Axis.None)
        {
            double ax = Math.Abs(_startDx), ay = Math.Abs(_startDy);
            if (ax + ay < e.AxisLockDistance) return;
            _axis = ax > ratio * ay ? Axis.Horizontal : Axis.Vertical;
            return;
        }

        double rx = Math.Abs(_recentDx), ry = Math.Abs(_recentDy);
        if (rx + ry < e.AxisLockDistance) return;
        if (_axis == Axis.Vertical && rx > ratio * ry) _axis = Axis.Horizontal;
        else if (_axis == Axis.Horizontal && ry > ratio * rx) _axis = Axis.Vertical;
    }

    private void EmitScroll(double wv, double wh)
    {
        _accV += wv;
        _accH += wh;
        int step = _s.Scrolling.Smooth ? 1 : 120;
        int outV = (int)(_accV / step) * step;
        int outH = (int)(_accH / step) * step;
        _accV -= outV;
        _accH -= outH;
        if (outV != 0 || outH != 0) _sink.Scroll(outV, outH);
    }

    private void StopMomentum()
    {
        _momentum = false;
        _velV = _velH = 0;
        _accV = _accH = 0;
    }

    // ------------------------------------------------------------------ taps

    private void TryTap(double now)
    {
        var e = _s.Engine;
        var t = _s.Taps;
        double duration = now - _sessionStart;
        if (duration > e.TapDelayMs || _sessionHadClick || _sessionSensorMotion > 40) return;
        if (t.IgnoreIfRealClick && now - _lastRealClick < e.IgnoreRealClickTapWindowMs) return;
        if (_maxTrackDistance > e.TapOffsetAllowed) return;

        ClickAction action = ClickAction.None;
        switch (_sessionMaxFingers)
        {
            case 1 when t.OneFingerTap:
            {
                double x = _sessionTapX, y = _sessionTapY;
                if (t.FilterIgnoreAreas && InIgnoreArea(x, y, e)) return;
                if (t.UseTapArea && y - Surface.MinY > e.TapAreaFromTop) return;
                double pct = (x - Surface.MinX) * 100.0 / Surface.Width;
                if (t.MiddleZone && pct >= e.MiddleClickZoneStartPercent && pct <= e.MiddleClickZoneEndPercent)
                    action = t.MiddleZoneAction;
                else if (pct < _s.Buttons.LeftRightSplitPercent)
                    action = t.LeftZone ? t.LeftZoneAction : ClickAction.None;
                else
                    action = t.RightZone ? t.RightZoneAction : ClickAction.None;
                break;
            }
            case 2 when t.TwoFingerTap: action = t.TwoFingerAction; break;
            case 3 when t.ThreeFingerTap: action = t.ThreeFingerAction; break;
        }
        if (action == ClickAction.None) return;
        _sink.Click(ApplySwap(action));
        if (_sessionMaxFingers == 1 && action == ClickAction.LeftClick) _lastLeftTapEnd = now;
    }

    private ClickAction ApplySwap(ClickAction a) => !_s.Buttons.SwapButtons ? a : a switch
    {
        ClickAction.LeftClick => ClickAction.RightClick,
        ClickAction.RightClick => ClickAction.LeftClick,
        _ => a,
    };

    // ------------------------------------------------------------------ physical buttons

    private void HandleButtons(TouchFrame f, List<Touch> down, double now)
    {
        // Runs before UpdateTracks, so on the final (no-touch) frame the tracks still hold their last positions.
        if (!_sessionActive) _maxTrackDistance = 0;
        else if (_tracks.Count > 0) _maxTrackDistance = Math.Max(_maxTrackDistance, _tracks.Values.Max(t => t.MaxDistance));

        bool wasDown = _prevButtons != PhysicalButtons.None;
        bool isDown = f.Buttons != PhysicalButtons.None;

        if (isDown && !wasDown)
        {
            _lastRealClick = now;
            StopMomentum();
            if (OwnsButtons)
            {
                foreach (var b in DecideButtons(f.Buttons, down))
                {
                    _pressed.Add(b);
                    _sink.Button(b, true);
                }
            }
        }
        else if (!isDown && wasDown)
        {
            foreach (var b in _pressed) _sink.Button(b, false);
            _pressed.Clear();
            _blockUntil = Math.Max(_blockUntil, now + _s.Engine.BlockTimeAfterResetMs);
            ResetScrollOrigin();
        }
    }

    private IEnumerable<MouseButton> DecideButtons(PhysicalButtons raw, List<Touch> down)
    {
        var b = _s.Buttons;
        var e = _s.Engine;
        var touches = down.Select(t => (X: Mirror(t.X), t.Y)).Where(t => !InIgnoreArea(t.X, t.Y, e)).ToList();
        double split = Surface.MinX + Surface.Width * b.LeftRightSplitPercent / 100.0;

        if (b.MiddleClick)
        {
            if (b.MiddleClickMode == MiddleClickMode.CenterZone && touches.Count == 1)
            {
                double pct = (touches[0].X - Surface.MinX) * 100.0 / Surface.Width;
                if (pct >= e.MiddleClickZoneStartPercent && pct <= e.MiddleClickZoneEndPercent)
                    return new[] { MouseButton.Middle };
            }
            if (b.MiddleClickMode == MiddleClickMode.TwoFingersApart && touches.Count == 2 &&
                Math.Abs(touches[0].X - touches[1].X) >= e.MiddleClickTwoFingerMinDistance)
                return new[] { MouseButton.Middle };
        }

        if (b.SimultaneousPrimarySecondary && touches.Count >= 2 &&
            touches.Any(t => t.X < split) && touches.Any(t => t.X >= split))
            return new[] { MouseButton.Left, MouseButton.Right };

        MouseButton result;
        if (touches.Count > 0 && (b.CustomSplit || b.LeftHanded || _model == DeviceModel.MagicMouse1))
        {
            // The front-most finger decides (smallest Y = nearest the front edge).
            var front = touches.MinBy(t => t.Y);
            result = front.X < split ? MouseButton.Left : MouseButton.Right;
        }
        else
        {
            result = raw.HasFlag(PhysicalButtons.Right) ? MouseButton.Right : MouseButton.Left;
            if (b.LeftHanded) result = result == MouseButton.Left ? MouseButton.Right : MouseButton.Left;
        }
        if (b.SwapButtons) result = result == MouseButton.Left ? MouseButton.Right : MouseButton.Left;
        return new[] { result };
    }

    /// <summary>Release anything held (called when disabling or on device loss).</summary>
    public void Reset()
    {
        foreach (var b in _pressed) _sink.Button(b, false);
        _pressed.Clear();
        if (_dragActive) { _sink.Button(MouseButton.Left, false); _dragActive = false; }
        StopMomentum();
        ResetScrollOrigin();
        _tracks.Clear();
        _sessionActive = false;
        _prevButtons = PhysicalButtons.None;
    }

    /// <summary>Keyboard shortcuts toggle state; resync if the user left Task View some other way.</summary>
    public void ResetViewToggles() => _inTaskView = false;
}
