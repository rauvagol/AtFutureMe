using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using AtFutureMe.Models;

namespace AtFutureMe.Services;

/// <summary>
/// Clock planetary aesthetics — concentric compound stages with idlers and
/// locked tooth-progression hollow rings (kinematic RPM, no faking).
/// </summary>
// auto model
public static class ClockGearGeometry
{
    public const int FaceRingTeeth = 144;
    public const int FacePlanetTeeth = 6;
    /// <summary>Default stage-1 compound when no progression is selected.</summary>
    public const int FaceCompoundRatio = 10;
    public const int FaceCompoundTeeth = FacePlanetTeeth * FaceCompoundRatio;
    /// <summary>Legacy default stage-2/3 compound ratio (6T+36T).</summary>
    public const int Stage2CompoundRatio = 6;
    public const int Stage2CompoundTeeth = FacePlanetTeeth * Stage2CompoundRatio;
    /// <summary>Idlers mesh externally with the compound gear (same module as compound).</summary>
    public const int FaceIdlerTeeth = FacePlanetTeeth;
    public const int FaceHourToothStride = 12;
    public const int MinSunTeeth = 8;
    public const int MaxSunTeeth = 256;
    /// <summary>Tip radius below this is a speck — teeth are not readable.</summary>
    public const double MinVisibleGearTipR = 4.0;

    /// <summary>
    /// Locked sun teeth per stage + compound tooth counts so
    /// ω_s/ω_h = (Nh/Np)·(Nc/Ns) hits the target stage ratios exactly.
    /// Nc = round(R · Ns · Np / Nh).
    /// </summary>
    public readonly record struct ToothProgression(
        string Label,
        int Outer,
        int Inner1,
        int Inner2,
        int Inner3,
        int Inner4,
        int Inner5,
        int Compound1,
        int Compound2,
        int Compound3,
        int Compound4,
        int Compound5,
        double Ratio1,
        double Ratio2,
        double Ratio3)
    {
        public int StageCount =>
            Compound5 > 0 ? 5 : Compound4 > 0 ? 4 : Compound3 > 0 ? 3 : 2;

        public int CompoundAt(int stage) => stage switch
        {
            0 => Compound1,
            1 => Compound2,
            2 => Compound3,
            3 => Compound4,
            _ => Compound5
        };

        public int[] Compounds()
        {
            var n = StageCount;
            var a = new int[n];
            for (var i = 0; i < n; i++)
                a[i] = CompoundAt(i);
            return a;
        }

        public int LockedSunAt(int stage)
        {
            if (stage != StageCount - 1)
                return 0;
            return stage switch
            {
                1 => Inner2,
                2 => Inner3,
                3 => Inner4,
                _ => Inner5
            };
        }
    }

    public static ToothProgression ProgressionFromCompounds(string label, params int[] compounds)
    {
        var live = compounds.Where(n => n > 0).Select(n => Math.Max(FacePlanetTeeth * 2, n)).ToArray();
        var c = new int[5];
        for (var i = 0; i < live.Length && i < 5; i++)
            c[i] = live[i];
        var last = ExactLastSunTeeth(live);
        var inn = new int[5];
        if (last > 0 && live.Length >= 2)
            inn[live.Length - 1] = last;
        return new(
            label, FaceRingTeeth,
            inn[0], inn[1], inn[2], inn[3], inn[4],
            c[0], c[1], c[2], c[3], c[4],
            0, 0, last > 0 ? 1440 : 0);
    }

    public static bool TryGetProgression(ClockGearDriveMode mode, out ToothProgression progression)
    {
        // Dropdown picks compound sizes, not ring teeth. Locking small first rings
        // (48T/72T) made ×10 require tiny compounds whose idlers cannot reach the sun.
        progression = mode switch
        {
            ClockGearDriveMode.Concentric =>
                ProgressionFromCompounds("6+24 / 6+18 / 6+18 / 6+60", 24, 18, 18, 60),
            ClockGearDriveMode.Teeth144_96_64_32 =>
                ProgressionFromCompounds("6+30 / 6+48 / 6+54", 30, 48, 54),
            ClockGearDriveMode.Teeth144_108_72_36 =>
                ProgressionFromCompounds("6+36 / 6+60 / 6+36", 36, 60, 36),
            ClockGearDriveMode.Teeth144_120_90_60 =>
                ProgressionFromCompounds("6+60 / 6+24 / 6+54", 60, 24, 54),
            ClockGearDriveMode.Teeth144_120_80_60 =>
                ProgressionFromCompounds("6+36 / 6+36 / 6+60", 36, 36, 60),
            ClockGearDriveMode.Teeth144_144_108_72 =>
                ProgressionFromCompounds("6+18 / 6+24 / 6+18 / 6+30", 18, 24, 18, 30),
            ClockGearDriveMode.Teeth144_144_120_80 =>
                ProgressionFromCompounds("6+96 / 6+60", 96, 60),
            _ => default
        };
        return mode != ClockGearDriveMode.Off;
    }

    public static bool TryGetProgression(AppSettings settings, out ToothProgression progression)
    {
        if (settings.ClockGearDriveMode != ClockGearDriveMode.Off
            && settings.ClockGearCompounds is { Count: >= 2 } stored)
        {
            var live = stored.Where(n => n > 0).ToArray();
            if (live.Length >= 2 && ExactLastSunTeeth(live) > 0)
            {
                progression = ProgressionFromCompounds(FormatCompounds(live), live);
                return true;
            }
        }
        return TryGetProgression(settings.ClockGearDriveMode, out progression);
    }

    public static string FormatCompounds(IReadOnlyList<int> compounds) =>
        string.Join(" / ", compounds.Where(n => n > 0).Select(n => $"6+{n}"));

    public readonly record struct GearCombo(string Label, int[] Compounds, int[] StagePlanets, int LastSun)
    {
        public int Rings => Compounds.Length;
        public int PlanetTotal => StagePlanets.Sum();
    }

    static List<GearCombo>? _catalog;

    /// <summary>
    /// Compound trains that assemble, packed with the densest equally-spaced
    /// planet count that fits on each ring (outer first). Built once per process.
    /// </summary>
    public static IReadOnlyList<GearCombo> PrecomputedCombos()
    {
        if (_catalog != null)
            return _catalog;
        _catalog = BuildCatalog();
        return _catalog;
    }

    static readonly int[] CatalogTeeth = [18, 24, 30, 36, 40, 48, 54, 60, 72, 80, 90, 96];
    const double CatalogRim = 140;

