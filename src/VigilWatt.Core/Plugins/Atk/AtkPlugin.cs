using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins.Atk;

/// <summary>
/// 功 能： ATK/VXE 无线鼠标电量插件——主动查询 FF02 口的 17 字节命令-应答协议。
/// 2026/10/20 22:14:00 Yang
/// 帧结构与实测依据见 docs/hardware/atk.md；编解码在 AtkCodec。
/// </summary>
public sealed class AtkPlugin : BatteryPluginBase
{
    public override string Name => "Atk";                 // AppSettings.PluginEnabled 的 key
    public override string DisplayName => "ATK";
    public override string DisplayNameEn => "ATK";
    /// <summary>一套开关管鼠标与键盘两条链路：具体图标由 <see cref="ResolveKind"/> 按链路给。</summary>
    public override DeviceKind Kind => DeviceKind.Unknown;

    protected override DeviceKind ResolveKind(string protocol)
        => protocol == ProtocolVia ? DeviceKind.Keyboard : DeviceKind.Mouse;

    // 会写帧（发查询帧）→ 按社区适配指南"主动通信不进 15 秒常规发现节拍"，
    // 与 Betop / Ganss / Aula 一致保持默认 false；发现延迟走常规扫描周期。
    protected override TimeSpan ProbeCooldown => TimeSpan.FromSeconds(10);

    // null 与"被熔断挡住"不可区分 → 基类不自动记账，由 PollDevice 自报账
    protected override OfflinePolicy Offline => new() { Silence = TimeSpan.Zero, CountPollFailures = false };

    private const ushort VendorId = 0x373B;               // ⚠ 实测修正：非 0x375B
    private const ushort DonglePid = 0x101B;              // 8K dongle（产品名 "ATK Mouse 8K Dongle"）
    private const ushort DonglePid1K = 0x101A;            // 1K dongle
    private const ushort DonglePidV3 = 0x1303;            // 8K dongle（产品名 "Wireless mouse 8k dongle-M"）
    private const ushort DonglePidR1Nk = 0x10C9;
    private const ushort BodyPidR1Nk = 0x10C7;

    /// <summary>电量口的 usage page；必须精确匹配，否则会对 FF03/FF04/FF05/FF06 等厂商口各白发一帧查询。</summary>
    private const ushort UsagePageBattery = 0xFF02;

    // R1 Nearlink 的 FF02 同时有 20B 与 17B 集合，登记接收器后才能按严格长度排除前者。
    private static bool IsReceiverPid(ushort pid) =>
        pid is DonglePid or DonglePid1K or DonglePidV3 or DonglePidR1Nk;

    private static DateTimeOffset _receiverCheckedAt;


    // 当前在位的接收器 PID 集合（5 秒缓存：IsCandidate 每接口调一次，全量重扫太贵）
    private static HashSet<ushort> _receiversInPlace = new();

    /// <summary>刷新"当前在位的接收器 PID 集合"。</summary>
    private static void RefreshReceiversInPlace()
    {
        if (_receiverCheckedAt != default && DateTimeOffset.Now - _receiverCheckedAt < TimeSpan.FromSeconds(5))
            return;

        _receiverCheckedAt = DateTimeOffset.Now;
        _receiversInPlace = HidDeviceScanner.Scan()
            .Where(d => d.VendorId == VendorId && d.UsagePage == UsagePageBattery)
            .Select(d => d.ProductId)
            .ToHashSet();
    }

    /// <summary>本机的**本体**（插充电线时枚举的鼠标本体）是否已被对应接收器顶替。</summary>
    private static bool ReceiverOfSameGroupInPlace(ushort bodyPid)
    {
        if (!DeviceGroups.TryGetValue(bodyPid, out var group))
            return _receiversInPlace.Count > 0;   // 未知分组：保守按"有接收器在位"处理

        return DeviceGroups.Any(kv =>
            kv.Value == group && IsReceiverPid(kv.Key) && _receiversInPlace.Contains(kv.Key));
    }

    /// <summary>鼠标链路（op 帧）与键盘链路（VIA）各一套协议名。</summary>
    private const string ProtocolMouse = "atk-v1";
    private const string ProtocolVia = "atk-keyboard-via";

    // R1 Nearlink 插线后接收器不再应答，必须释放旧句柄并交给本体查询。
    protected override IReadOnlyCollection<string> SkipPathsForThisPass(IReadOnlyList<HidDeviceInfo> availableDevices)
        => availableDevices.Any(d => d.VendorId == VendorId && d.ProductId == BodyPidR1Nk
                && d.UsagePage == UsagePageBattery
                && AtkCodec.IsSupportedInterface(d.InputReportLength, d.OutputReportLength))
            ? availableDevices.Where(d => d.VendorId == VendorId && d.ProductId == DonglePidR1Nk)
                .Select(d => d.DevicePath).ToArray()
            : Array.Empty<string>();

