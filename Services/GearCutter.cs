using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace AtFutureMe.Services;

/// <summary>
/// ISO 53 involute cutter used by every clock gear. 20° full depth:
/// module m = 2 r_pitch / z, addendum 1.0 m, dedendum 1.25 m. Tooth and
/// valley each take half a circular pitch at the pitch circle, so two
/// gears of equal m mesh when pitch circles are tangent (external) or
/// differ by r_ring − r_planet (internal).
/// </summary>
// auto model
public static class GearCutter
{
    public const double PressureDeg = 20;
    public const double AddendumModules = 1.0;
    public const double DedendumModules = 1.25;
    const int FlankSamplesMax = 8;

    static int FlankCount(int teeth) =>
        teeth >= 96 ? 3 : teeth >= 36 ? 4 : teeth >= 18 ? 6 : FlankSamplesMax;

    public readonly record struct Cut(
        int Teeth,
        double PitchR,
        double TipR,
        double RootR,
        double BaseR,
        double Module,
        bool Internal)
    {
        public double WholeDepth => Math.Abs(TipR - RootR);
        public double PitchStep => Math.PI * 2.0 / Math.Max(1, Teeth);
        public double PitchHalf => Math.PI / (2.0 * Math.Max(1, Teeth));
    }

    public static double IsoModule(double pitchR, int teeth) =>
        2.0 * Math.Max(2, pitchR) / Math.Max(4, teeth);

    public static Cut External(double pitchR, int teeth)
    {
        teeth = Math.Clamp(teeth, 4, 256);
        pitchR = Math.Max(2, pitchR);
        var m = IsoModule(pitchR, teeth);
        var alpha = PressureDeg * Math.PI / 180.0;
        var tipR = pitchR + m * AddendumModules;
        var rootR = Math.Max(m * 0.4, pitchR - m * DedendumModules);
        return new Cut(teeth, pitchR, tipR, rootR, pitchR * Math.Cos(alpha), m, false);
    }

    public static Cut Internal(double pitchR, int teeth)
    {
        teeth = Math.Clamp(teeth, 6, 256);
        pitchR = Math.Max(2, pitchR);
        var m = IsoModule(pitchR, teeth);
        var alpha = PressureDeg * Math.PI / 180.0;
        var tipR = Math.Max(2, pitchR - m * AddendumModules);
        var rootR = pitchR + m * DedendumModules;
        return new Cut(teeth, pitchR, tipR, rootR, pitchR * Math.Cos(alpha), m, true);
    }

    /// <summary>Internal ring whose root sits just inside <paramref name="outerR"/>.</summary>
    public static Cut InternalAgainstRim(double outerR, int teeth, double rimInset = 0.55)
    {
        teeth = Math.Clamp(teeth, 6, 256);
        outerR = Math.Max(8, outerR);
        var k = 1.0 + 2.0 * DedendumModules / teeth;
        var pitchR = Math.Max(4, (outerR - Math.Max(0.35, rimInset)) / k);
        return Internal(pitchR, teeth);
    }

    public static double InternalPitchFromBand(double innerTip, double innerRoot, int teeth)
    {
        teeth = Math.Max(6, teeth);
        var k = 1.0 + 2.0 * DedendumModules / teeth;
        return Math.Max(4, innerRoot / k);
    }

    public static Geometry BuildExternal(
        double cx, double cy, double pitchR, int teeth, double angle0 = 0)
    {
        var cut = External(pitchR, teeth);
        var outline = Stream(ctx => WriteToothLoop(ctx, cx, cy, cut, angle0), FillRule.Nonzero);
        var hubR = Math.Max(0.8, cut.RootR);
        var hub = new EllipseGeometry(new Point(cx, cy), hubR, hubR);
        var geo = new CombinedGeometry(GeometryCombineMode.Union, hub, outline);
        geo.Freeze();
        return geo;
    }

    public static Geometry BuildInternalRing(
        double cx, double cy, double outerR, double pitchR, int teeth, double angle0)
    {
        var cut = Internal(pitchR, teeth);
        outerR = Math.Max(outerR, cut.RootR + 0.35);
        return Stream(ctx =>
        {
            WriteCircle(ctx, cx, cy, outerR, clockwise: true);
            WriteToothLoop(ctx, cx, cy, cut, angle0);
        });
    }