    static List<GearCombo> BuildCatalog()
    {
        var ncs = CatalogTeeth;
        var jobs = new List<int[]>();
        foreach (var a in ncs)
        foreach (var b in ncs)
        {
            jobs.Add([a, b]);
            foreach (var c in ncs)
            {
                jobs.Add([a, b, c]);
                foreach (var d in ncs)
                    jobs.Add([a, b, c, d]);
            }
        }

        var bag = new ConcurrentBag<GearCombo>();
        Parallel.ForEach(jobs, combo =>
        {
            var last = ExactLastSunTeeth(combo);
            if (last < MinSunTeeth)
                return;
            var packed = DensestStagePlanets(combo, CatalogRim);
            if (packed.Length == 0)
                return;
            var planets = string.Join("+", packed);
            var label = $"{combo.Length} rings · {planets} planets · {FormatCompounds(combo)} · last {last}T";
            bag.Add(new GearCombo(label, combo, packed, last));
        });

        return bag
            .OrderByDescending(x => x.Rings)
            .ThenByDescending(x => x.PlanetTotal)
            .ThenBy(x => x.Label, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Most equally-spaced planets that fit on each ring, packed outer-first.
    /// Two opposite planets are just N=2 at 180°.
    /// </summary>
    public static int[] DensestStagePlanets(IReadOnlyList<int> compounds, double rim)
    {
        var n = compounds.Count;
        var counts = new int[n];
        Array.Fill(counts, 1);
        if (!TryAssembleCompounds(compounds, rim, counts, out _))
            return [];
        for (var i = 0; i < n; i++)
        {
            var best = 1;
            for (var p = 12; p >= 2; p--)
            {
                counts[i] = p;
                if (!TryAssembleCompounds(compounds, rim, counts, out _))
                    continue;
                best = p;
                break;
            }
            counts[i] = best;
        }
        return counts;
    }

    public static List<int> ValidStagePlanetCounts(
        IReadOnlyList<int> compounds, double rim, IReadOnlyList<int> current, int stage)
    {
        var n = compounds.Count;
        var trial = new int[n];
        for (var i = 0; i < n; i++)
            trial[i] = current != null && i < current.Count ? Math.Clamp(current[i], 1, 12) : 1;
        if (stage < 0 || stage >= n)
            return [];
        var found = new List<int>();
        for (var p = 1; p <= 12; p++)
        {
            trial[stage] = p;
            if (TryAssembleCompounds(compounds, rim, trial, out _))
                found.Add(p);
        }
        return found;
    }

    public static double FaceToSecondRatio(double horizonHours) =>
        Math.Max(1, horizonHours) * 60.0;

    public readonly record struct FaceMeshLayout(
        double RingOuter,
        double TipR,
        double RootR,
        double RingPitch,
        double PlanetPitch,
        double PlanetTipR,
        double PlanetRootR,
        double PlanetHubR,
        double CompoundPitch,
        double CompoundTipR,
        double CompoundRootR,
        double IdlerPitch,
        double IdlerTipR,
        double IdlerRootR,
        double IdlerMeshRadius,
        double CarrierRadius,
        double Module,
        double TipWidthFrac,
        double RootWidthFrac,
        int RingTeeth,
        int PlanetTeeth,
        int CompoundTeeth,
        int IdlerTeeth,
        int DrawCompoundTeeth,
        int DrawIdlerTeeth)
    {
        /// <summary>Tooth count used to draw / mesh-align the compound body.</summary>
        public int MeshCompoundTeeth =>
            DrawCompoundTeeth > 0 ? DrawCompoundTeeth : CompoundTeeth;
        /// <summary>Tooth count used to draw / mesh-align the idler.</summary>
        public int MeshIdlerTeeth =>
            DrawIdlerTeeth > 0 ? DrawIdlerTeeth : IdlerTeeth;
    }

    /// <summary>Auto-fit sun/inner ring for a stage (not locked yet).</summary>
    public readonly record struct InnerRingFit(
        int Teeth,
        double Pitch,
        double TipR,
        double RootR,
        double IdlerCenterDist,
        double Module,
        double PitchError,
        double StageRatioFromOuter,
        bool AssemblyOk,
        string FailReason);

    public static FaceMeshLayout LayoutFaceMesh(double rim, int compoundTeeth = FaceCompoundTeeth)
    {
        rim = Math.Max(28, rim);
        var ring = GearCutter.InternalAgainstRim(rim, FaceRingTeeth);
        return LayoutPlanetaryStage(
            rim, ring.TipR, ring.RootR, ring.PitchR, ring.Module,
            FaceRingTeeth, FacePlanetTeeth, compoundTeeth,
            tipWidthFrac: 0.5, rootWidthFrac: 0.5);
    }

    /// <summary>
    /// Next concentric stage inside a hollow ring: host = that ring's interior teeth
    /// (root farther from center, tip closer). Pass locked compound tooth count.
    /// </summary>
    public static FaceMeshLayout LayoutFromInternalHost(
        InnerRingBand hostBand, int hostTeeth, int compoundTeeth)
    {
        hostTeeth = Math.Clamp(hostTeeth, MinSunTeeth, MaxSunTeeth);
        compoundTeeth = Math.Max(FacePlanetTeeth * 2, compoundTeeth);
        var pitch = GearCutter.InternalPitchFromBand(hostBand.InnerTip, hostBand.InnerRoot, hostTeeth);
        var host = GearCutter.Internal(pitch, hostTeeth);
        return LayoutPlanetaryStage(
            host.RootR, host.TipR, host.RootR, host.PitchR, host.Module,
            hostTeeth, FacePlanetTeeth, compoundTeeth,
            tipWidthFrac: 0.5, rootWidthFrac: 0.5);
    }

    static FaceMeshLayout LayoutPlanetaryStage(
        double ringOuter, double tipR, double rootR, double ringPitch, double module,
        int ringTeeth, int planetTeeth, int compoundTeeth,
        double tipWidthFrac, double rootWidthFrac)
    {
        _ = module;
        ringTeeth = Math.Max(1, ringTeeth);
        planetTeeth = Math.Max(1, planetTeeth);
        compoundTeeth = Math.Max(planetTeeth * 2, compoundTeeth);
        var idlerTeeth = planetTeeth;

        var planetPitch = ringPitch * planetTeeth / (double)ringTeeth;
        var planet = GearCutter.External(planetPitch, planetTeeth);
        var hubMax = Math.Max(0.4, planet.RootR - Math.Min(0.3, planet.RootR * 0.2));
        var hubMin = Math.Min(1.5, hubMax);
        var planetHubR = Math.Clamp(planet.RootR * 0.88, hubMin, hubMax);

        var tipClear = planet.Module * 0.25;
        var carrier = Math.Min(
            ringPitch - planetPitch,
            rootR - planet.TipR - tipClear);
        carrier = Math.Max(planet.TipR * 1.5, carrier);

        var compoundPitch = planetPitch * (compoundTeeth / (double)planetTeeth);
        var compoundMax = Math.Max(planetPitch * 2.5, carrier * 0.72);
        if (compoundPitch > compoundMax)
            compoundPitch = compoundMax;
        var meshModule = GearCutter.IsoModule(planetPitch, planetTeeth);
        var drawNc = Math.Clamp(
            (int)Math.Round(2.0 * compoundPitch / meshModule),
            planetTeeth * 2, MaxSunTeeth);
        var compound = GearCutter.External(compoundPitch, drawNc);
        var idler = GearCutter.External(planetPitch, idlerTeeth);
        var idlerMeshRadius = MeshCenters(compound, idler);

        return new FaceMeshLayout(
            ringOuter, tipR, rootR, ringPitch, planetPitch,
            planet.TipR, planet.RootR, planetHubR,
            compoundPitch, compound.TipR, compound.RootR,
            idler.PitchR, idler.TipR, idler.RootR, idlerMeshRadius,
            carrier, planet.Module, tipWidthFrac, rootWidthFrac,
            ringTeeth, planetTeeth, compoundTeeth, idlerTeeth, drawNc, idlerTeeth);
    }

    public static int CompoundMulFromScale(IReadOnlyList<double>? scales, int stage)
    {
        if (scales == null || stage < 0 || stage >= scales.Count)
            return 1;
        var v = scales[stage];
        if (double.IsNaN(v) || double.IsInfinity(v) || v < 1.5)
            return 1;
        return (int)Math.Clamp(Math.Round(v), 1, 24);
    }

    public static List<StageSolve> WithDrawnCompounds(
        IReadOnlyList<StageSolve> stages, IReadOnlyList<double>? scales)
    {
        var list = new List<StageSolve>(stages.Count);
        for (var i = 0; i < stages.Count; i++)
        {
            var st = stages[i];
            var off = st.IdlerOffsetDeg;
            var lay = ApplyCompoundMul(
                st.Layout, st.Fit.Teeth, st.PlanetCount, st.PlanetOffsetDeg, off,
                CompoundMulFromScale(scales, i), out off);
            list.Add(st with { Layout = lay, IdlerOffsetDeg = off });
        }
        return list;
    }

    /// <summary>
    /// Integer size step k: compound and idler both ×k at the idler’s module
    /// (same tooth size, more teeth, ratio Nc/Ni preserved). k=1 is the real train.
    /// </summary>
    public static FaceMeshLayout ApplyCompoundMul(
        FaceMeshLayout layout, int sunTeeth, int planetCount, double planetOffsetDeg,
        double idlerOffsetDeg, int mul, out double newIdlerOffsetDeg)
    {
        newIdlerOffsetDeg = ClampIdlerOffsetDeg(idlerOffsetDeg);
        sunTeeth = Math.Clamp(sunTeeth, MinSunTeeth, MaxSunTeeth);
        planetCount = Math.Clamp(planetCount, 1, 12);
        planetOffsetDeg = WrapOffsetDeg(planetOffsetDeg, PlanetOffsetPeriodDeg(planetCount));
        var maxK = MaxCompoundMul(layout, sunTeeth, planetCount, planetOffsetDeg);
        mul = Math.Clamp(mul, 1, maxK);
        if (mul <= 1)
            return layout;
        var grown = WithCompoundMul(layout, mul);
        newIdlerOffsetDeg = IdlerOffsetForSun(grown, sunTeeth, newIdlerOffsetDeg);
        return grown;
    }

    public static int MaxCompoundMul(
        FaceMeshLayout layout, int sunTeeth, int planetCount, double planetOffsetDeg = 0)
    {
        sunTeeth = Math.Clamp(sunTeeth, MinSunTeeth, MaxSunTeeth);
        planetCount = Math.Clamp(planetCount, 1, 12);
        planetOffsetDeg = WrapOffsetDeg(planetOffsetDeg, PlanetOffsetPeriodDeg(planetCount));
        if (!CompoundPitchHitsSun(layout, sunTeeth, planetCount, planetOffsetDeg))
            return 1;
        var maxK = 1;
        for (var k = 2; k <= 24; k++)
        {
            if (layout.CompoundTeeth * k > MaxSunTeeth || layout.IdlerTeeth * k > MaxSunTeeth)
                break;
            var trial = WithCompoundMul(layout, k);
            if (!CompoundPitchHitsSun(trial, sunTeeth, planetCount, planetOffsetDeg))
                break;
            maxK = k;
        }
        return maxK;
    }

    static double IdlerOffsetForSun(FaceMeshLayout layout, int sunTeeth, double preferDeg)
    {
        if (!TryOffsetForSunTeeth(layout, sunTeeth, out var abs))
            return ClampIdlerOffsetDeg(preferDeg);
        var pos = ClampIdlerOffsetDeg(abs);
        var neg = ClampIdlerOffsetDeg(-abs);
        return Math.Abs(preferDeg - neg) < Math.Abs(preferDeg - pos) ? neg : pos;
    }

    static FaceMeshLayout WithCompoundMul(FaceMeshLayout src, int k)
    {
        k = Math.Max(1, k);
        if (k == 1)
            return src;
        var visNc = Math.Clamp(src.CompoundTeeth * k, src.CompoundTeeth, MaxSunTeeth);
        var visNi = Math.Clamp(src.IdlerTeeth * k, src.IdlerTeeth, MaxSunTeeth);
        var ni0 = Math.Max(1, src.IdlerTeeth);
        var compoundPitch = src.IdlerPitch * visNc / (double)ni0;
        var idlerPitch = src.IdlerPitch * visNi / (double)ni0;
        var compound = GearCutter.External(compoundPitch, visNc);
        var idler = GearCutter.External(idlerPitch, visNi);
        return src with
        {
            CompoundPitch = compound.PitchR,
            CompoundTipR = compound.TipR,
            CompoundRootR = compound.RootR,
            IdlerPitch = idler.PitchR,
            IdlerTipR = idler.TipR,
            IdlerRootR = idler.RootR,
            IdlerMeshRadius = MeshCenters(compound, idler),
            DrawCompoundTeeth = visNc,
            DrawIdlerTeeth = visNi
        };
    }

    static double MeshCenters(GearCutter.Cut a, GearCutter.Cut b) =>
        Math.Max(a.PitchR + b.PitchR, Math.Max(a.TipR + b.RootR, a.RootR + b.TipR));

    static bool CompoundPitchHitsSun(
        FaceMeshLayout layout, int sunTeeth, int planetCount, double planetOffsetDeg)
    {
        if (!TryOffsetForSunTeeth(layout, sunTeeth, out var offAbs))
            return false;
        if (planetCount < 2)
            return true;
        var ip = layout.IdlerPitch;
        var ni = Math.Max(1, layout.MeshIdlerTeeth);
        var sunPitch = ip * sunTeeth / (double)ni;
        var sun = GearCutter.External(sunPitch, sunTeeth);
        var sunTip = sun.TipR;
        if (!CompoundBodiesClear(
            layout, planetCount, planetOffsetDeg, layout, planetCount, planetOffsetDeg, true, out _,
            visibleRMin: sunTip, visibleRMax: layout.RingOuter))
            return false;
        var minIdler = 2.0 * layout.IdlerTipR + layout.Module * 0.15;
        var mesh = layout.IdlerMeshRadius;
        for (var i = 0; i < planetCount; i++)
        for (var j = i + 1; j < planetCount; j++)
        {
            var ai = PlanetCarrierAngle(i, planetCount, layout.RingTeeth, planetOffsetDeg);
            var aj = PlanetCarrierAngle(j, planetCount, layout.RingTeeth, planetOffsetDeg);
            var si = IdlerSeatAngle(ai, 0, 1, offAbs);
            var sj = IdlerSeatAngle(aj, 0, 1, offAbs);
            var ix = layout.CarrierRadius * Math.Cos(ai) + mesh * Math.Cos(si);
            var iy = layout.CarrierRadius * Math.Sin(ai) + mesh * Math.Sin(si);
            var jx = layout.CarrierRadius * Math.Cos(aj) + mesh * Math.Cos(sj);
            var jy = layout.CarrierRadius * Math.Sin(aj) + mesh * Math.Sin(sj);
            var gap = Math.Sqrt((ix - jx) * (ix - jx) + (iy - jy) * (iy - jy));
            if (gap + 1e-6 < minIdler)
                return false;
        }
        return true;
    }

    public static double PlanetCarrierAngle(
        int planetIndex, int planetCount, int ringTeeth, double offsetDeg = 0)
    {
        planetCount = Math.Max(1, planetCount);
        ringTeeth = Math.Max(1, ringTeeth);
        var periodDeg = 360.0 / planetCount;
        offsetDeg = WrapOffsetDeg(offsetDeg, periodDeg);
        // Equal spacing (10 planets → 36°). Snap each seat to the nearest ring
        // valley so a planet tooth can nest — 144/N need not be integer.
        var toothIndex = (int)Math.Round(planetIndex * (double)ringTeeth / planetCount) % ringTeeth;
        var ringPitch = Math.PI * 2.0 / ringTeeth;
        var offsetRad = offsetDeg * (Math.PI / 180.0);
        return -Math.PI / 2 + (toothIndex + 0.5) * ringPitch + offsetRad;
    }

    /// <summary>
    /// Spin so a planet tooth points along the carrier into the ring valley at that seat.
    /// At the reference valley, tooth 0 points outward; as the seat moves by Δα the planet
    /// rolls by −Δα·(N_ring/N_planet) so tooth-into-valley is preserved.
    /// </summary>
    public static double PlanetAlignAngle(double carrierAngleRad, int planetTeeth, int ringTeeth)
    {
        planetTeeth = Math.Max(1, planetTeeth);
        ringTeeth = Math.Max(1, ringTeeth);
        var ringPitch = Math.PI * 2.0 / ringTeeth;
        // Reference: first valley clockwise of the 12 o'clock tooth.
        var refValley = -Math.PI / 2 + ringPitch * 0.5;
        var delta = carrierAngleRad - refValley;
        // ψ = α − Δ·(Nr/Np): tooth along carrier at Δ=0, then rolls with the ring.
        return carrierAngleRad - delta * (ringTeeth / (double)planetTeeth);
    }

    /// <summary>
    /// Direction from compound center to the idler seat. Offset 0° = toward clock center;
    /// positive/negative rotate either way (signed degrees, typically −180…+180).
    /// </summary>
    public static double IdlerSeatAngle(
        double compoundCarrierRad, int idlerIndex, int idlerCount, double offsetDeg = 0)
    {
        // Single idler only (pairs are not assemblable with the auto inner ring).
        offsetDeg = ClampIdlerOffsetDeg(offsetDeg);
        var inward = compoundCarrierRad + Math.PI;
        return inward + offsetDeg * (Math.PI / 180.0);
    }

    /// <summary>
    /// External mesh: idler tooth into compound valley along the line of centers.
    /// Opposite spin vs the host: ψ = seat+π + (seat − hostValley)·(N_host/N_idler).
    /// </summary>
    public static double IdlerAlignAngle(
        double seatAngleRad, double hostAngleRad, int hostTeeth, int idlerTeeth)
    {
        hostTeeth = Math.Max(1, hostTeeth);
        idlerTeeth = Math.Max(1, idlerTeeth);
        var hostPitch = Math.PI * 2.0 / hostTeeth;
        var hostValleyOnSeat = hostAngleRad + hostPitch * 0.5;
        var delta = seatAngleRad - hostValleyOnSeat;
        return seatAngleRad + Math.PI + delta * (hostTeeth / (double)idlerTeeth);
    }

    /// <summary>Signed idler travel from the inward seat (exclusive of a full turn).</summary>
    public const double IdlerOffsetMinDeg = -180.0;
    public const double IdlerOffsetMaxDeg = 180.0;

    public static double ClampIdlerOffsetDeg(double offsetDeg)
    {
        if (double.IsNaN(offsetDeg) || double.IsInfinity(offsetDeg))
            return 0;
        if (offsetDeg < IdlerOffsetMinDeg)
            return IdlerOffsetMinDeg;
        if (offsetDeg > IdlerOffsetMaxDeg)
            return IdlerOffsetMaxDeg;
        return offsetDeg;
    }

    /// <summary>
    /// Signed idler offsets that fully assemble: pitch-exact inner ring, every planet’s
    /// idler tip/valley-meshes its compound and the shared sun (real carrier angles),
    /// plus idler↔idler clearance between neighboring planets.
    /// </summary>
    public static IReadOnlyList<double> ExactIdlerOffsetDegs(
        int planetCount = 1, double planetOffsetDeg = 0) =>
        ExactIdlerOffsetDegs(LayoutFaceMesh(120), planetCount, planetOffsetDeg);

    public static IReadOnlyList<double> ExactIdlerOffsetDegs(
        FaceMeshLayout layout, int planetCount = 1, double planetOffsetDeg = 0,
        int? lockSunTeeth = null)
    {
        planetCount = Math.Clamp(planetCount, 1, 12);
        planetOffsetDeg = WrapOffsetDeg(planetOffsetDeg, PlanetOffsetPeriodDeg(planetCount));
        var set = new SortedSet<double>();
        var nsMin = MinSunTeeth;
        var nsMax = MaxSunTeeth;
        if (lockSunTeeth is int locked && locked > 0)
            nsMin = nsMax = Math.Clamp(locked, MinSunTeeth, MaxSunTeeth);

        for (var ns = nsMin; ns <= nsMax; ns++)
        {
            if (!TryOffsetForSunTeeth(layout, ns, out var offAbs))
                continue;
            foreach (var signed in UniqueSigned(offAbs))
            {
                if (ValidateAssembly(layout, planetCount, planetOffsetDeg, signed, ns, out _))
                    AddExactSignedOffset(set, signed);
            }
        }

        for (var off = IdlerOffsetMinDeg; off <= IdlerOffsetMaxDeg + 1e-9; off += 0.1)
        {
            var candidate = ClampIdlerOffsetDeg(Math.Round(off, 3));
            if (!TrySunTeethForOffset(layout, planetCount, planetOffsetDeg, candidate, out var ns))
                continue;
            if (lockSunTeeth is int want && want > 0 && ns != want)
                continue;
            if (ValidateAssembly(layout, planetCount, planetOffsetDeg, candidate, ns, out _))
                AddExactSignedOffset(set, candidate);
        }

        // Locked progression: no unvalidated fallbacks — empty means "won't assemble".
        return set.ToList();
    }

    static IEnumerable<double> UniqueSigned(double offAbs)
    {
        offAbs = Math.Abs(offAbs);
        yield return ClampIdlerOffsetDeg(offAbs);
        if (offAbs > 0.05)
            yield return ClampIdlerOffsetDeg(-offAbs);
    }

    static void AddExactSignedOffset(SortedSet<double> set, double offDeg)
    {
        if (double.IsNaN(offDeg) || double.IsInfinity(offDeg))
            return;
        offDeg = ClampIdlerOffsetDeg(Math.Round(offDeg, 3));
        foreach (var existing in set)
        {
            if (Math.Abs(existing - offDeg) < 0.12)
                return;
        }
        set.Add(offDeg);
    }

    /// <summary>Snap to the nearest fully-validated assembly offset.</summary>
    public static double SnapIdlerOffsetDeg(
        double offsetDeg, int planetCount = 1, double planetOffsetDeg = 0) =>
        SnapIdlerOffsetDeg(LayoutFaceMesh(120), offsetDeg, planetCount, planetOffsetDeg);

    public static double SnapIdlerOffsetDeg(
        FaceMeshLayout layout, double offsetDeg, int planetCount = 1, double planetOffsetDeg = 0,
        int? lockSunTeeth = null)
    {
        offsetDeg = ClampIdlerOffsetDeg(offsetDeg);
        var opts = ExactIdlerOffsetDegs(layout, planetCount, planetOffsetDeg, lockSunTeeth);
        if (opts.Count == 0)
            return offsetDeg;
        var best = opts[0];
        var bestDist = Math.Abs(best - offsetDeg);
        foreach (var o in opts)
        {
            var dist = Math.Abs(o - offsetDeg);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = o;
            }
        }
        return best;
    }

    public readonly record struct SunSeat(int SunTeeth, double OffsetDeg);

    /// <summary>
    /// Every pitch-exact (Ns, idler offset) that validates for this planet count.
    /// Intermediate rings may use any of these — Ns cancels in the RPM product.
    /// Locked last sun also walks 0.1° so a slightly off cosine still meshes.
    /// </summary>
    public static List<SunSeat> EnumerateSunSeats(
        FaceMeshLayout layout, int planetCount, double planetOffsetDeg,
        int? lockSunTeeth, bool scanLockedOffsets = true)
    {
        var found = new List<SunSeat>();
        planetCount = Math.Clamp(planetCount, 1, 12);
        planetOffsetDeg = WrapOffsetDeg(planetOffsetDeg, PlanetOffsetPeriodDeg(planetCount));
        var nsMin = MinSunTeeth;
        var nsMax = MaxSunTeeth;
        if (lockSunTeeth is int locked && locked > 0)
            nsMin = nsMax = Math.Clamp(locked, MinSunTeeth, MaxSunTeeth);

        for (var ns = nsMin; ns <= nsMax; ns++)
        {
            if (!TryOffsetForSunTeeth(layout, ns, out var offAbs))
                continue;
            foreach (var signed in UniqueSigned(offAbs))
            {
                if (!ValidateAssembly(layout, planetCount, planetOffsetDeg, signed, ns, out _))
                    continue;
                found.Add(new SunSeat(ns, signed));
            }
        }

        if (scanLockedOffsets && lockSunTeeth is int want && want > 0)
        {
            for (var off = IdlerOffsetMinDeg; off <= IdlerOffsetMaxDeg + 1e-9; off += 0.1)
            {
                var candidate = ClampIdlerOffsetDeg(Math.Round(off, 3));
                if (!TrySunTeethForOffset(layout, planetCount, planetOffsetDeg, candidate, out var ns)
                    || ns != want)
                    continue;
                if (!ValidateAssembly(layout, planetCount, planetOffsetDeg, candidate, ns, out _))
                    continue;
                if (found.Any(s => s.SunTeeth == ns && Math.Abs(s.OffsetDeg - candidate) < 0.12))
                    continue;
                found.Add(new SunSeat(ns, candidate));
            }
        }

        return found;
    }

    /// <summary>
    /// First validated idler seat. Integer-Ns path only (no 0.1° scan unless locked).
    /// </summary>
    public static bool TryFirstValidSeat(
        FaceMeshLayout layout, int planetCount, double planetOffsetDeg,
        int? lockSunTeeth, out double offsetDeg, out int sunTeeth,
        bool scanLockedOffsets = true)
    {
        var seats = EnumerateSunSeats(
            layout, planetCount, planetOffsetDeg, lockSunTeeth, scanLockedOffsets);
        if (seats.Count == 0)
        {
            offsetDeg = 0;
            sunTeeth = 0;
            return false;
        }
        offsetDeg = seats[0].OffsetDeg;
        sunTeeth = seats[0].SunTeeth;
        return true;
    }

    /// <summary>
    /// True when every stage assembles and the last sun is locked to the exact ×1440 count.
    /// Intermediate suns may auto-fit — they cancel in the product.
    /// </summary>
    public static bool TryAssembleProgression(
        ToothProgression prog,
        double rim,
        IReadOnlyList<int> planetCounts,
        IReadOnlyList<double> planetOffsetsDeg,
        out string failReason)
    {
        var ncs = new int[prog.StageCount];
        for (var i = 0; i < ncs.Length; i++)
            ncs[i] = prog.CompoundAt(i);
        return TryAssembleCompounds(ncs, rim, planetCounts, out failReason);
    }

    public static int MaxAssembledPlanets(IReadOnlyList<int> compounds, double rim)
    {
        var max = 0;
        for (var p = 1; p <= 12; p++)
        {
            if (TryAssembleCompounds(compounds, rim, p, out _))
                max = p;
        }
        return max;
    }

    public readonly record struct StageSolve(
        int PlanetCount,
        double PlanetOffsetDeg,
        double IdlerOffsetDeg,
        FaceMeshLayout Layout,
        InnerRingFit Fit);

    /// <summary>
    /// Fixed-carrier ratio R = (Nh/Np)·(Nc/Ns). Willis: ω_s = R·ω_h + (1−R)·ω_c.
    /// <paramref name="visualCompound"/> uses the drawn (k-scaled) compound teeth so a
    /// sized-up idler still drives its inner ring; the last sun keeps kinematic Nc.
    /// </summary>
    public static double StageRatioR(FaceMeshLayout layout, int sunTeeth, bool visualCompound = false)
    {
        sunTeeth = Math.Max(1, sunTeeth);
        var nc = visualCompound
            ? Math.Max(1, layout.MeshCompoundTeeth)
            : Math.Max(1, layout.CompoundTeeth);
        return (layout.RingTeeth / (double)Math.Max(1, layout.PlanetTeeth))
            * (nc / (double)sunTeeth);
    }

    /// <summary>
    /// Invert Willis per stage so ring speeds form a geometric ladder from
    /// <paramref name="outerSec"/> (drawn outer ring) to <paramref name="innerSec"/>
    /// (center sun, 1 rpm when the second hand is 1 rpm). Returns carrier
    /// secScale (revolutions per second-hand revolution).
    /// </summary>
    public static double[] SolveCarrierSecScales(
        IReadOnlyList<StageSolve> stages, double outerSec, double innerSec)
    {
        var n = stages.Count;
        var carriers = new double[n];
        if (n == 0)
            return carriers;
        if (Math.Abs(outerSec) < 1e-18)
            outerSec = 1e-18;
        if (Math.Abs(innerSec) < 1e-18)
            innerSec = 1e-18;
        var w = new double[n + 1];
        w[0] = outerSec;
        w[n] = innerSec;
        var log0 = Math.Log(Math.Abs(outerSec));
        var logN = Math.Log(Math.Abs(innerSec));
        var sign = outerSec < 0 ? -1.0 : 1.0;
        for (var i = 1; i < n; i++)
            w[i] = sign * Math.Exp(log0 + (logN - log0) * (i / (double)n));
        for (var i = 0; i < n; i++)
        {
            // Last stage stays kinematic so the center sun can lock at 1 rpm.
            var R = StageRatioR(stages[i].Layout, stages[i].Fit.Teeth, visualCompound: i < n - 1);
            var den = 1.0 - R;
            carriers[i] = Math.Abs(den) < 1e-12 ? 0 : (w[i + 1] - R * w[i]) / den;
        }
        return carriers;
    }

    /// <summary>
    /// Same outer/sun locks as <see cref="SolveCarrierSecScales"/>. Auto
    /// prefers one shared |ω_c| so every unlocked carousel moves together.
    /// Forced CW/CCW fix the sign. Pinned rings keep the given °/min (0 =
    /// stand still) and do not take the min-speed floor. <paramref name="minSec"/>
    /// floors every unlocked ring: if equalizing would leave one slower than
    /// that, unlocked rings may run at different |ω_c|. If the floor is
    /// impossible, maximize the slowest unlocked carousel.
    /// </summary>
    public static double[] SolveEqualCarrierSecScales(
        IReadOnlyList<StageSolve> stages, double outerSec, double innerSec,
        IReadOnlyList<ClockGearCarrierDir>? dirs, double minSec = 0,
        IReadOnlyList<bool>? pinned = null, IReadOnlyList<double>? pinDegPerMin = null)
    {
        var n = stages.Count;
        var fallback = SolveCarrierSecScales(stages, outerSec, innerSec);
        if (n == 0)
            return fallback;
        var R = new double[n];
        var pinMask = new bool[n];
        var pinVal = new double[n];
        var pinCount = 0;
        for (var i = 0; i < n; i++)
        {
            R[i] = StageRatioR(stages[i].Layout, stages[i].Fit.Teeth, visualCompound: i < n - 1);
            var d = dirs != null && i < dirs.Count ? dirs[i] : ClockGearCarrierDir.Auto;
            if (d == ClockGearCarrierDir.Still)
            {
                pinMask[i] = true;
                pinVal[i] = 0;
                pinCount++;
            }
            else if (pinned != null && i < pinned.Count && pinned[i])
            {
                pinMask[i] = true;
                pinCount++;
                var dpm = pinDegPerMin != null && i < pinDegPerMin.Count ? pinDegPerMin[i] : 0;
                pinVal[i] = Math.Abs(dpm) < 0.05 ? 0 : dpm / 360.0;
            }
        }
        if (pinCount >= n && n > 0)
        {
            pinMask[n - 1] = false;
            pinCount--;
        }
        var anyPin = pinCount > 0;
        var forced = new int[n];
        var autoAt = new List<int>();
        var seed = new int[n];
        var target = 0.0;
        var targetN = 0;
        for (var i = 0; i < n; i++)
        {
            var d = dirs != null && i < dirs.Count ? dirs[i] : ClockGearCarrierDir.Auto;
            forced[i] = pinMask[i] ? 0
                : d == ClockGearCarrierDir.Clockwise ? 1
                : d == ClockGearCarrierDir.Counterclockwise ? -1 : 0;
            var s = Math.Sign(fallback[i]);
            seed[i] = s == 0 ? 1 : s;
            if (!pinMask[i])
            {
                target += Math.Abs(fallback[i]);
                targetN++;
                if (forced[i] == 0)
                    autoAt.Add(i);
            }
        }
        target = targetN > 0 ? target / targetN : 0;
        if (target < 1e-6)
            target = Math.Abs(innerSec - outerSec) / Math.Max(1, n);
        minSec = Math.Max(0, minSec);
        var floor = minSec > 1e-8;
        var want = floor ? Math.Max(target, minSec) : target;
        var maxSec = Math.Max(8, minSec * 4);

        var signs = new int[n];
        var patterns = 1 << autoAt.Count;
        double bestScore = double.PositiveInfinity;
        double[]? best = null;

        void Consider(int pattern, double[] carriers)
        {
            var score = ScoreCarrierFloor(carriers, minSec, floor, want, pattern == 0, pinMask);
            if (score + 1e-12 >= bestScore)
                return;
            bestScore = score;
            best = (double[])carriers.Clone();
        }

        void Search(double need)
        {
            for (var p = 0; p < patterns; p++)
            {
                for (var i = 0; i < n; i++)
                    signs[i] = forced[i] != 0 ? forced[i] : seed[i];
                for (var k = 0; k < autoAt.Count; k++)
                {
                    if (((p >> k) & 1) != 0)
                        signs[autoAt[k]] = -signs[autoAt[k]];
                }
                if (TryEqualUnpinnedMagnitude(
                        R, signs, pinMask, pinVal, outerSec, innerSec, out var v, out var eq)
                    && v + 1e-12 >= need)
                    Consider(p, eq);
                if ((need > 1e-8 || anyPin)
                    && TryFloorCarrierMagnitudes(
                        R, signs, pinMask, pinVal, outerSec, innerSec,
                        need, maxSec, out var floored))
                    Consider(p, floored);
            }
        }

        Search(floor ? minSec : 0);
        if (floor && (best == null || UnpinnedMinAbs(best, pinMask) + 1e-12 < minSec))
        {
            var lo = 0.0;
            var hi = minSec;
            for (var step = 0; step < 40; step++)
            {
                var mid = 0.5 * (lo + hi);
                best = null;
                bestScore = double.PositiveInfinity;
                Search(mid);
                if (best != null && UnpinnedMinAbs(best, pinMask) + 1e-12 >= mid)
                    lo = mid;
                else
                    hi = mid;
            }
            best = null;
            bestScore = double.PositiveInfinity;
            Search(lo);
        }
        if (best == null && anyPin)
            best = ForcePinnedSolve(R, pinMask, pinVal, outerSec, innerSec);
        return best ?? fallback;
    }

    static double UnpinnedMinAbs(double[] carriers, bool[] pinned)
    {
        var m = double.PositiveInfinity;
        var any = false;
        for (var i = 0; i < carriers.Length; i++)
        {
            if (i < pinned.Length && pinned[i])
                continue;
            m = Math.Min(m, Math.Abs(carriers[i]));
            any = true;
        }
        return any && !double.IsInfinity(m) ? m : 0;
    }

    static double[] ForcePinnedSolve(
        double[] ratio, bool[] pinned, double[] pinSec, double outerSec, double innerSec)
    {
        var n = ratio.Length;
        var c = new double[n];
        var slack = -1;
        for (var i = n - 1; i >= 0; i--)
        {
            if (!pinned[i])
            {
                slack = i;
                break;
            }
        }
        for (var i = 0; i < n; i++)
        {
            if (pinned[i])
                c[i] = pinSec[i];
        }
        if (slack < 0)
            return c;
        double a = outerSec, b = 0;
        for (var i = 0; i < n; i++)
        {
            var r = ratio[i];
            if (i == slack)
            {
                var nextA = r * a;
                var nextB = r * b + (1.0 - r);
                a = nextA;
                b = nextB;
            }
            else
            {
                a = r * a + (1.0 - r) * c[i];
                b *= r;
            }
        }
        if (Math.Abs(b) > 1e-12)
            c[slack] = (innerSec - a) / b;
        return c;
    }

    /// <summary>
    /// Null when outer face rate, 1 rpm sun, locked speeds, forced directions
    /// and the min floor are all consistent with <paramref name="carrierSec"/>.
    /// </summary>
    public static string? DiagnoseCarrierLocks(
        IReadOnlyList<StageSolve> stages,
        double outerSec,
        double innerSec,
        IReadOnlyList<double> carrierSec,
        bool auto,
        IReadOnlyList<ClockGearCarrierDir>? dirs = null,
        double minSec = 0,
        IReadOnlyList<bool>? pinned = null,
        IReadOnlyList<double>? pinDegPerMin = null)
    {
        var n = stages.Count;
        if (n == 0)
            return null;
        const double tol = 0.004; // ~1.4 °/min
        var notes = new List<string>();
        var w = outerSec;
        var sun = outerSec;
        for (var i = 0; i < n; i++)
        {
            var R = StageRatioR(stages[i].Layout, stages[i].Fit.Teeth, visualCompound: i < n - 1);
            var c = i < carrierSec.Count ? carrierSec[i] : 0;
            sun = R * w + (1.0 - R) * c;
            if (i < n - 1)
                w = sun;
        }
        if (Math.Abs(sun - innerSec) > tol)
        {
            notes.Add(auto
                ? $"These carousel locks don’t line up: center would be {FormatLockRpm(sun)}, not 1 rpm. Relax a locked speed, a direction, or the min floor."
                : $"These °/min don’t line up: center would be {FormatLockRpm(sun)}, not 1 rpm. Auto can keep the outer ring on the face rate and the sun at 1 rpm.");
        }
        minSec = Math.Max(0, minSec);
        for (var i = 0; i < n; i++)
        {
            var c = i < carrierSec.Count ? carrierSec[i] : 0;
            var d = dirs != null && i < dirs.Count ? dirs[i] : ClockGearCarrierDir.Auto;
            var still = d == ClockGearCarrierDir.Still;
            var isPin = still || (pinned != null && i < pinned.Count && pinned[i]);
            if (isPin)
            {
                var dpm = still ? 0
                    : pinDegPerMin != null && i < pinDegPerMin.Count ? pinDegPerMin[i] : 0;
                var want = Math.Abs(dpm) < 0.05 ? 0 : dpm / 360.0;
                if (Math.Abs(c - want) > tol)
                    notes.Add($"Ring {i + 1} is locked at {FormatLockDpm(want * 360)} but the train needs {FormatLockDpm(c * 360)} to mesh.");
                continue;
            }
            if (!auto)
                continue;
            if (d == ClockGearCarrierDir.Clockwise && c < -tol)
                notes.Add($"Ring {i + 1} is set clockwise but is turning the other way.");
            else if (d == ClockGearCarrierDir.Counterclockwise && c > tol)
                notes.Add($"Ring {i + 1} is set counter-clockwise but is turning the other way.");
            if (minSec > 1e-8 && Math.Abs(c) + 1e-12 < minSec)
                notes.Add($"Ring {i + 1} is below the min floor ({FormatLockDpm(Math.Abs(c) * 360)} < {FormatLockDpm(minSec * 360)}).");
        }
        return notes.Count == 0 ? null : string.Join("\n", notes);
    }

    static string FormatLockRpm(double sec)
    {
        var a = Math.Abs(sec);
        var sign = sec < 0 || double.IsNegative(sec) ? "−" : "";
        if (a < 1e-9)
            return $"{sign}0 rpm";
        if (a >= 0.05)
            return $"{sign}{a:0.##} rpm";
        if (a >= 0.001)
            return $"{sign}{a:0.###} rpm";
        return $"{sign}{a:0.###e0} rpm";
    }

    static string FormatLockDpm(double dpm)
    {
        var a = Math.Abs(dpm);
        var sign = dpm < 0 || double.IsNegative(dpm) ? "−" : "+";
        if (a < 0.05)
            return $"{sign}0°/m";
        if (Math.Abs(dpm - Math.Round(dpm)) < 0.05)
            return $"{sign}{Math.Abs(Math.Round(dpm)):0}°/m";
        return $"{dpm:+0.0;-0.0}°/m";
    }

    static double ScoreCarrierFloor(
        double[] carriers, double minSec, bool floor, double want, bool seed, bool[] pinned)
    {
        var minAbs = UnpinnedMinAbs(carriers, pinned);
        var maxAbs = 0.0;
        var sumAbs = 0.0;
        var count = 0;
        for (var i = 0; i < carriers.Length; i++)
        {
            if (i < pinned.Length && pinned[i])
                continue;
            var a = Math.Abs(carriers[i]);
            maxAbs = Math.Max(maxAbs, a);
            sumAbs += a;
            count++;
        }
        var n = Math.Max(1, count);
        var mean = sumAbs / n;
        var score = maxAbs - minAbs;
        score += 0.05 * Math.Abs(mean - want);
        if (floor && minAbs + 1e-12 < minSec)
            score += 1e9 * (minSec - minAbs);
        if (seed)
            score *= 0.5;
        return score;
    }

    static bool TryEqualUnpinnedMagnitude(
        double[] ratio, int[] signs, bool[] pinned, double[] pinSec,
        double outerSec, double innerSec, out double v, out double[] carriers)
    {
        v = 0;
        var n = ratio.Length;
        carriers = new double[n];
        double a = outerSec, b = 0;
        var anyFree = false;
        for (var i = 0; i < n; i++)
        {
            var r = ratio[i];
            if (pinned[i])
            {
                a = r * a + (1.0 - r) * pinSec[i];
                b *= r;
            }
            else
            {
                anyFree = true;
                a *= r;
                b = r * b + (1.0 - r) * signs[i];
            }
        }
        if (!anyFree || Math.Abs(b) < 1e-12)
            return false;
        v = (innerSec - a) / b;
        if (double.IsNaN(v) || double.IsInfinity(v) || v <= 1e-8)
            return false;
        for (var i = 0; i < n; i++)
            carriers[i] = pinned[i] ? pinSec[i] : signs[i] * v;
        return true;
    }

    /// <summary>
    /// Unpinned carriers in stages 0..n−2 are free at least <paramref name="minSec"/>
    /// (with the given signs). Pinned stages stay put. The last unpinned ring is
    /// fixed by the sun lock and must also meet that floor in its signed direction.
    /// </summary>
    static bool TryFloorCarrierMagnitudes(
        double[] ratio, int[] signs, bool[] pinned, double[] pinSec,
        double outerSec, double innerSec, double minSec, double maxSec,
        out double[] carriers)
    {
        var n = ratio.Length;
        carriers = new double[n];
        if (n == 0)
            return true;
        if (pinned[n - 1])
            return false;
        if (n == 1)
        {
            var den0 = 1.0 - ratio[0];
            var c0 = Math.Abs(den0) < 1e-12 ? 0 : (innerSec - ratio[0] * outerSec) / den0;
            carriers[0] = c0;
            return signs[0] * c0 + 1e-12 >= minSec
                && !double.IsNaN(c0) && !double.IsInfinity(c0);
        }

        var freeList = new List<int>();
        for (var i = 0; i < n - 1; i++)
        {
            if (!pinned[i])
                freeList.Add(i);
        }
        var freeIdx = freeList.ToArray();
        var nFree = freeIdx.Length;
        var cnst = outerSec;
        var coeff = new double[nFree];
        for (var i = 0; i < n - 1; i++)
        {
            var r = ratio[i];
            if (pinned[i])
            {
                cnst = r * cnst + (1.0 - r) * pinSec[i];
                for (var j = 0; j < nFree; j++)
                    coeff[j] *= r;
                continue;
            }
            var k = Array.IndexOf(freeIdx, i);
            var nextCnst = r * cnst;
            var nextCoeff = new double[nFree];
            for (var j = 0; j < nFree; j++)
                nextCoeff[j] = r * coeff[j];
            if (k >= 0)
                nextCoeff[k] += 1.0 - r;
            cnst = nextCnst;
            coeff = nextCoeff;
        }

        var rLast = ratio[n - 1];
        var den = 1.0 - rLast;
        if (Math.Abs(den) < 1e-12)
            return false;
        var P = (innerSec - rLast * cnst) / den;
        var Q = new double[nFree];
        for (var j = 0; j < nFree; j++)
            Q[j] = -rLast * coeff[j] / den;

        if (nFree == 0)
        {
            carriers[n - 1] = P;
            for (var i = 0; i < n - 1; i++)
                carriers[i] = pinSec[i];
            return !double.IsNaN(P) && !double.IsInfinity(P)
                && signs[n - 1] * P + 1e-8 >= minSec
                && Math.Abs(P) <= maxSec + 1e-6;
        }

        var a0 = signs[n - 1] * P;
        var a = new double[nFree];
        var aSum = 0.0;
        for (var j = 0; j < nFree; j++)
        {
            a[j] = signs[n - 1] * Q[j] * signs[freeIdx[j]];
            aSum += a[j];
        }

        var u = new double[nFree];
        for (var j = 0; j < nFree; j++)
            u[j] = Math.Max(0, minSec);

        if (Math.Abs(aSum) > 1e-14)
        {
            var t = minSec;
            if (aSum > 0)
                t = Math.Max(minSec, (minSec - a0) / aSum);
            else
            {
                var hi = (minSec - a0) / aSum;
                if (hi + 1e-12 < minSec)
                    t = double.NaN;
                else
                    t = minSec;
            }
            if (!double.IsNaN(t) && t <= maxSec + 1e-9)
            {
                for (var j = 0; j < nFree; j++)
                    u[j] = t;
                if (TryWriteFlooredCarriers(
                        carriers, signs, pinned, pinSec, freeIdx, u, P, Q, minSec, maxSec))
                    return true;
            }
        }
        else if (a0 + 1e-12 >= minSec)
        {
            if (TryWriteFlooredCarriers(
                    carriers, signs, pinned, pinSec, freeIdx, u, P, Q, minSec, maxSec))
                return true;
        }

        var acc = a0;
        for (var j = 0; j < nFree; j++)
            acc += a[j] * minSec;
        for (var j = 0; j < nFree; j++)
            u[j] = minSec;
        if (acc + 1e-12 < minSec)
        {
            var pos = 0.0;
            for (var j = 0; j < nFree; j++)
            {
                if (a[j] > 1e-14)
                    pos += a[j];
            }
            if (pos < 1e-14)
                return false;
            var extra = (minSec - acc) / pos;
            for (var j = 0; j < nFree; j++)
            {
                if (a[j] > 1e-14)
                    u[j] = Math.Min(maxSec, minSec + extra);
            }
        }
        return TryWriteFlooredCarriers(
            carriers, signs, pinned, pinSec, freeIdx, u, P, Q, minSec, maxSec);
    }

    static bool TryWriteFlooredCarriers(
        double[] carriers, int[] signs, bool[] pinned, double[] pinSec,
        int[] freeIdx, double[] mag, double P, double[] Q,
        double minSec, double maxSec)
    {
        var n = carriers.Length;
        var last = n - 1;
        for (var i = 0; i < n; i++)
        {
            if (pinned[i])
                carriers[i] = pinSec[i];
        }
        for (var j = 0; j < freeIdx.Length; j++)
        {
            if (mag[j] < minSec - 1e-12 || mag[j] > maxSec + 1e-9)
                return false;
            carriers[freeIdx[j]] = signs[freeIdx[j]] * mag[j];
        }
        var lastVal = P;
        for (var j = 0; j < freeIdx.Length; j++)
            lastVal += Q[j] * carriers[freeIdx[j]];
        carriers[last] = lastVal;
        return !double.IsNaN(lastVal) && !double.IsInfinity(lastVal)
            && signs[last] * lastVal + 1e-8 >= minSec
            && Math.Abs(lastVal) <= maxSec + 1e-6;
    }

    /// <summary>
    /// Walk every stage at this rim. Each planet count is chosen from values that
    /// still let the rest of the train seat (want first, then nearest). Idlers snap
    /// to a validated offset. Empty result = no train.
    /// </summary>
    public static bool TrySolveTrain(
        ToothProgression prog,
        double rim,
        IReadOnlyList<int> wantCounts,
        IReadOnlyList<double> wantPlanetOffs,
        IReadOnlyList<double> wantIdlerOffs,
        out List<StageSolve> stages,
        out string failReason,
        bool snapIdlers = true)
    {
        stages = [];
        rim = Math.Max(28, rim);
        var last = ExactLastSunTeeth(prog.Compounds());
        if (last <= 0)
        {
            failReason = "no exact last sun";
            return false;
        }

        if (!TryCanonicalSuns(prog.Compounds(), rim, out var suns, out failReason))
        {
            stages = [];
            return false;
        }

        if (!TrySolveFrom(prog, rim, last, suns, 0, default, wantCounts, wantPlanetOffs, wantIdlerOffs,
                stages, out failReason))
        {
            stages = [];
            return false;
        }

        if (snapIdlers)
        {
            for (var i = 0; i < stages.Count; i++)
            {
                var st = stages[i];
                var idlerWant = wantIdlerOffs != null && i < wantIdlerOffs.Count
                    ? wantIdlerOffs[i]
                    : st.IdlerOffsetDeg;
                int? lockSun = i < suns.Length ? suns[i] : i == stages.Count - 1 ? last : null;
                var snapped = SnapIdlerOffsetDeg(
                    st.Layout, idlerWant, st.PlanetCount, st.PlanetOffsetDeg, lockSun);
                var fit = FitInnerRing(
                    st.Layout, st.PlanetCount, st.PlanetOffsetDeg, snapped, lockSun);
                if (fit.AssemblyOk)
                    stages[i] = st with { IdlerOffsetDeg = snapped, Fit = fit };
            }
        }

        failReason = "ok";
        return true;
    }

    static bool TrySolveFrom(
        ToothProgression prog, double rim, int lastSun, int[] lockSuns, int index, InnerRingFit prevFit,
        IReadOnlyList<int> wantCounts, IReadOnlyList<double> wantPlanetOffs,
        IReadOnlyList<double> wantIdlerOffs, List<StageSolve> stages, out string failReason)
    {
        failReason = "";
        if (index >= prog.StageCount)
            return true;

        var nc = Math.Max(FacePlanetTeeth * 2, prog.CompoundAt(index));
        var layout = index == 0
            ? LayoutFaceMesh(rim, nc)
            : LayoutFromInternalHost(ResolveInnerRingBand(prevFit), prevFit.Teeth, nc);
        int? lockSun = index < lockSuns.Length && lockSuns[index] > 0
            ? lockSuns[index]
            : index == prog.StageCount - 1 ? lastSun : null;
        var wantP = wantCounts is { Count: > 0 }
            ? Math.Clamp(wantCounts[Math.Min(index, wantCounts.Count - 1)], 1, 12)
            : 1;
        var wantOff = wantPlanetOffs != null && index < wantPlanetOffs.Count
            ? wantPlanetOffs[index]
            : 0;
        var planets = wantP;
        var planetOff = WrapOffsetDeg(wantOff, PlanetOffsetPeriodDeg(planets));
        if (!TryFirstValidSeat(layout, planets, planetOff, lockSun, out var firstSeat, out var ns))
        {
            failReason = lockSun is int t
                ? $"stage{index + 1} last {t}T unreachable at {planets} planets"
                : $"stage{index + 1} unreachable at {planets} planets";
            return false;
        }
        var fit = FitInnerRing(layout, planets, planetOff, firstSeat, lockSun ?? ns);
        if (!fit.AssemblyOk)
        {
            failReason = $"stage{index + 1} {fit.FailReason}";
            return false;
        }

        stages.Add(new StageSolve(planets, planetOff, firstSeat, layout, fit));
        if (TrySolveFrom(prog, rim, lastSun, lockSuns, index + 1, fit,
                wantCounts, wantPlanetOffs, wantIdlerOffs, stages, out failReason))
            return true;
        stages.RemoveAt(stages.Count - 1);
        return false;
    }

    public static List<int> ValidPlanetCounts(
        FaceMeshLayout layout, double planetOffsetDeg, int? lockSunTeeth)
    {
        var list = new List<int>();
        for (var p = 1; p <= 12; p++)
        {
            var off = WrapOffsetDeg(planetOffsetDeg, PlanetOffsetPeriodDeg(p));
            if (TryFirstValidSeat(layout, p, off, lockSunTeeth, out _, out _))
                list.Add(p);
        }
        return list;
    }

    /// <summary>
    /// Planet counts for this stage that still let the remaining rings seat,
    /// with later stages allowed to snap. Earlier stages stay locked.
    /// </summary>
    public static List<int> CompletingPlanetCounts(
        ToothProgression prog, double rim, int stageIndex,
        IReadOnlyList<int> wantCounts, IReadOnlyList<double> wantPlanetOffs,
        IReadOnlyList<double> wantIdlerOffs)
    {
        var found = new List<int>();
        var want = wantCounts?.ToList() ?? Enumerable.Repeat(1, 5).ToList();
        while (want.Count < 5)
            want.Add(want[^1]);
        for (var p = 1; p <= 12; p++)
        {
            want[stageIndex] = p;
            if (TrySolveTrain(prog, rim, want, wantPlanetOffs, wantIdlerOffs, out var stages, out _, snapIdlers: false)
                && stages.Count > stageIndex && stages[stageIndex].PlanetCount == p)
                found.Add(p);
        }
        return found;
    }

    public static int ExactLastSunTeeth(int nc1, int nc2, int nc3 = 0) =>
        ExactLastSunTeeth(nc3 <= 0 ? [nc1, nc2] : [nc1, nc2, nc3]);

    /// <summary>
    /// Last sun for an n-stage idler train: 144·ΠNc / (6^n · Ns) = 1440 → ΠNc = 10·6^n·Ns.
    /// </summary>
    public static int ExactLastSunTeeth(IReadOnlyList<int> compounds)
    {
        if (compounds.Count < 2)
            return 0;
        long prod = 1;
        foreach (var nc in compounds)
        {
            if (nc <= 0)
                return 0;
            prod *= nc;
        }
        long denom = 10;
        for (var i = 0; i < compounds.Count; i++)
            denom *= FacePlanetTeeth;
        if (prod % denom != 0)
            return 0;
        var last = prod / denom;
        if (last < MinSunTeeth || last > MaxSunTeeth)
            return 0;
        return (int)last;
    }

    public static bool TryAssembleCompounds(
        IReadOnlyList<int> compounds, double rim, int planetCount, out string failReason) =>
        TryAssembleCompounds(compounds, rim, (IReadOnlyList<int>)[planetCount], out failReason);

    public static bool TryAssembleCompounds(
        IReadOnlyList<int> compounds, double rim, IReadOnlyList<int> planetCounts, out string failReason)
    {
        failReason = "";
        var last = ExactLastSunTeeth(compounds);
        if (last <= 0)
        {
            failReason = "no exact last sun";
            return false;
        }

        rim = Math.Max(28, rim);
        if (!TryCanonicalSuns(compounds, rim, out var suns, out failReason))
            return false;
        return TryAssembleFrom(compounds, rim, planetCounts, last, suns, 0, default, out failReason);
    }

    /// <summary>
    /// Inner-ring tooth counts for this train at 1 planet per ring. Planet count
    /// is cosmetic — extra planets must mesh these same suns so rings do not move.
    /// </summary>
    public static bool TryCanonicalSuns(
        IReadOnlyList<int> compounds, double rim, out int[] suns, out string failReason)
    {
        suns = [];
        failReason = "";
        var last = ExactLastSunTeeth(compounds);
        if (last <= 0)
        {
            failReason = "no exact last sun";
            return false;
        }

        rim = Math.Max(28, rim);
        suns = new int[compounds.Count];
        InnerRingFit fit = default;
        for (var i = 0; i < compounds.Count; i++)
        {
            var nc = Math.Max(FacePlanetTeeth * 2, compounds[i]);
            var layout = i == 0
                ? LayoutFaceMesh(rim, nc)
                : LayoutFromInternalHost(ResolveInnerRingBand(fit), fit.Teeth, nc);
            int? lockSun = i == compounds.Count - 1 ? last : null;
            if (!TryFirstValidSeat(layout, 1, 0, lockSun, out var off, out var ns))
            {
                failReason = lockSun is int t
                    ? $"stage{i + 1} last {t}T unreachable"
                    : $"stage{i + 1} unreachable";
                suns = [];
                return false;
            }
            var seatNs = lockSun ?? ns;
            fit = FitInnerRing(layout, 1, 0, off, seatNs);
            if (!fit.AssemblyOk)
            {
                failReason = $"stage{i + 1} {fit.FailReason}";
                suns = [];
                return false;
            }
            suns[i] = fit.Teeth;
        }

        failReason = "ok";
        return true;
    }

    static bool TryAssembleFrom(
        IReadOnlyList<int> compounds, double rim, IReadOnlyList<int> planetCounts,
        int lastSun, int[] lockSuns, int index, InnerRingFit prevFit, out string failReason)
    {
        failReason = "";
        if (index >= compounds.Count)
        {
            failReason = "ok";
            return true;
        }

        var planetCount = planetCounts is { Count: > 0 }
            ? Math.Clamp(planetCounts[Math.Min(index, planetCounts.Count - 1)], 1, 12)
            : 1;
        var nc = Math.Max(FacePlanetTeeth * 2, compounds[index]);
        var layout = index == 0
            ? LayoutFaceMesh(rim, nc)
            : LayoutFromInternalHost(ResolveInnerRingBand(prevFit), prevFit.Teeth, nc);
        int? lockSun = index < lockSuns.Length && lockSuns[index] > 0
            ? lockSuns[index]
            : index == compounds.Count - 1 ? lastSun : null;
        if (!TryFirstValidSeat(layout, planetCount, 0, lockSun, out var off, out var ns))
        {
            failReason = lockSun is int t
                ? $"stage{index + 1} last {t}T unreachable"
                : $"stage{index + 1} unreachable";
            return false;
        }
        var fit = FitInnerRing(layout, planetCount, 0, off, lockSun ?? ns);
        if (!fit.AssemblyOk)
        {
            failReason = $"stage{index + 1} {fit.FailReason}";
            return false;
        }
        return TryAssembleFrom(
            compounds, rim, planetCounts, lastSun, lockSuns, index + 1, fit, out failReason);
    }

    public static IEnumerable<ToothProgression> EnumerateExactCompoundCandidates()
    {
        int[] ncs = [18, 24, 30, 36, 40, 48, 54, 60, 72, 80, 90, 96];
        foreach (var a in ncs)
        foreach (var b in ncs)
        {
            var last2 = ExactLastSunTeeth(a, b);
            if (last2 >= MinSunTeeth && last2 <= MaxSunTeeth)
                yield return ProgressionFromCompounds($"6+{a} / 6+{b}", a, b, 0);
            foreach (var c in ncs)
            {
                var last3 = ExactLastSunTeeth(a, b, c);
                if (last3 >= MinSunTeeth && last3 <= MaxSunTeeth)
                    yield return ProgressionFromCompounds($"6+{a} / 6+{b} / 6+{c}", a, b, c);
            }
        }
    }

    public static Geometry BuildIdlerGear(
        double cx, double cy, FaceMeshLayout layout) =>
        GearCutter.BuildExternal(cx, cy, layout.IdlerPitch, layout.MeshIdlerTeeth);

    /// <summary>Distance from clock center to an idler center at the current signed offset.</summary>
    public static double IdlerCenterDistance(
        FaceMeshLayout layout, int idlerCount, double idlerOffsetDeg)
    {
        var off = ClampIdlerOffsetDeg(idlerOffsetDeg) * (Math.PI / 180.0);
        var c = layout.CarrierRadius;
        var m = layout.IdlerMeshRadius;
        return Math.Sqrt(Math.Max(0, c * c + m * m - 2.0 * c * m * Math.Cos(off)));
    }

    /// <summary>
    /// Same tip↔root slack used for compound↔idler (mesh radius − pitch sum).
    /// Applied to idler↔inner so gear:gear gaps stay consistent.
    /// </summary>
    public static double IdlerMeshExtra(FaceMeshLayout layout)
    {
        var extra = layout.IdlerMeshRadius - (layout.CompoundPitch + layout.IdlerPitch);
        return Math.Max(0, extra);
    }

    static bool TryOffsetForSunTeeth(FaceMeshLayout layout, int sunTeeth, out double offsetAbsDeg)
    {
        offsetAbsDeg = 0;
        var ip = Math.Max(0.5, layout.IdlerPitch);
        var ni = Math.Max(1, layout.MeshIdlerTeeth);
        var d = ip * (sunTeeth + ni) / (double)ni
            + IdlerMeshExtra(layout);
        var c = layout.CarrierRadius;
        var m = layout.IdlerMeshRadius;
        var dMin = Math.Abs(c - m);
        var dMax = c + m;
        if (d < dMin - 1e-6 || d > dMax + 1e-6)
            return false;
        var cos = (c * c + m * m - d * d) / (2.0 * c * m);
        if (double.IsNaN(cos))
            return false;
        cos = Math.Clamp(cos, -1.0, 1.0);
        offsetAbsDeg = Math.Acos(cos) * (180.0 / Math.PI);
        return true;
    }

    static bool TrySunTeethForOffset(
        FaceMeshLayout layout, int planetCount, double planetOffsetDeg,
        double idlerOffsetDeg, out int sunTeeth)
    {
        sunTeeth = 0;
        if (!TryMeasureIdlers(layout, planetCount, planetOffsetDeg, idlerOffsetDeg,
                out var dMean, out _, out _))
            return false;
        var ip = Math.Max(0.5, layout.IdlerPitch);
        var pitchBudget = Math.Max(ip * 1.5, dMean - IdlerMeshExtra(layout));
        var nExact = layout.IdlerTeeth * (pitchBudget / ip - 1.0);
        sunTeeth = (int)Math.Clamp(Math.Round(nExact), MinSunTeeth, MaxSunTeeth);
        return Math.Abs(nExact - sunTeeth) < 0.08;
    }

    /// <summary>
    /// Size + tooth count the inner ring; validates every planet idler against
    /// compound + shared sun using real carrier seats. Optional lockSunTeeth forces
    /// a progression tooth count (pitch still follows idler mesh).
    /// </summary>
    public static InnerRingFit FitInnerRing(
        FaceMeshLayout layout,
        int planetCount,
        double planetOffsetDeg,
        double idlerOffsetDeg,
        int? lockSunTeeth = null)
    {
        planetCount = Math.Clamp(planetCount, 1, 12);
        planetOffsetDeg = WrapOffsetDeg(planetOffsetDeg, PlanetOffsetPeriodDeg(planetCount));
        idlerOffsetDeg = ClampIdlerOffsetDeg(idlerOffsetDeg);

        var idlerPitch = Math.Max(0.5, layout.IdlerPitch);
        if (!TryMeasureIdlers(layout, planetCount, planetOffsetDeg, idlerOffsetDeg,
                out var dMean, out var dSpread, out var failMeasure))
        {
            return EmptyInnerFit(dMean, failMeasure);
        }

        // Match compound↔idler tip clearance: pitch circles sit meshExtra inside dMean.
        var pitchBudget = Math.Max(idlerPitch * 1.5, dMean - IdlerMeshExtra(layout));
        var nExact = layout.IdlerTeeth * (pitchBudget / idlerPitch - 1.0);
        var teeth = lockSunTeeth is int locked && locked > 0
            ? Math.Clamp(locked, MinSunTeeth, MaxSunTeeth)
            : (int)Math.Clamp(Math.Round(nExact), MinSunTeeth, MaxSunTeeth);
        var sunPitch = idlerPitch * teeth / (double)layout.IdlerTeeth;
        var sun = GearCutter.External(sunPitch, teeth);
        var pitchError = dMean - (sunPitch + idlerPitch) - IdlerMeshExtra(layout);
        var stageRatio = (layout.RingTeeth / (double)layout.PlanetTeeth)
            * (layout.CompoundTeeth / (double)teeth);

        var ok = ValidateAssembly(
            layout, planetCount, planetOffsetDeg, idlerOffsetDeg, teeth, out var fail);
        if (dSpread > layout.IdlerPitch * 0.04)
        {
            ok = false;
            fail = $"idler radius spread {dSpread:0.00}";
        }

        return new InnerRingFit(
            teeth, sunPitch, sun.TipR, sun.RootR, dMean, sun.Module, pitchError, stageRatio, ok, fail);
    }

    static InnerRingFit EmptyInnerFit(double d, string reason) =>
        new(8, d * 0.5, d * 0.5, d * 0.4, d, 1, 0, 0, false, reason);

    /// <summary>
    /// True when every idler tip/valley-meshes its compound and the shared sun.
    /// </summary>
    public static bool ValidateAssembly(
        FaceMeshLayout layout,
        int planetCount,
        double planetOffsetDeg,
        double idlerOffsetDeg,
        int sunTeeth,
        out string failReason)
    {
        failReason = "";
        planetCount = Math.Clamp(planetCount, 1, 12);
        sunTeeth = Math.Clamp(sunTeeth, MinSunTeeth, MaxSunTeeth);
        idlerOffsetDeg = ClampIdlerOffsetDeg(idlerOffsetDeg);
        planetOffsetDeg = WrapOffsetDeg(planetOffsetDeg, PlanetOffsetPeriodDeg(planetCount));

        if (!TryBuildStations(layout, planetCount, planetOffsetDeg, idlerOffsetDeg,
                out var stations, out failReason))
            return false;

        if (layout.PlanetTipR + 1e-6 < MinVisibleGearTipR)
        {
            failReason = $"planet too small ({layout.PlanetTipR:0.0} < {MinVisibleGearTipR})";
            return false;
        }
        if (layout.IdlerTipR + 1e-6 < MinVisibleGearTipR)
        {
            failReason = $"idler too small ({layout.IdlerTipR:0.0} < {MinVisibleGearTipR})";
            return false;
        }
        if (layout.CompoundTipR + 1e-6 < MinVisibleGearTipR)
        {
            failReason = $"compound too small ({layout.CompoundTipR:0.0} < {MinVisibleGearTipR})";
            return false;
        }

        var ip = layout.IdlerPitch;
        var sunPitch = ip * sunTeeth / (double)layout.IdlerTeeth;
        var sunTipR = GearCutter.External(sunPitch, sunTeeth).TipR;
        if (sunTipR + 1e-6 < MinVisibleGearTipR)
        {
            failReason = $"sun too small ({sunTipR:0.0} < {MinVisibleGearTipR})";
            return false;
        }
        // Idler lives in the annulus between host tips and sun tips. If that
        // gap is narrower than the idler, the disk occupies both rims (jam).
        var ringGap = layout.TipR - sunTipR;
        var minRingGap = 2.0 * layout.IdlerTipR;
        if (ringGap + 1e-6 < minRingGap)
        {
            failReason = $"ring gap {ringGap:0.0} < idler ⌀ {minRingGap:0.0}";
            return false;
        }

        var d0 = stations[0].Dist;
        var pitchErr = Math.Abs(d0 - (sunPitch + ip) - IdlerMeshExtra(layout));
        if (pitchErr > ip * 0.06)
        {
            failReason = $"pitch err {pitchErr:0.00}";
            return false;
        }

        foreach (var s in stations)
        {
            if (Math.Abs(s.Dist - d0) > ip * 0.04)
            {
                failReason = $"idler r mismatch ({s.Dist:0.0} vs {d0:0.0})";
                return false;
            }
        }

        // Phase sun from station 0 (host = idler → sun).
        var idlerToSun0 = stations[0].AngleFromSun + Math.PI;
        var sunAlign = IdlerAlignAngle(
            idlerToSun0, stations[0].IdlerAlign, layout.MeshIdlerTeeth, sunTeeth);

        const double phaseTol = 0.12; // fraction of a tooth
        for (var i = 0; i < stations.Length; i++)
        {
            var st = stations[i];
            // Compound ↔ idler
            if (!ExternalMeshOk(
                    st.SeatFromCompound, st.CompoundAlign, layout.MeshCompoundTeeth,
                    st.SeatFromCompound + Math.PI, st.IdlerAlign, layout.MeshIdlerTeeth,
                    phaseTol))
            {
                failReason = $"planet {i}: compound↔idler phase";
                return false;
            }

            // Sun ↔ idler (sun toward idler = AngleFromSun; idler toward sun = +π)
            if (!ExternalMeshOk(
                    st.AngleFromSun, sunAlign, sunTeeth,
                    st.AngleFromSun + Math.PI, st.IdlerAlign, layout.MeshIdlerTeeth,
                    phaseTol))
            {
                failReason = $"planet {i}: sun↔idler phase";
                return false;
            }
        }

        if (planetCount >= 2
            && !CompoundBodiesClear(
                layout, planetCount, planetOffsetDeg,
                layout, planetCount, planetOffsetDeg,
                sameStage: true, out failReason,
                visibleRMin: sunTipR, visibleRMax: layout.RingOuter))
            return false;

        // Same-type idlers: every pair, not just neighbors.
        if (stations.Length >= 2)
        {
            var minClear = 2.0 * layout.IdlerTipR + layout.Module * 0.15;
            for (var i = 0; i < stations.Length; i++)
            for (var j = i + 1; j < stations.Length; j++)
            {
                var a = stations[i];
                var b = stations[j];
                var ax = a.Dist * Math.Cos(a.AngleFromSun);
                var ay = a.Dist * Math.Sin(a.AngleFromSun);
                var bx = b.Dist * Math.Cos(b.AngleFromSun);
                var by = b.Dist * Math.Sin(b.AngleFromSun);
                var gap = Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
                if (gap + 1e-6 < minClear)
                {
                    failReason = $"idler clash {i}↔{j} ({gap:0.0} < {minClear:0.0})";
                    return false;
                }
            }
        }

        failReason = "ok";
        return true;
    }

    /// <summary>
    /// Same-ring compound/planet disks. Other rings are ignored — masked lobes
    /// inside the inner sun / outside the host rim do not count.
    /// </summary>
    public static bool CompoundBodiesClear(
        FaceMeshLayout layoutA, int countA, double offsetA,
        FaceMeshLayout layoutB, int countB, double offsetB,
        bool sameStage, out string failReason,
        double visibleRMin = 0, double visibleRMax = 0)
    {
        failReason = "";
        if (!sameStage)
        {
            failReason = "ok";
            return true;
        }
        countA = Math.Clamp(countA, 1, 12);
        countB = Math.Clamp(countB, 1, 12);
        var rA = Math.Max(layoutA.CompoundTipR, layoutA.PlanetTipR);
        var rB = Math.Max(layoutB.CompoundTipR, layoutB.PlanetTipR);
        var pad = Math.Max(layoutA.Module, layoutB.Module) * 0.15;
        var rMin = Math.Max(0, visibleRMin);
        var rMax = visibleRMax > 1e-6 ? visibleRMax : Math.Max(layoutA.RingOuter, layoutB.RingOuter);
        for (var i = 0; i < countA; i++)
        {
            var ai = PlanetCarrierAngle(i, countA, layoutA.RingTeeth, offsetA);
            var ax = layoutA.CarrierRadius * Math.Cos(ai);
            var ay = layoutA.CarrierRadius * Math.Sin(ai);
            for (var j = i + 1; j < countB; j++)
            {
                var bj = PlanetCarrierAngle(j, countB, layoutB.RingTeeth, offsetB);
                var bx = layoutB.CarrierRadius * Math.Cos(bj);
                var by = layoutB.CarrierRadius * Math.Sin(bj);
                if (!DisksOverlapInAnnulus(
                        ax, ay, rA + pad * 0.5, bx, by, rB + pad * 0.5, rMin, rMax))
                    continue;
                var gap = Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
                failReason = $"compound clash {i}↔{j} ({gap:0.0})";
                return false;
            }
        }

        failReason = "ok";
        return true;
    }

    /// <summary>
    /// True when two disks overlap in the visible annulus. Overlap that lives
    /// only in the masked hole (r &lt; rMin) or past the rim does not count.
    /// </summary>
    static bool DisksOverlapInAnnulus(
        double ax, double ay, double rA,
        double bx, double by, double rB,
        double rMin, double rMax)
    {
        var dx = ax - bx;
        var dy = ay - by;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist + 1e-6 >= rA + rB)
            return false;
        if (!TryIntersectionRadii(ax, ay, rA, bx, by, rB, out var minR, out var maxR))
            return false;
        return minR <= rMax + 1e-6 && maxR >= rMin - 1e-6;
    }

