using System.Numerics;
using BepuUtilities.Memory;
using Brutal.Numerics;
using KSA;
using KSA.Aero;

namespace HeadlessHarness.Harness;

// Atmosphere at one altitude, rounded to float the way PhysicsEnvironment.RecomputePositionalValues
// stores it for the solver, so a probe sees the same numbers as the flight. SI units: Pa, K, kg/m^3,
// m/s. Above the atmosphere (PhysicalAtmosphere.Height) or without one every value is 0.
public readonly record struct AeroAtmosphere(
    double Altitude,
    double Pressure,
    double Temperature,
    double Density,
    double SpeedOfSound,
    double Gamma)
{
    public bool HasAtmosphere => Density > 0.0 && SpeedOfSound > 0.0;
}

// Aerodynamic loads on a vehicle at one flow state.
//   ForceBody: force in N in the vehicle body frame (the assembly axes), MomentBody: moment in N m
//   about the center of mass in the same frame. Both are the raw surface loads, without the drag
//   saturation and the torque limiter that PhysicsStates.ComputeAerodynamicLoads applies per solver
//   step, and without buoyancy.
//   FlowDirectionBody: the unit direction the air moves relative to the vehicle, body frame.
//   Drag, Lift, SideForce: the force in N along wind axes built on the helm frame (Vehicle.Helm2Body,
//   X forward, Y right, Z down): drag along the flow, lift opposite helm +Z projected normal to the
//   flow, side force along the remaining axis toward helm +Y.
//   MomentStiffnessBound, MomentDampingBound: the bounds SurfaceLoads reports for the solver's
//   torque limiter, per unit dynamic pressure.
public readonly record struct AeroLoads(
    AeroAtmosphere Atmosphere,
    double Speed,
    double Mach,
    double DynamicPressure,
    double3 FlowDirectionBody,
    double3 ForceBody,
    double3 MomentBody,
    double Drag,
    double Lift,
    double SideForce,
    float MomentStiffnessBound,
    float MomentDampingBound,
    float PanelWidth,
    int FaceCount);

// Measures the aerodynamic loads of a vehicle at a chosen flow state without touching the vehicle.
// It runs the game's own load model, LocalInclinationLaw.ComputeLoads with a SkinFrictionLaw, on the
// vehicle's published aerodynamic mesh (PartTree.AwaitAerodynamics), exactly as
// PhysicsStates.ComputeAerodynamicLoads does, but on a private AeroSurfaceFlow in a private
// BufferPool that is freed before the call returns.
//
// Calling PhysicsStates.ComputeDerivatives with vehicle.Parts is not a side-effect-free way to get
// these loads: it takes the live flow through Aerodynamics.GetSurfaceFlow and calls
// AeroSurfaceFlow.Update on it. That flow caches its last direction and skips the running length and
// shielding recompute while the next direction is within one degree of it, so a probe at another
// angle would leave its solution in the cache and change the forces the solver applies in later
// steps. GetSurfaceFlow also creates the live flow if the tree has none yet.
//
// Call only between SimDriver steps. A rebuild of the mesh frees the surface the private flow reads,
// so nothing of the mesh is kept after a call returns. The surface coefficients are float vector
// math whose width depends on the CPU (Vector<float>), so results are bit-stable on one machine only.
public static class AeroProbe
{
    // The vehicle-free atmosphere at altitude (m above the body's mean radius), from
    // PhysicalAtmosphere.ComputeConditionsAtAltitude and the mixture's speed of sound and gamma.
    public static AeroAtmosphere Atmosphere(IParentBody body, double altitude)
    {
        altitude = Math.Max(altitude, 0.0);
        PhysicalAtmosphere? atmosphere = body.GetPhysicalAtmosphere();
        if (atmosphere == null || !(altitude < atmosphere.Height))
            return new AeroAtmosphere(altitude, 0.0, 0.0, 0.0, 0.0, 0.0);
        AtmosphericConditions conditions = atmosphere.ComputeConditionsAtAltitude(altitude);
        float temperature = (float)conditions.Temperature;
        return new AeroAtmosphere(
            altitude,
            (float)conditions.Pressure,
            temperature,
            (float)conditions.Density,
            (float)atmosphere.Mixture.ComputeSpeedOfSound(conditions.Temperature),
            atmosphere.Mixture.ComputeGamma(temperature));
    }

    // Flow state from airspeed (m/s), angle of attack and sideslip (radians) in the helm frame: the
    // vehicle moves through the air along (cos a cos b, sin b, sin a cos b) of Vehicle.Helm2Body, so a
    // positive angle of attack is nose up and a positive sideslip has the air come from the right.
    public static AeroLoads Measure(Vehicle vehicle, IParentBody body, double altitude, double speed, double angleOfAttack, double sideslip)
    {
        (double sinA, double cosA) = Math.SinCos(angleOfAttack);
        (double sinB, double cosB) = Math.SinCos(sideslip);
        double3 airVelocityHelm = speed * new double3(cosA * cosB, sinB, sinA * cosB);
        return Measure(vehicle, body, altitude, airVelocityHelm.Transform(vehicle.Helm2Body), double3.Zero);
    }