    /// <summary>Individual internal teeth (hour accents) using the same cutter.</summary>
    public static Geometry BuildInternalCrowns(
        double cx, double cy, double pitchR, int teeth, int stride, double angle0)
    {
        var cut = Internal(pitchR, teeth);
        stride = Math.Clamp(stride, 1, cut.Teeth);
        return Stream(ctx =>
        {
            for (var i = 0; i < cut.Teeth; i += stride)
                WriteCrown(ctx, cx, cy, cut, angle0 + i * cut.PitchStep);
        }, FillRule.Nonzero);
    }

    /// <summary>
    /// Hollow-ring outer crown as one even-odd outline: full external disc
    /// minus a hole slightly inside the root, so tooth bases overlap the rim
    /// mask and there is no hairline join.
    /// </summary>
    public static Geometry BuildExternalCrowns(
        double cx, double cy, double pitchR, int teeth, double angle0 = 0)
    {
        var cut = External(pitchR, teeth);
        var holeR = Math.Max(2, cut.RootR - Math.Max(0.7, cut.Module * 0.35));
        return Stream(ctx =>
        {
            WriteToothLoop(ctx, cx, cy, cut, angle0);
            WriteCircle(ctx, cx, cy, holeR, clockwise: false);
        });
    }

    public static Geometry BuildHollowMask(
        double cx, double cy, double outerRoot, double innerPitch, int teeth, double innerAngle0)
    {
        var inn = Internal(innerPitch, teeth);
        // Overlap the exterior crown bases so AA cannot open a seam.
        outerRoot = Math.Max(outerRoot, inn.RootR + 0.2)
            + Math.Max(0.4, inn.Module * 0.18);
        return Stream(ctx =>
        {
            WriteCircle(ctx, cx, cy, outerRoot, clockwise: true);
            WriteToothLoop(ctx, cx, cy, inn, innerAngle0);
        });
    }

    public static Geometry BuildHollowRing(
        double cx, double cy, double outerPitch, double innerPitch, int teeth, double innerAngle0)
    {
        var ext = External(outerPitch, teeth);
        var inn = Internal(innerPitch, teeth);
        return Stream(ctx =>
        {
            WriteToothLoop(ctx, cx, cy, ext, 0);
            WriteToothLoop(ctx, cx, cy, inn, innerAngle0);
        });
    }

    static StreamGeometry Stream(Action<StreamGeometryContext> write, FillRule rule = FillRule.EvenOdd)
    {
        var geo = new StreamGeometry { FillRule = rule };
        using (var ctx = geo.Open())
            write(ctx);
        geo.Freeze();
        return geo;
    }

    static void WriteToothLoop(StreamGeometryContext ctx, double cx, double cy, Cut cut, double angle0)
    {
        Point? start = null;
        for (var i = 0; i < cut.Teeth; i++)
        {
            var a0 = angle0 + i * cut.PitchStep;
            var left = Flank(cx, cy, cut, a0, left: true, tipward: true);
            var right = Flank(cx, cy, cut, a0, left: false, tipward: false);
            if (start == null)
            {
                ctx.BeginFigure(left[0], true, true);
                start = left[0];
            }
            WriteLines(ctx, left, skipFirst: true);
            WriteMinorArc(ctx, cx, cy, cut.TipR, left[^1], right[0]);
            WriteLines(ctx, right, skipFirst: true);
            var nextStart = Flank(cx, cy, cut, a0 + cut.PitchStep, left: true, tipward: true)[0];
            WriteMinorArc(ctx, cx, cy, cut.RootR, right[^1], nextStart);
        }
    }

    static void WriteCrown(StreamGeometryContext ctx, double cx, double cy, Cut cut, double a0)
    {
        var left = Flank(cx, cy, cut, a0, left: true, tipward: true);
        var right = Flank(cx, cy, cut, a0, left: false, tipward: false);
        ctx.BeginFigure(left[0], true, true);
        WriteLines(ctx, left, skipFirst: true);
        WriteMinorArc(ctx, cx, cy, cut.TipR, left[^1], right[0]);
        WriteLines(ctx, right, skipFirst: true);
    }