    static bool TryIntersectionRadii(
        double ax, double ay, double rA,
        double bx, double by, double rB,
        out double minR, out double maxR)
    {
        var lo = double.MaxValue;
        var hi = 0.0;
        var any = false;
        void Consider(double x, double y, double cx, double cy, double rb)
        {
            var ddx = x - cx;
            var ddy = y - cy;
            if (ddx * ddx + ddy * ddy > rb * rb + 1e-6)
                return;
            var r = Math.Sqrt(x * x + y * y);
            lo = Math.Min(lo, r);
            hi = Math.Max(hi, r);
            any = true;
        }

        void Extremes(double cx, double cy, double rr, double ox, double oy, double orr)
        {
            var cr = Math.Sqrt(cx * cx + cy * cy);
            if (cr < 1e-9)
            {
                Consider(rr, 0, ox, oy, orr);
                Consider(0, 0, ox, oy, orr);
                return;
            }
            var ux = cx / cr;
            var uy = cy / cr;
            Consider(cx + rr * ux, cy + rr * uy, ox, oy, orr);
            Consider(cx - rr * ux, cy - rr * uy, ox, oy, orr);
        }

        Extremes(ax, ay, rA, bx, by, rB);
        Extremes(bx, by, rB, ax, ay, rA);
        if (ax * ax + ay * ay <= rA * rA && bx * bx + by * by <= rB * rB)
        {
            lo = 0;
            any = true;
        }

        var d = Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
        if (d > 1e-9 && d < rA + rB && d > Math.Abs(rA - rB))
        {
            var a = (rA * rA - rB * rB + d * d) / (2.0 * d);
            var h2 = rA * rA - a * a;
            if (h2 >= -1e-6)
            {
                var h = Math.Sqrt(Math.Max(0, h2));
                var ux = (bx - ax) / d;
                var uy = (by - ay) / d;
                var px = ax + a * ux;
                var py = ay + a * uy;
                Consider(px + h * -uy, py + h * ux, bx, by, rB);
                Consider(px - h * -uy, py - h * ux, bx, by, rB);
            }
        }

        minR = any ? lo : 0;
        maxR = any ? hi : 0;
        return any;
    }

