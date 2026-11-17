using System.Collections.Generic;
using Brutal.Numerics;
using KSA;

namespace HeadlessHarness.Harness;

// Builds a vehicle part tree from part template ids in code, the way the vehicle editor builds one
// (VehicleEditor.SpawnPart and Attach): each part is new Part(id, template) in a tree of its own
// with its attached internals, joined to the vehicle connector to connector with
// Part.Connector.ConnectAndMerge or mounted on a part surface. Build computes the resource groups
// (fuel stages) from the decouplers, as the editor's automatic staging does, and fills the tanks.
// Spawn the result with VehicleSpawner.SpawnTree or SpawnAtLocation.
//
// The part tree frame ("vehicle asmb") is the frame of the root part; +X is the stack axis, up on
// a stock rocket. A connector's own +X points out of its part.
public sealed class VehicleBuilder
{
    // Stock part template ids the canned designs use. Each is checked by the game when the design
    // is built, so a renamed part fails with its id instead of a broken vehicle.
    public static class StockParts
    {
        public const string CommandPod = "CoreCommandA_Prefab_MediumCapsuleVariantA";
        public const string CargoBay = "CoreServiceModuleA_Prefab_SetA2DoubleA";
        public const string Tank3W3H = "CoreFuelTankA_Prefab_LF3W3HA";
        public const string Interstage3W3H = "CoreFairingA_Prefab_Interstage3W3HB";
        public const string VacuumEngine = "CorePropulsionA_Prefab_EngineA5";
        public const string SeaLevelEngine = "CorePropulsionA_Prefab_EngineA2";
        public const string ProbeCoreSmall = "CoreCommandC_Prefab_ProbeCoreSmallA";
        public const string Battery1W = "CoreElectricalA_Prefab_InlineBattery1WBankV1A";
        public const string MonopropTank1W = "CoreFuelTankA_Prefab_MP1W1HA";
        public const string RcsBlock = "CorePropulsionB_Prefab_RCSSmallFourHornV1A";
    }

    private bool _built;

    public VehicleBuilder(string rootPartId)
    {
        Root = CreatePart(rootPartId);
    }

    public Part Root { get; }

    // Every part of the tree, including attached internals, but no sub-parts.
    public int PartCount => Tree.Count;

    private PartTree Tree => Root.Tree ?? throw new InvalidOperationException("the builder's root part has no tree.");

    // Joins a new part to a free connector of a part of this vehicle. The new part is turned and
    // moved so that its connector faces the parent connector (Part.Connector.ConnectAndMerge).
    public Part AddStacked(Part parent, string parentConnectorKey, string partId, string connectorKey)
    {
        EnsureOpen(parent);
        Part.Connector parentConnector = FindConnector(parent, parentConnectorKey);
        if (parentConnector.Connection != null)
            throw new InvalidOperationException($"connector '{parentConnectorKey}' of '{parent.Template.Id}' is already connected.");

        Part child = CreatePart(partId);
        Part.Connector childConnector;
        try
        {
            childConnector = FindConnector(child, connectorKey);
        }
        catch
        {
            child.Tree?.Dispose();
            throw;
        }
        if (!parentConnector.ConnectAndMerge(childConnector, out _))
        {
            child.Tree?.Dispose();
            throw new InvalidOperationException(
                $"could not join '{partId}' ({connectorKey}) to '{parent.Template.Id}' ({parentConnectorKey}).");
        }
        return child;
    }

