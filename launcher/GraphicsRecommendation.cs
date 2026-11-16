using System.Runtime.InteropServices;

namespace GRIDAutosportVR;

internal sealed record GpuInfo(string Name, uint VendorId, uint DeviceId, long DedicatedMb);

// Preset is Keep (current quality and VR anti-aliasing, new render scale only) or a preset id.
internal sealed record Recommendation(string Preset, int Scale, string Reason);

// Graphics → Recommend for this PC, ported from GRID2VR. A starting point, never a silent change:
// the launcher applies it to the form with Undo, and the player saves or launches to keep it.
internal static class GraphicsRecommendation
{
    internal const string Keep = "keep";
    // Pixels per eye that VR Balanced should sustain at 90 Hz on an RTX 4080 class GPU, with margin
    // for heavier tracks. Calibrated on xr-sim at 90 Hz with an RTX 4080 SUPER, the benchmark race
    // with 11 opponents: VR Balanced at 150% of Quest 3 (4608 × 4896, 22.6 M pixels per eye) held a
    // 90 FPS median with no more drops than Potato at 100%; Ultra with 8× MSAA there fell to 82.
    internal const double BudgetPixels = 18e6;
    private const double LaptopFactor = .6;
    // GRID2VR's table: approximate desktop raster throughput relative to an RTX 4080. More specific
    // names come first because matching is by substring.
    private static readonly (string Name, double Index)[] Gpus =
    [
        ("RTX 5090", 1.55), ("RTX 4090", 1.30), ("RTX 5080", 1.10), ("RTX 4080 SUPER", 1.03), ("RTX 4080", 1.00),
        ("RTX 5070 Ti", .90), ("RTX 5070", .70), ("RTX 4070 Ti SUPER", .85), ("RTX 4070 Ti", .76), ("RTX 4070 SUPER", .70), ("RTX 4070", .60),
        ("RTX 5060 Ti", .50), ("RTX 5060", .42), ("RTX 4060 Ti", .43), ("RTX 4060", .35),
        ("RTX 3090 Ti", .80), ("RTX 3090", .72), ("RTX 3080 Ti", .70), ("RTX 3080", .64), ("RTX 3070 Ti", .50), ("RTX 3070", .46),
        ("RTX 3060 Ti", .40), ("RTX 3060", .32), ("RTX 3050", .20),
        ("RTX 2080 Ti", .45), ("RTX 2080 SUPER", .38), ("RTX 2080", .36), ("RTX 2070 SUPER", .33), ("RTX 2070", .29),
        ("RTX 2060 SUPER", .28), ("RTX 2060", .24), ("GTX 1080 Ti", .32), ("GTX 1080", .25), ("GTX 1070", .20),
        ("RX 9070 XT", .88), ("RX 9070", .78), ("RX 7900 XTX", .97), ("RX 7900 XT", .83), ("RX 7900 GRE", .70),
        ("RX 7800 XT", .62), ("RX 7700 XT", .50), ("RX 7600", .34), ("RX 6950 XT", .70), ("RX 6900 XT", .64),
        ("RX 6800 XT", .60), ("RX 6800", .52), ("RX 6750 XT", .45), ("RX 6700 XT", .44), ("RX 6600 XT", .31), ("RX 6600", .27),
        ("B580", .36), ("A770", .33), ("A750", .30),
    ];

    internal static double? GpuIndex(string name)
    {
        foreach (var (pattern, index) in Gpus)
            if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return name.Contains("Laptop", StringComparison.OrdinalIgnoreCase) ? index * LaptopFactor : index;
        return null;
    }

    private static int RoundFive(double scale) => (int)Math.Round(scale / 5, MidpointRounding.AwayFromZero) * 5;