    static bool ExternalMeshOk(
        double dirA, double alignA, int teethA,
        double dirB, double alignB, int teethB,
        double tol)
    {
        var fa = ToothFrac(dirA, alignA, teethA);
        var fb = ToothFrac(dirB, alignB, teethB);
        var aTip = IsNearTip(fa, tol);
        var aValley = IsNearValley(fa, tol);
        var bTip = IsNearTip(fb, tol);
        var bValley = IsNearValley(fb, tol);
        return (aTip && bValley) || (aValley && bTip);
    }

    /// <summary>0 = tip center, 0.5 = valley center within one tooth.</summary>
    static double ToothFrac(double worldDir, double tooth0Align, int teeth)
    {
        teeth = Math.Max(1, teeth);
        var pitch = Math.PI * 2.0 / teeth;
        var x = worldDir - tooth0Align;
        x %= pitch;
        if (x < 0)
            x += pitch;
        return x / pitch;
    }

    static bool IsNearTip(double frac, double tol) =>
        Math.Min(frac, 1.0 - frac) <= tol;

    static bool IsNearValley(double frac, double tol) =>
        Math.Abs(frac - 0.5) <= tol;

    readonly struct IdlerStation
    {
        public double Dist { get; init; }
        public double AngleFromSun { get; init; }
        public double SeatFromCompound { get; init; }
        public double CompoundAlign { get; init; }
        public double IdlerAlign { get; init; }
    }

