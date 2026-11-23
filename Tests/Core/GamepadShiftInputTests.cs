using FH6Telemetry;

internal static class GamepadShiftInputTests
{
    public static void Register(Action<string, Action> run)
    {
        run("兼容换挡：保留实体手柄优先权，键盘组合顺序按下和释放", () =>
        {
            var reports = new List<(byte[] Keys, bool Release)>();
            var manual = true;
            var clock = new ManualClock();
            using var input = new KeyboardShiftInput(() => true, () => manual, _ => false,
                (keys, release) => { reports.Add((keys, release)); return keys.Length; }, physicalStateIsReliable: true, clock);
            var trace = new List<ShiftTraceEvent>();
            input.SetTraceSink(trace.Add);
            var settings = new AutomaticShiftSettings();
            Check(input.ConnectAsync(settings, CancellationToken.None).GetAwaiter().GetResult() is null, "应启用兼容模式");
            Check(input.TryShift(ShiftDirection.Down, settings) == ShiftInputResult.Busy && reports.Count == 0,
                "实体手柄正在换挡时不能同时模拟键盘");
            manual = false;
            Check(input.TryShift(ShiftDirection.Down, settings) == ShiftInputResult.Sent, "应发送降挡、离合和补油");
            Check(reports.Count == 1 && !reports[0].Release && reports[0].Keys.SequenceEqual(new byte[] { 0xA0, 0x57 }),
                "接到降挡请求时先踩离合和补油");
            clock.Advance(25);
            Check(reports.Count == 2 && !reports[1].Release && reports[1].Keys.SequenceEqual(new byte[] { 0x51 }),
                "离合先于换挡按键 25 毫秒");
            clock.Advance(120);
            Check(reports.Count == 3 && reports[2].Release && reports[2].Keys.SequenceEqual(new byte[] { 0x51 }),
                "换挡键应先抬起");
            clock.Advance(10);
            Check(reports.Count == 4 && reports[3].Release && reports[3].Keys.SequenceEqual(new byte[] { 0x57, 0xA0 }),
                "最后应依次松开补油和离合");
            Check(trace.Select(action => (action.Action, action.VirtualKeysHex, action.AcceptedKeys)).SequenceEqual(
                [("keyboard_down", "A057", (int?)2), ("keyboard_down", "51", 1),
                 ("keyboard_up", "51", 1), ("keyboard_up", "57A0", 2)]), "记录须包含 Windows 接受的键位和每一步动作");
        });

        run("兼容换挡：Windows 部分接收输入时只回滚已按的键", () =>
        {
            byte[] released = [];
            using var input = new KeyboardShiftInput(() => true, () => false, _ => false,
                (keys, release) => { if (release) released = keys; return release ? keys.Length : 1; }, physicalStateIsReliable: true);
            var settings = new AutomaticShiftSettings();
            input.ConnectAsync(settings, CancellationToken.None).GetAwaiter().GetResult();
            Check(input.TryShift(ShiftDirection.Down, settings) == ShiftInputResult.Failed &&
                  released.SequenceEqual(new byte[] { 0xA0 }) && input.Error is not null, "部分发送失败须释放已经按住的离合");
        });

        run("兼容换挡：实体按住补油键时降挡省略补油", () =>
        {
            using var keyboard = new KeyboardDrive();
            keyboard.Physical.Add(0x57);
            Check(keyboard.Shift(ShiftDirection.Down) == ShiftInputResult.Sent, "按住油门仍应接受降挡");
            keyboard.Clock.Advance(155);
            Check(keyboard.Sent.Any(input => !input.Release && input.Keys.Contains((byte)0x51)) &&
                  keyboard.Sent.All(input => !input.Keys.Contains((byte)0x57)), "实体油门保持，降挡键应正常发出");
        });

        run("兼容换挡：脉冲中实体按下的键不发送抬起", () =>
        {
            using var keyboard = new KeyboardDrive();
            Check(keyboard.Shift(ShiftDirection.Down) == ShiftInputResult.Sent, "应开始降挡");
            keyboard.Clock.Advance(25);
            keyboard.Physical.Add(0x57);
            keyboard.Clock.Advance(130);
            var ups = keyboard.Sent.Where(input => input.Release).SelectMany(input => input.Keys);
            Check(ups.SequenceEqual(new byte[] { 0x51, 0xA0 }), "只释放降挡和离合，实体油门应保持");
        });

        run("兼容换挡：脉冲中实体按换挡键立即让出", () =>
        {
            using var keyboard = new KeyboardDrive();
            keyboard.Shift(ShiftDirection.Up);
            keyboard.Clock.Advance(25);
            keyboard.Physical.Add(0x45);
            keyboard.Clock.Advance(10);
            Check(keyboard.Input.ManualShiftHeld &&
                  keyboard.Sent.Where(input => input.Release).SelectMany(input => input.Keys).SequenceEqual(new byte[] { 0xA0 }),
                "玩家接过升挡键后保留该键，只释放自己的离合");
        });

        run("兼容换挡：松键后不会把自己的注入误判为手动", () =>
        {
            using var keyboard = new KeyboardDrive();
            keyboard.Shift(ShiftDirection.Up);
            keyboard.Clock.Advance(155);
            Check(keyboard.Sent.Any(input => input.Release && input.Keys.Contains((byte)0x45)) && !keyboard.Input.ManualShiftHeld,
                "即使系统仍认为合成键按下，可靠模式也只看实体键");
        });

        run("兼容换挡：离合提前 25 毫秒且齿轮键先释放 10 毫秒", () =>
        {
            using var keyboard = new KeyboardDrive();
            keyboard.Shift(ShiftDirection.Down);
            keyboard.Clock.Advance(24);
            Check(keyboard.Sent.Count == 1, "离合阶段不能提前发送换挡键");
            keyboard.Clock.Advance(1);
            keyboard.Clock.Advance(120);
            keyboard.Clock.Advance(9);
            Check(keyboard.Sent.Count == 3, "换挡键松开后离合仍应暂时保持");
            keyboard.Clock.Advance(1);
            Check(keyboard.Sent.Select(report => report.Time).SequenceEqual(new long[] { 0, 25, 145, 155 }),
                "默认降挡应在 0、25、145 和 155 毫秒发送四个事件组");
        });

        run("兼容换挡：回退模式保持忙碌判断与自持键排除", () =>
        {
            using var keyboard = new KeyboardDrive(reliable: false);
            keyboard.Physical.Add(0x57);
            Check(keyboard.Shift(ShiftDirection.Down) == ShiftInputResult.Busy && keyboard.Sent.Count == 0,
                "无法区分来源时，已按下的补油键仍禁止注入组合");
            keyboard.Physical.Clear();
            Check(keyboard.Shift(ShiftDirection.Up) == ShiftInputResult.Sent, "应在按键松开后换挡");
            keyboard.Clock.Advance(25);
            Check(!keyboard.Input.ManualShiftHeld && keyboard.Combined.Contains(0x45), "回退模式应排除自己的注入");
            keyboard.Input.Release();
            Check(keyboard.Sent.Last().Release && keyboard.Sent.Last().Keys.SequenceEqual(new byte[] { 0x45, 0xA0 }),
                "回退模式应逆序释放所有自持键");
        });

        run("兼容换挡：未配置离合时只发送换挡键", () =>
        {
            using var keyboard = new KeyboardDrive(settings: new AutomaticShiftSettings { KeyboardClutch = "" });
            Check(keyboard.Shift(ShiftDirection.Down) == ShiftInputResult.Sent, "普通手动挡应直接换挡");
            keyboard.Clock.Advance(120);
            Check(keyboard.Sent.Count == 2 && keyboard.Sent[0].Time == 0 && keyboard.Sent[1].Time == 120 &&
                  keyboard.Sent.All(report => report.Keys.SequenceEqual(new byte[] { 0x51 })),
                "无离合组合只按下和释放降挡键");
        });

        run("兼容换挡：关闭自动离合仍能使用已绑定的升降挡键", () =>
        {
            using var keyboard = new KeyboardDrive(settings: new AutomaticShiftSettings { AutomaticClutch = false });
            Check(keyboard.Shift(ShiftDirection.Down) == ShiftInputResult.Sent, "应直接发送降挡键");
            keyboard.Clock.Advance(120);
            Check(keyboard.Sent.Count == 2 && keyboard.Sent.All(report => report.Keys.SequenceEqual(new byte[] { 0x51 })),
                "关闭自动离合后，只发送 Q 的按下和释放");
        });

        run("兼容换挡：无需补油或低速回挡时传入零补油只踩离合换挡", () =>
        {
            using var keyboard = new KeyboardDrive();
            Check(keyboard.Input.TryShift(ShiftDirection.Down, new AutomaticShiftSettings(), downshiftTrigger: 0) == ShiftInputResult.Sent,
                "零补油降挡应正常发送");
            keyboard.Clock.Advance(155);
            Check(keyboard.Sent.Any(report => !report.Release && report.Keys.Contains((byte)0x51)) &&
                  keyboard.Sent.All(report => !report.Keys.Contains((byte)0x57)),
                "显式传入 0 补油时不应按下补油键 W");
        });

        run("兼容换挡：提前踩离合阶段失焦会取消换挡", () =>
        {
            using var keyboard = new KeyboardDrive();
            keyboard.Shift(ShiftDirection.Down);
            keyboard.Foreground = false;
            keyboard.Clock.Advance(155);
            Check(keyboard.Sent.Count == 2 && keyboard.Sent[1].Release &&
                  keyboard.Sent[1].Keys.SequenceEqual(new byte[] { 0x57, 0xA0 }),
                "失焦应立即释放已按下的补油和离合，且不发送换挡键");
        });

        run("手柄转发：实体手柄对外可见时，拒绝创建第二只手柄", () =>
        {
            var attached = false;
            try
            {
                using var relay = IsolatedGamepadRelay.Connect(slot => slot == 0 ? new ShiftGamepadReport() : null,
                    () => 1, _ => { attached = true; return new TestGamepad(); });
                throw new Exception("同时对外暴露实体手柄时应拒绝连接");
            }
            catch (InvalidOperationException error)
            {
                Check(!attached && error.Message.Contains("HidHide"), "应在添置虚拟设备前给出隔离原因");
            }
        });

        run("手柄转发：始终保留实体摇杆、制动和按键，主动降挡只叠加换挡组合", () =>
        {
            var pad = new TestGamepad();
            var physical = new ShiftGamepadReport(Buttons: 0x1101, RightTrigger: 92, LeftTrigger: 80,
                LeftX: -11000, LeftY: 600, RightX: 220, RightY: -550);
            var deviceCreated = false;
            ShiftGamepadReport? Read(int slot) => slot switch
            {
                0 => physical,
                1 when deviceCreated => default(ShiftGamepadReport),
                _ => null,
            };
            using var input = new GamepadShiftInput(
                () => IsolatedGamepadRelay.Connect(Read, () => deviceCreated ? 2 : 0, _ => { deviceCreated = true; return pad; }),
                () => true, () => false, new FrozenClock());
            Check(input.ConnectAsync(new AutomaticShiftSettings { GamepadMode = GamepadShiftMode.Combination, AutomaticClutch = false },
                CancellationToken.None).GetAwaiter().GetResult() is null, "应连接实体手柄转发");
            Check(input.IsForwarding && pad.Reports.Last() == physical, "接入时必须马上转发完整实体状态");
            var settings = new AutomaticShiftSettings { GamepadMode = GamepadShiftMode.Combination };
            Check(input.TryShift(ShiftDirection.Down, settings) == ShiftInputResult.Sent &&
                  pad.Reports.Last() == physical with { RightTrigger = 255, Buttons = (ushort)(physical.Buttons | 0x4040) },
                "自动降挡与转向、制动、相机摇杆应出现在同一份手柄报告");
            Check(input.DriverPedals == new ShiftPedals(92 / 255.0, 80 / 255.0),
                "驾驶意图应读取实体扳机，即使叠加后的补油是满油");
            input.Release();
            Check(pad.Reports.Last() == physical, "取消脉冲只应清除合成输入");
            physical = physical with { Buttons = 0x2101, RightTrigger = 22, LeftX = 7000 };
            Check(SpinWait.SpinUntil(() => pad.Reports.Last() == physical, 1000), "即使不换挡也要继续转发实体手柄");
            Check(input.TryShift(ShiftDirection.Up, settings) == ShiftInputResult.Busy, "实体 B 优先于自动换挡");
        });

        run("手柄转发：内置布局对手动 B 和 X 同样应用离合及降挡补油", () =>
        {
            var pad = new TestGamepad();
            var physical = new ShiftGamepadReport(0x4101, 92, 136, LeftX: -5300, RightY: 1500);
            var deviceCreated = false;
            ShiftGamepadReport? Read(int slot) => slot == 0 ? physical
                : slot == 1 && deviceCreated ? default(ShiftGamepadReport) : null;
            using var input = new GamepadShiftInput(
                () => IsolatedGamepadRelay.Connect(Read, () => deviceCreated ? 2 : 0, _ => { deviceCreated = true; return pad; }),
                () => true, () => false, new FrozenClock());
            var direct = new AutomaticShiftSettings { GamepadMode = GamepadShiftMode.Combination };
            Check(input.ConnectAsync(direct, CancellationToken.None).GetAwaiter().GetResult() is null, "应连接内置布局");
            Check(pad.Reports.Last() == physical with { Buttons = 0x4141, RightTrigger = 255 },
                "手动 X 应同时保留转向制动，按下 L3 并把右扳机用于补油");

            physical = physical with { Buttons = 0x2101 };
            Check(SpinWait.SpinUntil(() => pad.Reports.Last().Buttons == 0x2141, 1000) &&
                  pad.Reports.Last().RightTrigger == 92, "手动 B 应叠加离合并保留当前油门");
            physical = physical with { Buttons = 0x6101 };
            Check(SpinWait.SpinUntil(() => pad.Reports.Last() == physical with { Buttons = 0x6141, RightTrigger = 255 }, 1000),
                "两组普通按压同时活跃时，应合并按钮、离合和补油");
            input.SetAutomaticClutch(false);
            Check(SpinWait.SpinUntil(() => pad.Reports.Last() == physical, 1000), "关闭时实时停止给实体 B/X 加离合和补油");
            physical = physical with { Buttons = 0x0101 };
            Check(SpinWait.SpinUntil(() => pad.Reports.Last() == physical, 1000),
                "松开换挡按键时应同时释放组合动作并恢复实体扳机");
        });

        run("手柄换挡：截图模式在同一报告输出 B+L3 或 X+L3+RT", () =>
        {
            var up = ShiftGamepadReport.For(ShiftDirection.Up);
            var down = ShiftGamepadReport.For(ShiftDirection.Down);
            Check(up.Buttons == 0x2040 && up.RightTrigger == 0, "升挡应同时按 B 和左摇杆，RT 保持松开");
            Check(down.Buttons == 0x4040 && down.RightTrigger == 255, "降挡应同时按 X、左摇杆和 RT");
        });

        run("手柄换挡：自动离合开关覆盖 B/X 和内置补油", () =>
        {
            Check(ShiftGamepadReport.For(ShiftDirection.Up, false) == new ShiftGamepadReport(0x2000, 0),
                "关闭时升挡只按 B");
            Check(ShiftGamepadReport.For(ShiftDirection.Down, false, 128) == new ShiftGamepadReport(0x4000, 0),
                "关闭时降挡只按 X，忽略补油参数");
            Check(ShiftGamepadReport.For(ShiftDirection.Down, true, 128) == new ShiftGamepadReport(0x4040, 128),
                "开启时内置方案可以在 X 和离合键之外输出指定补油量");
        });

        run("手柄换挡：连续同向脉冲之间保留可采样的松键间隔", () =>
        {
            var pad = new TestGamepad();
            var clock = new ManualClock();
            using var input = new GamepadShiftInput(() => pad, () => true, () => false, clock);
            Connect(input);
            var settings = new AutomaticShiftSettings { GamepadMode = GamepadShiftMode.Combination, AutomaticClutch = false, PressDurationMs = 120 };
            Check(input.TryShift(ShiftDirection.Down, settings) == ShiftInputResult.Sent, "应按下第一个 X");
            clock.Advance(120);
            Check(pad.Reports.Count == 2 && pad.Reports.Last() == default, "应在 120 毫秒时松开 X");
            Check(input.TryShift(ShiftDirection.Down, settings) == ShiftInputResult.PulseInProgress,
                "刚松开 X 的同一时刻应为游戏保留读取松键的时间");
            clock.Advance(20);
            Check(input.TryShift(ShiftDirection.Down, settings) == ShiftInputResult.Sent && pad.Reports.Count == 3 &&
                  pad.Reports.Last().Buttons == 0x4000, "松键间隔后应发出独立的第二次 X 按下");
        });

        run("手柄换挡：手动按键优先，脉冲结束清空按键与 RT", () =>
        {
            var pad = new TestGamepad();
            var manualHeld = true;
            using var input = new GamepadShiftInput(() => pad, () => true, () => manualHeld);
            Connect(input);
            var settings = new AutomaticShiftSettings { GamepadMode = GamepadShiftMode.Combination, PressDurationMs = 100 };
            Check(input.TryShift(ShiftDirection.Down, settings) == ShiftInputResult.Busy && pad.Reports.IsEmpty,
                "实体手柄正在换挡时应让出控制");
            manualHeld = false;
            Check(input.TryShift(ShiftDirection.Down, settings) == ShiftInputResult.Sent, "实体按键松开后应发送组合键");
            Check(pad.Reports.TryPeek(out var report) && report.RightTrigger == 255, "应发送补油 RT");
            Check(SpinWait.SpinUntil(() => pad.Reports.Count == 2, 2000) && pad.Reports.Last() == default,
                "脉冲结束后应把所有模拟按键与扳机归零");
        });

        run("手柄换挡：即使遥测停止，游戏失焦也立即松开组合键", () =>
        {
            var pad = new TestGamepad();
            var foreground = 1;
            using var input = new GamepadShiftInput(() => pad, () => Volatile.Read(ref foreground) == 1,
                () => false, new FrozenClock());
            Connect(input);
            Check(input.TryShift(ShiftDirection.Down, new AutomaticShiftSettings()) == ShiftInputResult.Sent, "游戏在前台时应发送按键");
            Volatile.Write(ref foreground, 0);
            Check(SpinWait.SpinUntil(() => pad.Reports.Count == 2, 2000) && pad.Reports.Last() == default,
                "失焦时无需等待脉冲时间或下一帧遥测");
            Check(input.TryShift(ShiftDirection.Up, new AutomaticShiftSettings()) == ShiftInputResult.NotInForeground,
                "游戏在后台时不可发新的手柄输入");
        });

        run("手柄换挡：停止和松开失败都移除虚拟手柄", () =>
        {
            var pad = new TestGamepad();
            using var input = new GamepadShiftInput(() => pad, () => true, () => false, new FrozenClock());
            Connect(input);
            input.TryShift(ShiftDirection.Down, new AutomaticShiftSettings());
            pad.FailNeutral = true;
            input.Release();
            Check(pad.Disposed && input.Error?.Contains("模拟设备断开") == true, "松开失败后须拔除设备并报告原因");
            Check(input.TryShift(ShiftDirection.Up, new AutomaticShiftSettings()) == ShiftInputResult.Failed,
                "断开的虚拟手柄不可继续发送");
            pad = new TestGamepad();
            Connect(input);
            input.TryShift(ShiftDirection.Up, new AutomaticShiftSettings());
            input.Disconnect();
            Check(pad.Disposed && pad.Reports.Last() == default, "停用时应先发送全松开，再断开虚拟手柄");
        });

        run("手柄换挡：取消连接后关闭迟到的设备", () =>
        {
            using var opening = new ManualResetEventSlim();
            using var finish = new ManualResetEventSlim();
            using var cancellation = new CancellationTokenSource();
            var pad = new TestGamepad();
            using var input = new GamepadShiftInput(() =>
            {
                opening.Set();
                finish.Wait(TimeSpan.FromSeconds(3));
                return pad;
            }, () => true, () => false);
            var connecting = input.ConnectAsync(cancellation.Token);
            Check(opening.Wait(TimeSpan.FromSeconds(3)), "连接应在后台开始");
            cancellation.Cancel();
            finish.Set();
            try { connecting.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            Check(pad.Disposed && pad.Reports.IsEmpty, "取消后不可保留迟到的虚拟设备");
        });
    }

    private static void Connect(GamepadShiftInput input)
    {
        var error = input.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
        Check(error is null, error ?? "");
    }

    private sealed class FrozenClock : TimeProvider
    {
        public override long GetTimestamp() => 0;
    }

    private sealed class KeyboardDrive : IDisposable
    {
        private readonly AutomaticShiftSettings _settings;

        public KeyboardDrive(bool reliable = true, AutomaticShiftSettings? settings = null)
        {
            _settings = settings ?? new AutomaticShiftSettings();
            Input = new KeyboardShiftInput(() => Foreground, () => false,
                key => Physical.Contains(key) || (!reliable && Combined.Contains(key)), (keys, release) =>
                {
                    Sent.Add((Clock.GetTimestamp(), keys, release));
                    // Retain the merged state after key up to model GetAsyncKeyState catching up.
                    if (!release)
                        foreach (var key in keys) Combined.Add(key);
                    return keys.Length;
                }, reliable, Clock);
            Check(Input.ConnectAsync(_settings, CancellationToken.None).GetAwaiter().GetResult() is null, "应连接键盘输出");
        }

        public ManualClock Clock { get; } = new();
        public KeyboardShiftInput Input { get; }
        public bool Foreground { get; set; } = true;
        public HashSet<byte> Physical { get; } = new();
        public HashSet<byte> Combined { get; } = new();
        public List<(long Time, byte[] Keys, bool Release)> Sent { get; } = new();
        public ShiftInputResult Shift(ShiftDirection direction) => Input.TryShift(direction, _settings);
        public void Dispose() => Input.Dispose();
    }

    internal sealed class ManualClock : TimeProvider
    {
        private long _now;
        private ManualTimer? _timer;

        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timer = new ManualTimer(this, callback, state);
            _timer.Change(dueTime, period);
            return _timer;
        }

        public void Advance(int milliseconds)
        {
            var end = _now + milliseconds;
            while (_timer is { } timer && timer.DueAt <= end)
            {
                _now = timer.DueAt;
                timer.Fire();
            }
            _now = end;
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long _period;
            public long DueAt { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._now + (long)dueTime.TotalMilliseconds;
                _period = (long)period.TotalMilliseconds;
                return true;
            }

            public void Fire()
            {
                DueAt = _period > 0 ? DueAt + _period : long.MaxValue;
                callback(state);
            }

            public void Dispose() => DueAt = long.MaxValue;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class TestGamepad : IShiftGamepad
    {
        public System.Collections.Concurrent.ConcurrentQueue<ShiftGamepadReport> Reports { get; } = new();
        public bool FailNeutral { get; set; }
        public bool Disposed { get; private set; }

        public void Submit(ShiftGamepadReport report)
        {
            if (FailNeutral && report == default) throw new InvalidOperationException("模拟设备断开");
            Reports.Enqueue(report);
        }

        public void Dispose() => Disposed = true;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