    // Mounts a new part on the surface of a part of this vehicle, the way the editor attaches a
    // radial part: the part is placed, Part.Connection.Connect joins its connector to the parent's
    // surface, and the parent adds it as a child. positionParentAsmb and normalParentAsmb are in
    // the parent part's frame; the connector sits on the position and faces into the parent
    // against the outward normal. rollRad turns the part about the normal from the orientation in
    // which the connector's +Y lies along the vehicle stack axis +X.
    public Part AddSurfaceMounted(Part parent, string partId, string connectorKey, double3 positionParentAsmb, double3 normalParentAsmb, double rollRad = 0.0)
    {
        EnsureOpen(parent);
        double3 normal = Rotate(normalParentAsmb, parent.Asmb2VehicleAsmb).Normalized();
        double3 target = parent.PositionVehicleAsmb + Rotate(positionParentAsmb, parent.Asmb2VehicleAsmb);

        Part child = CreatePart(partId);
        try
        {
            Part.Connector connector = FindConnector(child, connectorKey);
            if (connector.Connection != null)
                throw new InvalidOperationException($"connector '{connectorKey}' of '{partId}' is already connected.");

            // The same pose change Part.Connector.ConnectAndMerge applies, with the mount frame in
            // place of the parent connector: the child turns by q about its connector and the
            // connector moves onto the mount point. Rz(pi) turns the connector to face the frame.
            doubleQuat frame = MountFrame(normal, rollRad);
            doubleQuat flip = doubleQuat.CreateFromAxisAngle(double3.UnitZ, Math.PI);
            doubleQuat q = connector.Asmb2VehicleAsmb.Inverse().Concatenate(flip).Concatenate(frame);
            double3 connectorPosition = connector.PositionVehicleAsmb;
            MovePartSubtree(child, q, connectorPosition, target);

            if (!Part.Connection.Connect(connector, parent))
                throw new InvalidOperationException($"could not connect '{partId}' ({connectorKey}) to the surface of '{parent.Template.Id}'.");
            if (!parent.AddChild(child))
                throw new InvalidOperationException($"could not add '{partId}' to '{parent.Template.Id}'.");
            Tree.ResetCachedPosMatrixValues();
            return child;
        }
        catch
        {
            if (child.Tree is { IsDisposed: false } own && !ReferenceEquals(own, Tree))
                own.Dispose();
            throw;
        }
    }