    static bool TryMeasureIdlers(
        FaceMeshLayout layout, int planetCount, double planetOffsetDeg, double idlerOffsetDeg,
        out double dMean, out double dSpread, out string failReason)
    {
        dMean = 0;
        dSpread = 0;
        if (!TryBuildStations(layout, planetCount, planetOffsetDeg, idlerOffsetDeg,
                out var stations, out failReason))
            return false;
        var min = double.MaxValue;
        var max = 0.0;
        var sum = 0.0;
        foreach (var s in stations)
        {
            sum += s.Dist;
            min = Math.Min(min, s.Dist);
            max = Math.Max(max, s.Dist);
        }
        dMean = sum / stations.Length;
        dSpread = max - min;
        failReason = "";
        return true;
    }

    static bool TryBuildStations(
        FaceMeshLayout layout, int planetCount, double planetOffsetDeg, double idlerOffsetDeg,
        out IdlerStation[] stations, out string failReason)
    {
        failReason = "";
        stations = new IdlerStation[planetCount];
        var mesh = layout.IdlerMeshRadius;
        var carrier = layout.CarrierRadius;
        for (var i = 0; i < planetCount; i++)
        {
            var ang = PlanetCarrierAngle(i, planetCount, layout.RingTeeth, planetOffsetDeg);
            var compoundAlign = PlanetAlignAngle(ang, layout.PlanetTeeth, layout.RingTeeth);
            var seat = IdlerSeatAngle(ang, 0, 1, idlerOffsetDeg);
            var idlerAlign = IdlerAlignAngle(seat, compoundAlign, layout.MeshCompoundTeeth, layout.MeshIdlerTeeth);
            var px = carrier * Math.Cos(ang);
            var py = carrier * Math.Sin(ang);
            var ix = px + mesh * Math.Cos(seat);
            var iy = py + mesh * Math.Sin(seat);
            var dist = Math.Sqrt(ix * ix + iy * iy);
            if (dist < 1e-6)
            {
                failReason = $"planet {i}: idler at origin";
                return false;
            }
            stations[i] = new IdlerStation
            {
                Dist = dist,
                AngleFromSun = Math.Atan2(iy, ix),
                SeatFromCompound = seat,
                CompoundAlign = compoundAlign,
                IdlerAlign = idlerAlign
            };
        }
        return true;
    }

