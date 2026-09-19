using AtFutureMe.Services;

var layout = ClockGearGeometry.LayoutFaceMesh(140, 30);
ClockGearGeometry.TryFirstValidSeat(layout, 1, 0, null, out var off0, out var ns);
var maxK = ClockGearGeometry.MaxCompoundMul(layout, ns, 1, 0);
Console.WriteLine($"sun={ns}T maxK={maxK} stock Nc={layout.CompoundTeeth} Ni={layout.IdlerTeeth} mesh={layout.IdlerMeshRadius:0.0}");
for (var k = 1; k <= maxK; k++)
{
    var grown = ClockGearGeometry.ApplyCompoundMul(layout, ns, 1, 0, off0, k, out var off);
    var cMod = 2 * Math.PI * grown.CompoundPitch / grown.MeshCompoundTeeth;
    var iMod = 2 * Math.PI * grown.IdlerPitch / grown.MeshIdlerTeeth;
    Console.WriteLine(
        $"k={k} draw {grown.MeshCompoundTeeth}/{grown.MeshIdlerTeeth} kine {grown.CompoundTeeth}/{grown.IdlerTeeth} " +
        $"cTip={grown.CompoundTipR:0.0} iTip={grown.IdlerTipR:0.0} mesh={grown.IdlerMeshRadius:0.0} off={off:0.0} " +
        $"mod {cMod:0.00}/{iMod:0.00}");
}
