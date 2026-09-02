using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using KSA.Rendering;
using RenderCore;

namespace HeadlessHarness.Harness;

// The main viewport of a GPU-free run, registered by HeadlessSession.RegisterHeadlessViewport. Sim
// code reaches cameras through Program.MainViewport, so the registry needs one; the sim side of a
// viewport (cameras, controllers, part picker) is pure CPU, the render side is not.
//
// Implementing the interface rather than reflecting into GameViewport means an IViewport or
// IGameViewport change breaks this build. That covers those two only: stock also implements
// IViewportLifecycle and IGameViewportLifecycle, left out here because only render, resize and
// secondary-lease paths cast to them. ViewportBase cannot be reused, its constructor needs a
// Renderer.
//
// Main-thread only, like the game's own viewports.
public sealed class HeadlessViewport : IGameViewport
{
    // Fixed, so camera-derived values reproduce across machines.
    public const int DefaultWidth = 1920;
    public const int DefaultHeight = 1080;

    // Where Program.AddViewport parks a fresh camera. Nothing headless calls SetFollow, so it stays
    // here, which keeps Vehicle.PrepareWorker's useHighFidelityOceanPhysics false.
    private static readonly double3 InitialCameraPositionEcl =
        new double3(-28423595433.0, 350059017438.0, 952938436359.0);

    public HeadlessViewport(ViewportId id, int shaderSlot, int2 size)
    {
        if (size.X <= 0 || size.Y <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), size, "viewport size must be positive.");

        Id = id;
        ShaderSlot = shaderSlot;
        Size = size;
        Name = $"Headless Viewport {id.Value}";