    public readonly record struct InnerRingBand(
        double OuterTip,
        double OuterRoot,
        double InnerRoot,
        double InnerTip)
    {
        /// <summary>Radial middle of the solid rim (exterior teeth sit outside this).</summary>
        public double SolidMidR => (OuterRoot + InnerRoot) * 0.5;
    }

    /// <summary>Shared radii for <see cref="BuildInnerRing"/> and mid-gear hollow masking.</summary>
    public static InnerRingBand ResolveInnerRingBand(InnerRingFit inner)
    {
        var teeth = Math.Clamp(inner.Teeth, MinSunTeeth, MaxSunTeeth);
        var ext = GearCutter.External(inner.Pitch, teeth);
        var rim = Math.Max(ext.Module * 1.6, ext.WholeDepth);
        var k = 1.0 + 2.0 * GearCutter.DedendumModules / teeth;
        var innerPitch = Math.Max(4, (ext.RootR - rim) / k);
        if (innerPitch < 4)
            innerPitch = Math.Max(4, ext.RootR * 0.45);
        var inn = GearCutter.Internal(innerPitch, teeth);
        if (inn.RootR >= ext.RootR - 0.4)
        {
            innerPitch *= 0.86;
            inn = GearCutter.Internal(innerPitch, teeth);
        }

        return new InnerRingBand(ext.TipR, ext.RootR, inn.RootR, inn.TipR);
    }