    // Moves the part's ISequenced modules (engines, decouplers, parachutes, ...) into the given
    // sequence, as dragging the part in the staging list does. A new part puts them in sequence 1
    // (Part.ResetModuleProperties), so a design names the sequence of every sequenced part.
    public VehicleBuilder Sequence(Part part, int sequence)
    {
        EnsureOpen(part);
        if (sequence < 1)
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "sequences start at 1.");
        bool any = false;
        foreach (ISequenced _ in part.GetSubtreeSequencedModules())
        {
            any = true;
            break;
        }
        if (!any)
            throw new InvalidOperationException($"part '{part.Template.Id}' has no sequenced module.");
        part.SetAllSubtreeModulesSequence(sequence);
        return this;
    }

    // Finishes the tree: ResourceGroupList.CalculateStages numbers the resource groups from the
    // decouplers and, with fillTanks, fills every tank with its automatic mix, the same call the
    // editor makes for a tree it changed (VehicleEditor.FlushDeferredStaging). The builder is spent
    // afterwards. A tree that is never spawned should be disposed with PartTree.Dispose.
    public PartTree Build(bool fillTanks = true)
    {
        if (_built)
            throw new InvalidOperationException("this builder has built its tree already.");
        _built = true;
        PartTree tree = Tree;
        tree.ResourceGroupList.CalculateStages(reconfigureTankContents: fillTanks);
        tree.EnsureDerived(DerivedData.Sequences);
        return tree;
    }

    public static PartTemplate FindPartTemplate(string partId)
    {
        PartTemplate template;
        try
        {
            template = ModLibrary.Get<PartTemplate>(partId);
        }
        catch (NullReferenceException)
        {
            // ModLibrary.Get reports a missing id this way.
            throw new InvalidOperationException($"part '{partId}' is not loaded by any enabled mod.");
        }
        if (template.LoadFailed)
            throw new InvalidOperationException($"part '{partId}' failed to load, so it cannot be built.");
        return template;
    }

    public static Part.Connector FindConnector(Part part, string connectorKey)
    {
        foreach (Part.Connector connector in part.Connectors)
        {
            if (string.Equals(connector.Id, connectorKey, StringComparison.Ordinal))
                return connector;
        }
        List<string> keys = new List<string>(part.Connectors.Length);
        foreach (Part.Connector connector in part.Connectors)
            keys.Add(connector.Id);
        throw new InvalidOperationException(
            $"part '{part.Template.Id}' has no connector '{connectorKey}'. Its connectors: {(keys.Count > 0 ? string.Join(", ", keys) : "none")}.");
    }

    // A crewed two-stage liquid rocket, top to bottom: command pod (crewed, with its attached
    // internal), cargo bay (decoupler under the pod), upper tank with the vacuum engine inside the
    // interstage, the interstage (decoupler), lower tank with the sea-level engine. 8 parts with the
    // pod's internal. Sequence 1 ignites the lower engine, 2 drops the lower stage (interstage, lower
    // tank and engine), 3 ignites the upper engine, 4 drops the pod from the bay.
    public static PartTree TwoStageRocket(bool fillTanks = true)
    {
        VehicleBuilder b = new VehicleBuilder(StockParts.CommandPod);
        Part bay = b.AddStacked(b.Root, "_connector2", StockParts.CargoBay, "_connector3");
        Part upperTank = b.AddStacked(bay, "_connector4", StockParts.Tank3W3H, "_connector25");
        Part vacuumEngine = b.AddStacked(upperTank, "_connector39", StockParts.VacuumEngine, "_connector5");
        Part interstage = b.AddStacked(upperTank, "_connector26", StockParts.Interstage3W3H, "_connector49");
        Part lowerTank = b.AddStacked(interstage, "_connector48", StockParts.Tank3W3H, "_connector25");
        Part seaLevelEngine = b.AddStacked(lowerTank, "_connector39", StockParts.SeaLevelEngine, "_connector2");
        b.Sequence(seaLevelEngine, 1)
            .Sequence(interstage, 2)
            .Sequence(vacuumEngine, 3)
            .Sequence(bay, 4);
        return b.Build(fillTanks);
    }

    // An uncrewed probe: the small probe core on an inline battery. 2 parts, no sequences, no tanks.
    public static PartTree ProbeCraft()
    {
        VehicleBuilder b = new VehicleBuilder(StockParts.ProbeCoreSmall);
        b.AddStacked(b.Root, "_connector44", StockParts.Battery1W, "_connector17");
        return b.Build(fillTanks: false);
    }

    // An RCS test craft: the small probe core on a 1 m monopropellant tank with four RCS blocks
    // mounted around the tank's middle at 90 degree steps. 6 parts.
    public static PartTree RcsCraft(bool fillTanks = true)
    {
        VehicleBuilder b = new VehicleBuilder(StockParts.ProbeCoreSmall);
        Part tank = b.AddStacked(b.Root, "_connector44", StockParts.MonopropTank1W, "_connector17");
        foreach (double3 normal in RcsNormals)
            b.AddSurfaceMounted(tank, StockParts.RcsBlock, "_connector7", normal * RcsMountRadius, normal);
        return b.Build(fillTanks);
    }

    // The RCS craft's mount points, in the tank's frame: the tank's 1 m diameter (its Diameter in
    // the part data) puts the surface 0.5 m off its axis.
    public const double RcsMountRadius = 0.5;

    public static readonly double3[] RcsNormals =
    {
        new double3(0.0, 1.0, 0.0),
        new double3(0.0, 0.0, 1.0),
        new double3(0.0, -1.0, 0.0),
        new double3(0.0, 0.0, -1.0),
    };

    // A new part in a tree of its own with its attached internals, as VehicleEditor.SpawnPart
    // creates one: a part template can name an internal part (AttachedInternal, such as the cabin
    // with the seats of a command pod), which the editor adds as a child joined part to part.
    private static Part CreatePart(string partId)
    {
        PartTemplate template = FindPartTemplate(partId);
        Part part = new Part(template.Id, template);
        part.CreateOwnTree().ReinitializeDerivedValues();
        foreach (AttachedInternal attached in part.Modules.Get<AttachedInternal>())
        {
            PartTemplate internalTemplate = FindPartTemplate(attached.InstanceOf);
            Part inner = new Part(internalTemplate.Id, internalTemplate)
            {
                PositionParentAsmb = part.PositionParentAsmb + Rotate(attached.PositionParentAsmb, part.Asmb2ParentAsmb),
                Asmb2ParentAsmb = attached.Asmb2ParentAsmb * part.Asmb2ParentAsmb,
            };
            inner.PositionParentAsmbSafe = inner.PositionParentAsmb;
            inner.Asmb2ParentAsmbSafe = inner.Asmb2ParentAsmb;
            part.AddChild(inner);
            Part.Connection.Connect(part, inner);
        }
        return part;
    }

    private void EnsureOpen(Part part)
    {
        if (_built)
            throw new InvalidOperationException("this builder has built its tree already.");
        if (!ReferenceEquals(part.FullPart.Tree, Tree))
            throw new InvalidOperationException($"part '{part.Template.Id}' does not belong to this builder's vehicle.");
    }

    // A frame whose +X is the outward normal and whose +Y lies along the vehicle +X (or +Z when
    // the normal is along +X), turned by roll about the normal. Rows are the images of the basis
    // vectors, as in Vehicle.ComputeBody2Cce.
    private static doubleQuat MountFrame(double3 normal, double roll)
    {
        double3 reference = Math.Abs(normal.X) < 0.99 ? double3.UnitX : double3.UnitZ;
        double3 y = (reference - normal * double3.Dot(reference, normal)).Normalized();
        double3 z = double3.Cross(normal, y);
        doubleQuat frame = doubleQuat.CreateFromRotationMatrix(new double4x4(
            normal.X, normal.Y, normal.Z, 0.0,
            y.X, y.Y, y.Z, 0.0,
            z.X, z.Y, z.Z, 0.0,
            0.0, 0.0, 0.0, 1.0));
        return roll == 0.0 ? frame : doubleQuat.Concatenate(doubleQuat.CreateFromAxisAngle(double3.UnitX, roll), frame);
    }

    // Turns the part and its tree children by q about the pivot and moves the pivot to target, as
    // Part.Connector.ConnectAndMerge moves a part it joins.
    private static void MovePartSubtree(Part part, doubleQuat q, double3 pivot, double3 target)
    {
        Move(part, q, pivot, target);
        PartTreeChildrenIterator children = new PartTreeChildrenIterator(part);
        for (Part? next = children.GetNextNode(); next != null; next = children.GetNextNode())
            Move(next, q, pivot, target);
    }

    private static void Move(Part part, doubleQuat q, double3 pivot, double3 target)
    {
        part.Asmb2ParentAsmb = part.Asmb2ParentAsmb.Concatenate(q);
        part.PositionParentAsmb = target + Rotate(part.PositionParentAsmb - pivot, q);
        part.Asmb2ParentAsmbSafe = part.Asmb2ParentAsmb;
        part.PositionParentAsmbSafe = part.PositionParentAsmb;
    }

    // The rotation of Double3Ex.Transform(double3, doubleQuat). The extension cannot be called from
    // here without a reference to BepuUtilities, because its overload set includes one that takes
    // a BepuUtilities.Symmetric3x3.
    internal static double3 Rotate(double3 v, doubleQuat q)
    {
        double x2 = q.X + q.X, y2 = q.Y + q.Y, z2 = q.Z + q.Z;
        double wx = q.W * x2, wy = q.W * y2, wz = q.W * z2;
        double xx = q.X * x2, xy = q.X * y2, xz = q.X * z2;
        double yy = q.Y * y2, yz = q.Y * z2, zz = q.Z * z2;
        return new double3(
            v.X * (1.0 - yy - zz) + v.Y * (xy - wz) + v.Z * (xz + wy),
            v.X * (xy + wz) + v.Y * (1.0 - xx - zz) + v.Z * (yz - wx),
            v.X * (xz - wy) + v.Y * (yz + wx) + v.Z * (1.0 - xx - yy));
    }
}