    /// <summary>认领电量接口；R1 Nearlink 插线优先本体，其它型号维持接收器优先。</summary>
    protected override bool IsCandidate(HidDeviceInfo info)
    {
        // 键盘链路（VIA，page 0xFF60）：与鼠标同 VID，靠 usage page 分流，必须先判。
        if (AtkViaCodec.IsSupportedInterface(info.VendorId, info.UsagePage,
                info.InputReportLength, info.OutputReportLength, info.OutputReportId))
            return true;

        if (info.VendorId != VendorId || info.UsagePage != UsagePageBattery)
            return false;

        // R1 Nearlink 两端均有 20B 干扰集合，和已知接收器一样只认严格 17B 接口。
        if (IsReceiverPid(info.ProductId) || info.ProductId == BodyPidR1Nk)
            return AtkCodec.IsSupportedInterface(info.InputReportLength, info.OutputReportLength);

        if (info.InputReportLength < AtkCodec.ReportLength || info.OutputReportLength < AtkCodec.ReportLength)
            return false;

        // 其余 PID 视为"有线本体"（插充电线时枚举出来的鼠标本体）。
        // 本体 PID **因型号而异**（实测 F1 Pro Max = 0x1015、F1 V3 ULTIMATE+ = 0x12A9），
        // 不能靠白名单穷举——改用"**本组对应的接收器**不在位"来判定，故新型号自动支持。
        // ⚠ 必须按"本组"判，不能按"全局有没有接收器"：同机另一只鼠标的接收器在位时，
        //   会把本鼠标的本体误判成"接收器还在"而不认领（2026-10-23 实机复现）。
        RefreshReceiversInPlace();
        return !ReceiverOfSameGroupInPlace(info.ProductId);
    }

    // 无跨模式通用身份时按实测型号组归一；同型号多设备限制见 docs/hardware/atk.md §4、§8.3。
    private static readonly Dictionary<ushort, string> DeviceGroups = new()
    {
        [0x101B] = "promax",   // F1 Pro Max 8K 接收器
        [0x101A] = "promax",   // F1 Pro Max 1K 接收器（同一只鼠标的另一种无线模式）
        [0x1015] = "promax",   // F1 Pro Max 本体（插充电线时枚举）
        [0x1303] = "v3",       // F1 V3 ULTIMATE+ 8K-M 接收器
        [0x12A9] = "v3",       // F1 V3 ULTIMATE+ 本体（插充电线时枚举）
        [DonglePidR1Nk] = "r1nk",
        [BodyPidR1Nk] = "r1nk",
    };

    /// <summary>各组的型号名（本体上报的产品名，即机型）；按组存，两只鼠标互不覆盖。</summary>
    private static readonly ConcurrentDictionary<string, string> ModelNames = new();

    /// <summary>从 HID 路径取 PID（形如 <c>vid_373b&amp;pid_101b&amp;mi_01&amp;col05</c>）。</summary>
    private static ushort PidFromPath(string devicePath)
    {
        var m = Regex.Match(devicePath, @"pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);
        return m.Success ? (ushort)Convert.ToInt32(m.Groups[1].Value, 16) : (ushort)0;
    }

    /// <summary>设备 ID 按配对组归一；未登记的 PID 保持路径哈希。</summary>
    protected override string IdFor(string devicePath)
    {
        var pid = PidFromPath(devicePath);
        return DeviceGroups.TryGetValue(pid, out var group)
            ? $"{Name.ToLowerInvariant()}:atk-{group}"
            : base.IdFor(devicePath);   // 未登记的 PID → 保持上游默认（路径哈希）
    }

    private static readonly Dictionary<ushort, string> KnownModelByPid = new()
    {
        [0x1152] = "ATK A98 Pro",
        [0x11D9] = "ATK F1 Ultimate 2.0",
        [0x11E4] = "ATK F1 Ultimate 2.0",
        [DonglePidR1Nk] = "VXE R1 Nearlink",
        [BodyPidR1Nk] = "VXE R1 Nearlink",
    };

    protected override string BuildDisplayName(HidDeviceInfo info)
    {
        if (KnownModelByPid.TryGetValue(info.ProductId, out var known)) return known;

        // 型号名取"本体"（非接收器）上报的产品名，比接收器的通用名更准确；
        // 按归一组存，两只鼠标各记各的，不会互相覆盖。
        var group = DeviceGroups.TryGetValue(info.ProductId, out var g)
            ? g
            : $"p{info.ProductId:X4}";

        if (!IsReceiverPid(info.ProductId) && !string.IsNullOrWhiteSpace(info.Product))
            ModelNames[group] = info.Product;

        return ModelNames.TryGetValue(group, out var model)
            ? model
            : (string.IsNullOrWhiteSpace(info.Product)
                ? $"{DisplayName} ({info.VidPid})"
                : info.Product);
    }

    // ------------------------------------------------------------------ 写读

    /// <summary>写查询帧；WriteOutput 不可用时回落 HidD_SetOutputReport（该设备独占 WriteFile 不可用）。</summary>
    private static bool WriteQuery(HidDeviceHandle handle, byte[] frame)
    {
        if (handle.WriteOutput(frame, TimeSpan.FromSeconds(1), quiet: true))
            return true;
        return NativeSetOutputReport(handle.Info.DevicePath, frame);
    }

    /// <summary>读电量响应：最多约 1.5 秒，过滤掉队列里的普通鼠标报文。</summary>
    private static byte[]? ReadBatteryResponse(HidDeviceHandle handle, TimeSpan window)
    {
        var deadline = DateTimeOffset.UtcNow + window;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var input = handle.ReadInput(TimeSpan.FromMilliseconds(50));
            if (AtkCodec.TryParseReply(input) is not null)
                return input;
        }
        return null;
    }