    /// <summary>
    /// Hollow first-stage ring: exterior teeth mesh the idlers; interior teeth use the
    /// same count (half-pitch offset so the rim stays even). Center is open.
    /// </summary>
    public static Geometry BuildInnerRing(
        double cx, double cy, FaceMeshLayout layout, InnerRingFit inner)
    {
        _ = layout;
        var teeth = Math.Clamp(inner.Teeth, MinSunTeeth, MaxSunTeeth);
        var band = ResolveInnerRingBand(inner);
        var innerPitch = GearCutter.InternalPitchFromBand(band.InnerTip, band.InnerRoot, teeth);
        return GearCutter.BuildHollowRing(
            cx, cy, inner.Pitch, innerPitch, teeth, Math.PI / teeth);
    }

    /// <summary>
    /// Exterior tooth crown only (outside the solid rim). Drawn under mid gears so tips
    /// mesh without cutouts; pair with <see cref="BuildInnerRingMaskCore"/>.
    /// </summary>
    public static Geometry BuildInnerRingExteriorTeeth(
        double cx, double cy, FaceMeshLayout layout, InnerRingFit inner)
    {
        _ = layout;
        var teeth = Math.Clamp(inner.Teeth, MinSunTeeth, MaxSunTeeth);
        return GearCutter.BuildExternalCrowns(cx, cy, inner.Pitch, teeth);
    }