    internal static Recommendation Recommend(HeadsetResolution? headset, GpuInfo? gpu, VrPerformance? last, Choices current)
    {
        var maximum = Math.Clamp(headset?.MaximumRenderScale ?? 100, RenderScaleSlider.MinimumScale, 100);
        int Clamp(int scale) => Math.Clamp(scale, RenderScaleSlider.MinimumScale, maximum);
        var sameHeadset = headset is null || last is null || last.EyeWidth == 0 || headset.EyeSize(last.Scale) == (last.EyeWidth, last.EyeHeight);
        var measured = last is { Usable: true } && sameHeadset;
        if (measured && last!.SameSettings(current))
        {
            var rate = $"{last.FpsMedian:0} of {last.DisplayHz:0} FPS";
            if (last.HeldRate && !last.Dips)
                return new(Keep, Clamp(last.Scale), $"Your last VR session held {last.DisplayHz:0} FPS at {last.Scale}% with these settings. Keep them." +
                    (last.SomeDips ? $" Short drops in {last.DipShare * 100:0}% of seconds happen at every quality setting; fewer opponents can reduce them." : ""));
            var preset = GraphicsCatalog.PresetId(current.GraphicsQuality, current.VrMsaa, current.VrRenderScale);
            var shortfall = last.HeldRate ? $"mostly held {last.DisplayHz:0} FPS, but {last.DipShare * 100:0}% of seconds dropped (low {last.FpsLow:0} FPS)" : $"ran at {rate}";
            if (preset is GraphicsCatalog.GameSettings or "ultra" or "high" or GraphicsCatalog.Custom)
                return new(GraphicsCatalog.Balanced, Clamp(last.Scale), $"Your last VR session {shortfall}. This switches to VR Balanced (High quality, Medium crowd, VR anti-aliasing Off) and keeps {Clamp(last.Scale)}% render scale. Launch VR, then press Recommend again.");
            var lower = RoundFive(last.Scale * Math.Sqrt((last.HeldRate ? last.FpsLow : last.FpsMedian) / last.DisplayHz) * .95);
            if (lower < 60) return new(GraphicsCatalog.Potato, GraphicsCatalog.PotatoScale, $"Your last VR session {shortfall}, even at {last.Scale}%. Potato is the lightest preset.");
            return new(Keep, Clamp(Math.Min(lower, last.Scale - 5)), $"Your last VR session {shortfall}, so render scale goes down from {last.Scale}%. Fewer opponents also help when the CPU is the limit.");
        }
        var name = gpu?.Name;
        var index = name is null ? null : GpuIndex(name);
        var basis = measured ? " Your last VR session used different quality or anti-aliasing settings, so its measurement was not used." : "";
        const string follow = " Launch VR once, then press Recommend again to fine-tune from the measured session.";
        if (index is null)
            return new(GraphicsCatalog.Balanced, Clamp(80), (name is null ? "No graphics card was detected" : $"{name} has no estimate yet") + ", so this starts at VR Balanced and 80%." + basis + follow);
        if (headset is null)
            return new(GraphicsCatalog.Balanced, Clamp(80), $"Headset resolution unknown, so this starts at 80% for {name}. Connect the headset and select Refresh headset for a better estimate." + basis + follow);
        var estimate = RoundFive(100 * Math.Sqrt(BudgetPixels * index.Value / ((double)headset.Width * headset.Height)));
        if (estimate < 60)
            return new(GraphicsCatalog.Potato, GraphicsCatalog.PotatoScale, $"{name} at {headset.Width} × {headset.Height} per eye is below the VR Balanced range, so this picks Potato, the lightest preset." + basis + follow);
        return new(GraphicsCatalog.Balanced, Clamp(estimate), $"Estimated for {name} at {headset.Width} × {headset.Height} per eye." + basis + follow);
    }

    // The launcher choices with a recommendation applied.
    internal static Choices Apply(Choices value, Recommendation recommendation)
    {
        if (recommendation.Preset == Keep) return value with { VrRenderScale = recommendation.Scale };
        var (quality, msaa, scale) = GraphicsCatalog.ApplyPreset(recommendation.Preset, value.VrMsaa, value.VrRenderScale);
        return value with { GraphicsQuality = quality, VrMsaa = msaa, VrRenderScale = recommendation.Preset == GraphicsCatalog.Potato ? scale : recommendation.Scale };
    }

    // The hardware adapter with the most dedicated memory; software adapters are skipped.
    internal static GpuInfo? DetectGpu()
    {
        IDXGIFactory1? factory = null;
        try
        {
            if (CreateDXGIFactory1(typeof(IDXGIFactory1).GUID, out factory) < 0 || factory is null) return null;
            GpuInfo? best = null;
            for (uint index = 0; index < 16 && factory.EnumAdapters1(index, out var adapter) >= 0; ++index)
            {
                try
                {
                    if (adapter.GetDesc1(out var desc) < 0 || (desc.Flags & SoftwareAdapter) != 0 || desc.VendorId == MicrosoftVendor) continue;
                    var memory = (long)(ulong)desc.DedicatedVideoMemory / (1024 * 1024);
                    if (best is null || memory > best.DedicatedMb) best = new(desc.Description.Trim(), desc.VendorId, desc.DeviceId, memory);
                }
                finally { Marshal.ReleaseComObject(adapter); }
            }
            return best;
        }
        catch (Exception error) when (error is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidCastException) { return null; }
        finally { if (factory is not null) Marshal.ReleaseComObject(factory); }
    }

    private const uint SoftwareAdapter = 2, MicrosoftVendor = 0x1414;
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);

    // Vtable order only; the placeholder methods are never called.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        void IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out AdapterDescription description);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow; public int LuidHigh; public uint Flags;
    }
}