    // ------------------------------------------------------------------ 基类钩子

    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
    {
        // 接口预检查：写不进 17 字节 output 的接口直接排除（北通经验：长度从 caps 读）
        // 键盘链路先判：同一接收器 VID 相同，反过来会把 VIA 查询帧发给鼠标。
        if (AtkViaCodec.IsSupportedInterface(handle.Info.VendorId, handle.Info.UsagePage,
                handle.Info.InputReportLength, handle.Info.OutputReportLength, handle.Info.OutputReportId))
        {
            var kb = QueryVia(handle);
            return kb.HasValue
                ? (ProtocolVia, kb.Value.Percent, kb.Value.Charging ? ChargingState.Charging : ChargingState.Discharging)
                : null;
        }

        if (handle.Info.OutputReportLength < AtkCodec.ReportLength) return null;

        if (!WriteQuery(handle, AtkCodec.BuildQuery())) return null;
        if (AtkCodec.TryParseReply(ReadBatteryResponse(handle, TimeSpan.FromSeconds(2))) is not { } r)
            return null;
        return (ProtocolMouse, r.Percent, r.Charging);
    }

    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice dev)
    {
        if (!CanWrite(dev))
            return null;                          // 未发帧（开关关闭/熔断中），不计失败

        if (dev.Protocol == ProtocolVia)
        {
            var via = QueryVia(dev.Handle);
            if (via.HasValue) ReportPollAnswered(dev);
            else ReportPollFailure(dev);
            return via.HasValue
                ? (via.Value.Percent, via.Value.Charging ? ChargingState.Charging : ChargingState.Discharging)
                : null;
        }

        if (!WriteQuery(dev.Handle, AtkCodec.BuildQuery()))
        {
            // 写失败 = 句柄已失效（设备被拔出，或 Windows 重新枚举了 HID 设备），
            // **不计失败**：基类下一轮扫描会移除旧记录并重新认领。计失败会误触熔断，
            // 让另一台设备假离线 30~60 秒（2026-10-23 实机复现）。
            return null;
        }

        var parsed = AtkCodec.TryParseReply(ReadBatteryResponse(dev.Handle, TimeSpan.FromSeconds(2)));
        if (parsed.HasValue) ReportPollAnswered(dev);   // 应答 → 清零失败计数、提前解除熔断
        else ReportPollFailure(dev);                    // 读得到句柄但不回帧 = 鼠标睡了，该熔断
        return parsed;
    }

    // ------------------------------------------------------------------ P/Invoke 后备：HidD_SetOutputReport

    [System.Runtime.InteropServices.DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(IntPtr fileHandle, byte[] reportBuffer, uint reportBufferLength);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string path, uint desiredAccess, int shareMode,
        IntPtr securityAttributes, int creationDisposition, int flags, IntPtr templateFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>独占 WriteFile 不可用时的写入后备：重新以 GENERIC_WRITE 打开并走 HidD_SetOutputReport。</summary>
    private static bool NativeSetOutputReport(string devicePath, byte[] report)
    {
        const uint GENERIC_WRITE = 0x40000000;
        const int FILE_SHARE_RW = 3, OPEN_EXISTING = 3;
        IntPtr handle = CreateFileW(devicePath, GENERIC_WRITE, FILE_SHARE_RW,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
        try
        {
            return HidD_SetOutputReport(handle, report, (uint)report.Length);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>键盘链路：发一条 VIA 查询，在 1 秒内等电量响应。</summary>
    private static (int Percent, bool Charging)? QueryVia(HidDeviceHandle handle)
    {
        if (!IsWritesEnabled("Atk")) return null;
        if (!handle.WriteOutput(AtkViaCodec.CreateQuery(handle.Info.OutputReportLength),
                TimeSpan.FromMilliseconds(500)))
            return null;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (DateTime.UtcNow < deadline)
        {
            var input = handle.ReadInput(TimeSpan.FromMilliseconds(300));
            if (input == null) continue;
            if (!AtkViaCodec.TryParse(input, out int percent, out bool charging))
            {
                if (Log.IsEnabled(LogLevel.Debug)) Log.Debug($"ATK 键盘忽略非电量响应: {Log.Hex(input)}");
                continue;
            }
            Log.Info($"ATK 键盘 VIA 电量查询: {percent}% 充电={charging} 原始={Log.Hex(input)}");
            return (percent, charging);
        }
        Log.Once(LogLevel.Debug, $"atk-kb-silent:{handle.Info.DevicePath}",
            $"ATK 键盘查询无响应（多半在睡，恢复应答后会再记一条）: {handle.Info.DevicePath}");
        return null;
    }
}