        BaseCamera = CreateCamera(size);
        MapCamera = CreateCamera(size);
        FlyController = new FlyController(BaseCamera);
        FlyController.SetDefaultSpeed();
        OrbitController = new OrbitController(BaseCamera, "Orbit", independentView: false);
        MapController = new MapController(MapCamera);
        IvaController = new IVAController(BaseCamera);
        FixedController = new FixedController(BaseCamera);
    }

    // Program.AddViewport also applies the player's field of view. Skipped: that is a machine-local
    // setting, and the camera's own default is a constant.
    private static Camera CreateCamera(int2 size)
    {
        Camera camera = new Camera(size);
        camera.SetPosition(InitialCameraPositionEcl);
        camera.LookAt(double3.Zero, double3.UnitY);
        return camera;
    }

    #region IViewport identity and state

    public ViewportId Id { get; }

    public int ShaderSlot { get; }

    public string Name { get; private set; }

    public ViewportType Type => ViewportType.Main;

    public ViewportStateFlags State { get; private set; }

    // Nothing to draw, capture or hear. Every reader of these two is a render, selection or
    // lighting path.
    public ViewportOptionFlags OptionFlags => ViewportOptionFlags.None;

    public ViewportLightMode LightMode => ViewportLightMode.None;

    public bool Visible => State.HasAll(ViewportStateFlags.Visible);

    public bool Hovered => State.HasAll(ViewportStateFlags.Hovered);

    public bool MenuBarInUse => State.HasAll(ViewportStateFlags.MenuBarInUse);

    public CameraMode Mode { get; private set; } = CameraMode.Orbit;

    public int Width => Size.X;

    public int Height => Size.Y;

    public int2 Size { get; private set; }

    public int2 PendingSize { get; private set; }

    // Off until asked, like ViewportBase.
    public bool AllowResize { get; private set; }

    public float2 Position { get; private set; }

    public uint ImGuiId { get; set; }

    public void SetVisible(bool inValue) => State = ViewportEx.Set(State, ViewportStateFlags.Visible, inValue);

    public void SetName(string inName) => Name = inName;

    public void SetResizeAllowed(bool inValue) => AllowResize = inValue;

    public void SetPosition(float2 inPosition) => Position = inPosition;

    // ViewportBase only parks the request, because the render loop applies it after rebuilding the
    // targets. No targets and no render loop here, so land directly on GameViewport.SetSize's end
    // state.
    public bool RequestResize(int2 inNewSize)
    {
        if (!AllowResize || inNewSize.X <= 0 || inNewSize.Y <= 0 || inNewSize == Size)
            return false;
        Size = inNewSize;
        PendingSize = int2.Zero;
        BaseCamera.Resize(inNewSize);
        MapCamera.Resize(inNewSize);
        return true;
    }

    #endregion

    #region IGameViewport cameras and controllers

    public Camera BaseCamera { get; }

    public Camera MapCamera { get; }

    public FlyController FlyController { get; }

    public OrbitController OrbitController { get; }

    public MapController MapController { get; }

    public IVAController IvaController { get; }

    public FixedController FixedController { get; }

    public ViewportPartPicker PartPicker { get; } = new ViewportPartPicker();

    // No audio pump: GameViewport only builds a BiomeSoundController for a HasAudio viewport.
    public float IvaAudio => 0f;

    public BiomeSoundController? BiomeSoundController => null;

    public Camera GetCamera() => Mode == CameraMode.Map ? MapCamera : BaseCamera;

    public Controller GetActiveController() => Mode switch
    {
        CameraMode.Orbit => OrbitController,
        CameraMode.Free => FlyController,
        CameraMode.Map => MapController,
        CameraMode.IVA => IvaController,
        CameraMode.Fixed => FixedController,
        _ => throw new ArgumentOutOfRangeException(nameof(Mode), Mode, "unknown camera mode."),
    };

    // Mirrors GameViewport minus its ClearHeldPlayerInput call, which would drop a test's throttle.
    public void SetCameraMode(CameraMode inMode)
    {
        if (Mode == inMode)
            return;
        Controller leaving = GetActiveController();
        CameraMode previous = Mode;
        leaving.OnSwitchOff(inMode);
        Mode = inMode;
        GetActiveController().OnSwitchOn(previous);
    }

    public bool NextCameraMode()
    {
        switch (Mode)
        {
            case CameraMode.Orbit: SetCameraMode(CameraMode.Free); return true;
            case CameraMode.Free: SetCameraMode(CameraMode.IVA); return true;
            case CameraMode.IVA: SetCameraMode(CameraMode.Orbit); return true;
            default: return false;
        }
    }

    // GameViewport also blends its audio here. Unused by the harness, which drives solvers rather
    // than player frames; a consumer wanting camera-follow state to advance can call it.
    public void OnFrame(double inDeltaTime)
    {
        GetActiveController().OnFrame(this, inDeltaTime);
        GetCamera().OnFrame(inDeltaTime);
    }

    // A no-op, not a throw: the game calls this over every registered viewport.
    public void DrawImGui()
    {
    }

    #endregion

    #region Render surface: not available headless

    // Null is the stock answer for a main viewport, which renders into the shared offscreen target
    // rather than owning one.
    public RenderTarget? MainTarget => null;

    // No null to return, so throw named rather than surface as an NRE inside a render pass.
    public ImTextureRef ImGuiTexture => throw NoRenderSurface(nameof(ImGuiTexture));

    public RenderTarget OffscreenTarget => throw NoRenderSurface(nameof(OffscreenTarget));

    public IRenderPassInfo CompositeTarget => throw NoRenderSurface(nameof(CompositeTarget));

    private static NotSupportedException NoRenderSurface(string member) =>
        new($"[HeadlessHarness] {nameof(HeadlessViewport)}.{member} needs a render surface, which a " +
            "GPU-free run does not have. Reaching it means render code ran headless.");

    #endregion

    // Deliberately not ViewportBase.Dispose's counterpart, which also unregisters: this viewport is
    // the one Program.MainViewport resolves to for the life of the process.
    public void Dispose()
    {
    }
}
