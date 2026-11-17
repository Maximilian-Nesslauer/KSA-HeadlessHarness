using Brutal.Numerics;
using KSA;

namespace HeadlessHarness.Harness;

// Test orbits built through the game's own Orbit.CreateFromStateCci from a state at periapsis.
// Altitudes are meters above the parent's mean radius, radii are meters from its centre, and angles
// are radians. The plane angles are those Orbit.Inclination, LongitudeOfAscendingNode and
// ArgumentOfPeriapsis report: inclination from the CCI XY plane (the parent's equator, since its
// spin axis is CCI +Z, IParentBody.GetAngularVelocityCci), the ascending node measured from CCI +X.
public static partial class VehicleSpawner
{
    public static Orbit CircularAtAltitude(IParentBody parent, double altitudeM, UniverseTime time)
        => CircularCci(parent, parent.MeanRadius + altitudeM, time);

    public static Orbit EllipticalAtAltitudes(IParentBody parent, double periapsisAltitudeM, double apoapsisAltitudeM, UniverseTime time)
        => EllipticalCci(parent, parent.MeanRadius + periapsisAltitudeM, parent.MeanRadius + apoapsisAltitudeM, time);

    // Starts at periapsis. Equal altitudes give a circular orbit, whose argument of periapsis the
    // game reports as 0 whatever was asked.
    public static Orbit InclinedEllipticalAt(IParentBody parent, double periapsisAltitudeM, double apoapsisAltitudeM,
        double inclinationRad, double longitudeOfAscendingNodeRad, double argumentOfPeriapsisRad, UniverseTime time)
    {
        double rp = parent.MeanRadius + periapsisAltitudeM;
        double ra = parent.MeanRadius + apoapsisAltitudeM;
        if (ra < rp)
            throw new ArgumentException($"apoapsis altitude {apoapsisAltitudeM} m is below periapsis altitude {periapsisAltitudeM} m.");
        double a = (rp + ra) / 2.0;
        double vp = Math.Sqrt(parent.Mu * (2.0 / rp - 1.0 / a));
        PerifocalAxes(inclinationRad, longitudeOfAscendingNodeRad, argumentOfPeriapsisRad, out double3 p, out double3 q);
        return Orbit.CreateFromStateCci(parent, time, p * rp, q * vp, OrbitLineColor);
    }

    // An escape orbit at its periapsis with the given hyperbolic excess speed (m/s), from the
    // vis-viva relation v_p^2 = v_inf^2 + 2 mu / r_p.
    public static Orbit HyperbolicAt(IParentBody parent, double periapsisRadiusM, double vInfinity, UniverseTime time,
        double inclinationRad = 0.0, double longitudeOfAscendingNodeRad = 0.0, double argumentOfPeriapsisRad = 0.0)
    {
        if (vInfinity <= 0.0)
            throw new ArgumentException($"v infinity must be positive for an escape orbit, got {vInfinity} m/s.");
        double vp = Math.Sqrt(vInfinity * vInfinity + 2.0 * parent.Mu / periapsisRadiusM);
        PerifocalAxes(inclinationRad, longitudeOfAscendingNodeRad, argumentOfPeriapsisRad, out double3 p, out double3 q);
        return Orbit.CreateFromStateCci(parent, time, p * periapsisRadiusM, q * vp, OrbitLineColor);
    }

    // The orbit after an instantaneous velocity change dvCci (m/s, CCI frame) at the given time.
    public static Orbit ApplyImpulse(Orbit orbit, UniverseTime time, double3 dvCci)
    {
        StateVectors sv = orbit.GetStateVectorsAt(time);
        return Orbit.CreateFromStateCci(orbit.Parent, time, sv.PositionCci, sv.VelocityCci + dvCci, OrbitLineColor);
    }

    // P points at periapsis and Q along the velocity there, in CCI. These are the first two rows
    // of Orbit.CreateOrb2ParentCci, the orbit-to-parent rotation the game builds from the same angles.
    private static void PerifocalAxes(double inclination, double lan, double argPe, out double3 p, out double3 q)
    {
        double cO = Math.Cos(lan), sO = Math.Sin(lan);
        double ci = Math.Cos(inclination), si = Math.Sin(inclination);
        double cw = Math.Cos(argPe), sw = Math.Sin(argPe);
        p = new double3(cO * cw - sO * sw * ci, sO * cw + cO * sw * ci, si * sw);
        q = new double3(-cO * sw - sO * cw * ci, -sO * sw + cO * cw * ci, si * cw);
    }
}