    /// <summary>
    /// Involute flank from root to tip (tipward) or tip to root. Low-z pinions
    /// undercut below the base circle with a radial runout to the root.
    /// </summary>
    static Point[] Flank(double cx, double cy, Cut cut, double a0, bool left, bool tipward)
    {
        var rb = Math.Max(0.4, cut.BaseR);
        var rp = Math.Max(rb + 1e-4, cut.PitchR);
        var phiP = InvolutePhi(rp, rb);
        // External: φ grows with r, subtract so the tooth narrows toward the tip.
        // Internal: r shrinks toward the tip, opposite sign.
        var sign = cut.Internal ? 1.0 : -1.0;
        var side = left ? -1.0 : 1.0;
        double Theta(double r)
        {
            var rr = Math.Max(r, rb);
            return a0 + side * cut.PitchHalf + side * sign * (InvolutePhi(rr, rb) - phiP);
        }

        var n = FlankCount(cut.Teeth);
        var pts = new List<Point>(n + 3);
        void AddR(double r) => pts.Add(Polar(cx, cy, r, Theta(r)));

        if (cut.Internal)
        {
            SampleRadii(cut.RootR, Math.Max(cut.TipR, rb), n, AddR);
            if (cut.TipR + 1e-6 < rb)
                pts.Add(Polar(cx, cy, cut.TipR, Theta(rb)));
        }
        else
        {
            if (cut.RootR + 1e-6 < rb)
                pts.Add(Polar(cx, cy, cut.RootR, Theta(rb)));
            SampleRadii(Math.Max(cut.RootR, rb), cut.TipR, n, AddR);
        }

        if (pts.Count < 2)
        {
            AddR(cut.RootR);
            AddR(cut.TipR);
        }

        if (!tipward)
            pts.Reverse();
        return pts.ToArray();
    }

    static void SampleRadii(double from, double to, int samples, Action<double> add)
    {
        samples = Math.Max(2, samples);
        if (Math.Abs(to - from) < 1e-6)
        {
            add(to);
            return;
        }
        for (var s = 0; s <= samples; s++)
        {
            var t = s / (double)samples;
            t = t * t * (3 - 2 * t);
            add(from + (to - from) * t);
        }
    }

    static double InvolutePhi(double r, double rb)
    {
        if (r <= rb + 1e-9)
            return 0;
        var c = Math.Clamp(rb / r, 0, 1);
        var t = Math.Sqrt(Math.Max(0, (r / rb) * (r / rb) - 1));
        return t - Math.Acos(c);
    }

    static void WriteLines(StreamGeometryContext ctx, IReadOnlyList<Point> pts, bool skipFirst)
    {
        for (var i = skipFirst ? 1 : 0; i < pts.Count; i++)
            ctx.LineTo(pts[i], true, false);
    }

    static double AngleOf(double cx, double cy, Point p) => Math.Atan2(p.Y - cy, p.X - cx);

    static void WriteMinorArc(
        StreamGeometryContext ctx, double cx, double cy, double r, Point from, Point to)
    {
        var a0 = AngleOf(cx, cy, from);
        var a1 = AngleOf(cx, cy, to);
        var ccw = WrapTau(a1 - a0);
        var clockwise = ccw > Math.PI;
        var delta = clockwise ? ccw - Math.PI * 2 : ccw;
        ctx.ArcTo(
            to,
            new Size(Math.Max(0.5, r), Math.Max(0.5, r)),
            0,
            Math.Abs(delta) > Math.PI + 1e-6,
            clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
            true, false);
    }

    static void WriteCircle(StreamGeometryContext ctx, double cx, double cy, double r, bool clockwise)
    {
        r = Math.Max(0.5, r);
        var dir = clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise;
        ctx.BeginFigure(Polar(cx, cy, r, 0), true, true);
        ctx.ArcTo(Polar(cx, cy, r, Math.PI), new Size(r, r), 0, false, dir, true, false);
        ctx.ArcTo(Polar(cx, cy, r, 0), new Size(r, r), 0, false, dir, true, false);
    }

    static double WrapTau(double a)
    {
        var tau = Math.PI * 2;
        a %= tau;
        if (a < 0) a += tau;
        return a;
    }

    static Point Polar(double cx, double cy, double r, double radians) =>
        new(cx + r * Math.Cos(radians), cy + r * Math.Sin(radians));
}