    /// <summary>
    /// Solid rim from exterior root inward (interior teeth + hole). Drawn above mid gears
    /// so only the rim body masks; exterior teeth stay under the idlers.
    /// </summary>
    public static Geometry BuildInnerRingMaskCore(
        double cx, double cy, FaceMeshLayout layout, InnerRingFit inner)
    {
        _ = layout;
        var teeth = Math.Clamp(inner.Teeth, MinSunTeeth, MaxSunTeeth);
        var band = ResolveInnerRingBand(inner);
        var innerPitch = GearCutter.InternalPitchFromBand(band.InnerTip, band.InnerRoot, teeth);
        return GearCutter.BuildHollowMask(
            cx, cy, band.OuterRoot, innerPitch, teeth, Math.PI / teeth);
    }

    public static double PlanetOffsetPeriodDeg(int planetCount) =>
        360.0 / Math.Max(1, planetCount);

    /// <summary>
    /// Clamp offset into [0, period] inclusive. Period is kept (not snapped to 0) so the
    /// slider can rest at maximum; mesh is identical to 0 via N-fold symmetry.
    /// </summary>
    public static double WrapOffsetDeg(double offsetDeg, double periodDeg)
    {
        if (periodDeg <= 0)
            return 0;
        if (double.IsNaN(offsetDeg) || double.IsInfinity(offsetDeg))
            return 0;
        if (offsetDeg < 0)
            return 0;
        if (offsetDeg > periodDeg)
            return periodDeg;
        return offsetDeg;
    }

    public static Geometry BuildInternalRing(
        double cx, double cy, FaceMeshLayout layout, int toothCount,
        double angleOffsetRad = -Math.PI / 2)
    {
        toothCount = Math.Clamp(toothCount, 6, 256);
        return GearCutter.BuildInternalRing(
            cx, cy, layout.RingOuter, layout.RingPitch, toothCount, angleOffsetRad);
    }

    public static Geometry BuildInternalHourToothAccents(
        double cx, double cy, FaceMeshLayout layout, int toothCount, int hourStride,
        double angleOffsetRad = -Math.PI / 2)
    {
        toothCount = Math.Clamp(toothCount, 6, 256);
        hourStride = Math.Clamp(hourStride, 1, toothCount);
        return GearCutter.BuildInternalCrowns(
            cx, cy, layout.RingPitch, toothCount, hourStride, angleOffsetRad);
    }

    public static Geometry BuildPlanetGear(
        double cx, double cy, FaceMeshLayout layout, int toothCount) =>
        GearCutter.BuildExternal(cx, cy, layout.PlanetPitch, toothCount);

    public static Geometry BuildCompoundGear(
        double cx, double cy, FaceMeshLayout layout) =>
        GearCutter.BuildExternal(cx, cy, layout.CompoundPitch, layout.MeshCompoundTeeth);
}