    // As Measure, with the airspeed given as a Mach number at that altitude. Without an atmosphere
    // there is no speed of sound, and the loads are zero.
    public static AeroLoads MeasureAtMach(Vehicle vehicle, IParentBody body, double altitude, double mach, double angleOfAttack, double sideslip)
    {
        AeroAtmosphere atmosphere = Atmosphere(body, altitude);
        return Measure(vehicle, body, altitude, mach * atmosphere.SpeedOfSound, angleOfAttack, sideslip);
    }

    // The general form: the velocity of the vehicle relative to the air (m/s) and the vehicle's
    // inertial body rates (rad/s), both in the body frame. The rates add the rotation term the game
    // passes as rotation per airspeed.
    public static AeroLoads Measure(Vehicle vehicle, IParentBody body, double altitude, double3 airVelocityBody, double3 bodyRates)
    {
        PhysicalAtmosphere? physical = body.GetPhysicalAtmosphere();
        AeroAtmosphere atmosphere = Atmosphere(body, altitude);
        AeroMesh mesh = PublishedMesh(vehicle);

        double speed = airVelocityBody.Length();
        double dynamicPressure = 0.5 * atmosphere.Density * speed * speed;
        double3 flowBody = speed > 0.0 ? -airVelocityBody / speed : double3.Zero;
        double mach = atmosphere.SpeedOfSound > 0.0 ? speed / atmosphere.SpeedOfSound : 0.0;

        // PhysicsStates.ComputeAerodynamicLoads returns without a load in this case as well.
        if (physical == null || !atmosphere.HasAtmosphere || dynamicPressure.IsNearlyZero())
            return Result(vehicle, atmosphere, speed, mach, dynamicPressure, flowBody, double3.Zero, double3.Zero, 0f, 0f, mesh);

        BufferPool pool = new BufferPool();
        try
        {
            AeroSurfaceFlow flow = new AeroSurfaceFlow(mesh.Surface, pool);
            Vector3 flowDirection = flowBody.ToBepu();
            flow.Update(flowDirection);
            LocalInclinationLaw law = new LocalInclinationLaw((float)mach, (float)atmosphere.Gamma);
            SkinFrictionLaw friction = new SkinFrictionLaw(physical.Mixture, atmosphere.Temperature, atmosphere.Density, speed, null);
            float3 centerOfMass = vehicle.CenterOfMassAsmbF;
            SurfaceLoads loads = law.ComputeLoads(flow, in friction, flowDirection, (bodyRates / speed).ToBepu(),
                new Vector3(centerOfMass.X, centerOfMass.Y, centerOfMass.Z));
            double3 force = dynamicPressure * Double3Ex.FromBepu(loads.ForcePerDynamicPressure);
            double3 moment = dynamicPressure * Double3Ex.FromBepu(loads.MomentPerDynamicPressure);
            return Result(vehicle, atmosphere, speed, mach, dynamicPressure, flowBody, force, moment,
                loads.MomentStiffnessBound, loads.MomentDampingBound, mesh);
        }
        finally
        {
            pool.Clear();
        }
    }

    // The loads at the vehicle's own state: its altitude above its orbit parent, its velocity relative
    // to the rotating body (Vehicle.GetSurfaceVelocityCci) turned into the body frame, and its body
    // rates. Wind is not modelled by the game, so this is the flow the solver sees.
    public static AeroLoads MeasureCurrent(Vehicle vehicle)
    {
        IParentBody body = vehicle.Orbit.Parent;
        double3 airVelocityBody = vehicle.GetSurfaceVelocityCci().Transform(vehicle.GetBody2Cci().Inverse());
        return Measure(vehicle, body, vehicle.GetBarometricAltitude(), airVelocityBody, vehicle.BodyRates);
    }

    private static AeroMesh PublishedMesh(Vehicle vehicle)
    {
        Aerodynamics? aerodynamics = vehicle.Parts.AwaitAerodynamics();
        AeroMesh? mesh = aerodynamics?.Mesh;
        if (mesh == null || mesh.Surface.FaceCount == 0)
            throw new InvalidOperationException(vehicle.Parts.HasAerodynamicsBuildFailed
                ? $"[HeadlessHarness] the aerodynamic mesh build of '{vehicle.Id}' failed; see the game log."
                : $"[HeadlessHarness] '{vehicle.Id}' has no aerodynamic mesh yet; step the driver once after the spawn.");
        return mesh;
    }

    private static AeroLoads Result(Vehicle vehicle, in AeroAtmosphere atmosphere, double speed, double mach, double dynamicPressure,
        double3 flowBody, double3 force, double3 moment, float stiffness, float damping, AeroMesh mesh)
    {
        double3 liftDirection = double3.Cross(double3.UnitY.Transform(vehicle.Helm2Body), -flowBody).NormalizeOrZero();
        double3 sideDirection = double3.Cross(-flowBody, liftDirection);
        return new AeroLoads(atmosphere, speed, mach, dynamicPressure, flowBody, force, moment,
            double3.Dot(force, flowBody), double3.Dot(force, liftDirection), double3.Dot(force, sideDirection),
            stiffness, damping, mesh.PanelWidth, mesh.Surface.FaceCount);
    }
}
