using System.Numerics;
using Content.Shared.Access.Components;
using Content.Shared.Actions;
using Content.Shared.Audio;
using Content.Shared.Buckle;
using Content.Shared.Buckle.Components;
using Content.Shared.Hands;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Item;
using Content.Shared.Light.Components;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Popups;
using Content.Shared.Tag;
using Content.Shared.Vehicle.Components;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Network;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Serialization;

namespace Content.Shared.Vehicle;

/// <summary>
/// Stores the VehicleVisuals and shared event
/// Nothing for a system but these need to be put somewhere in
/// Content.Shared
/// </summary>
public abstract partial class SharedVehicleSystem : EntitySystem
{
    [Dependency] private readonly INetManager _netManager = default!;

    [Dependency] protected readonly SharedAppearanceSystem Appearance = default!;
    [Dependency] private readonly SharedAudioSystem _audioSystem = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _modifier = default!;
    [Dependency] private readonly SharedAmbientSoundSystem _ambientSound = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly TagSystem _tagSystem = default!;
    [Dependency] private readonly SharedPopupSystem _popupSystem = default!;
    [Dependency] private readonly SharedVirtualItemSystem _virtualItemSystem = default!;
    [Dependency] private readonly SharedActionsSystem _actionsSystem = default!;
    [Dependency] private readonly SharedJointSystem _joints = default!;
    [Dependency] private readonly SharedBuckleSystem _buckle = default!;
    [Dependency] private readonly SharedMoverController _mover = default!;

    private const string KeySlot = "key_slot";

    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();
        InitializeRider();

        SubscribeLocalEvent<VehicleComponent, ComponentStartup>(OnVehicleStartup);
        SubscribeLocalEvent<VehicleComponent, StrappedEvent>(OnBuckled);
        SubscribeLocalEvent<VehicleComponent, UnstrappedEvent>(OnUnBuckled);

        SubscribeLocalEvent<VehicleComponent, HonkActionEvent>(OnHonkAction);
        SubscribeLocalEvent<VehicleComponent, EntInsertedIntoContainerMessage>(OnEntInserted);
        SubscribeLocalEvent<VehicleComponent, EntRemovedFromContainerMessage>(OnEntRemoved);
        SubscribeLocalEvent<VehicleComponent, RefreshMovementSpeedModifiersEvent>(OnRefreshMovementSpeedModifiers);
        SubscribeLocalEvent<VehicleComponent, MoveEvent>(OnMoveEvent);
        SubscribeLocalEvent<VehicleComponent, GetAdditionalAccessEvent>(OnGetAdditionalAccess);

        SubscribeLocalEvent<InVehicleComponent, GettingPickedUpAttemptEvent>(OnGettingPickedUpAttempt);
    }

    /// <summary>
    /// This just controls whether the wheels are turning.
    /// </summary>
    public override void Update(float frameTime)
    {
        var vehicleQuery = EntityQueryEnumerator<VehicleComponent, InputMoverComponent>();
        while (vehicleQuery.MoveNext(out var uid, out var vehicle, out var mover))
        {
            if (!vehicle.AutoAnimate)
                continue;

            // Why is this updating appearance data every tick, instead of when it needs to be updated???

            if (_mover.GetVelocityInput(mover).Sprinting == Vector2.Zero)
            {
                UpdateAutoAnimate(uid, false);
                continue;
            }

            UpdateAutoAnimate(uid, true);
        }
    }

    private void OnVehicleStartup(EntityUid uid, VehicleComponent component, ComponentStartup args)
    {
        UpdateDrawDepth(uid, 2);

        // This code should be purged anyway but with that being said this doesn't handle components being changed.
        if (TryComp<StrapComponent>(uid, out var strap))
        {
            component.BaseBuckleOffset = strap.BuckleOffset;
            strap.BuckleOffset = Vector2.Zero;
        }

        _modifier.RefreshMovementSpeedModifiers(uid);
    }

    /// <summary>
    /// Give the user the rider component if they're buckling to the vehicle,
    /// otherwise remove it.
    /// </summary>
    private void OnBuckled(EntityUid uid, VehicleComponent component, ref StrappedEvent args)
    {
        // Add Rider
        var rider = args.Buckle.Owner;
        if (component.UseHand == true)
        {
            // Add a virtual item to rider's hand, unbuckle if we can't.
            if (!_virtualItemSystem.TrySpawnVirtualItemInHand(uid, rider))
            {
                _buckle.TryUnbuckle(rider, uid, true);
                return;
            }
        }
        // Set up the rider and vehicle with each other
        EnsureComp<InputMoverComponent>(uid);
        var riderComp = EnsureComp<RiderComponent>(rider);
        component.Rider = rider;
        component.LastRider = component.Rider;
        Dirty(uid, component);
        Appearance.SetData(uid, VehicleVisuals.HideRider, true);

        _mover.SetRelay(rider, uid);
        riderComp.Vehicle = uid;

        // Update appearance stuff, add actions
        UpdateBuckleOffset(uid, Transform(uid), component);
        if (TryComp<InputMoverComponent>(uid, out var mover))
            UpdateDrawDepth(uid, GetDrawDepth(Transform(uid), component, mover.RelativeRotation.Degrees));

        if (TryComp<ActionsComponent>(rider, out var actions) && TryComp<UnpoweredFlashlightComponent>(uid, out var flashlight))
        {
            _actionsSystem.AddAction(rider, ref flashlight.ToggleActionEntity, flashlight.ToggleAction, uid, actions);
        }

        if (component.HornSound != null)
        {
            _actionsSystem.AddAction(rider, ref component.HornActionEntity, component.HornAction, uid, actions);
        }

        _joints.ClearJoints(rider);

        _tagSystem.AddTag(uid, "DoorBumpOpener");
    }

    private void OnUnBuckled(EntityUid uid, VehicleComponent component, ref UnstrappedEvent args)
    {
        // Remove rider
        var rider = args.Buckle.Owner;

        // Clean up actions and virtual items
        _actionsSystem.RemoveProvidedActions(rider, uid);

        if (component.UseHand == true)
            _virtualItemSystem.DeleteInHandsMatching(rider, uid);


        // Entity is no longer riding
        RemComp<RiderComponent>(rider);
        RemComp<RelayInputMoverComponent>(rider);
        _tagSystem.RemoveTag(uid, "DoorBumpOpener");

        Appearance.SetData(uid, VehicleVisuals.HideRider, false);
        // Reset component
        component.Rider = null;
        Dirty(uid, component);
    }

    /// <summary>
    /// This fires when the rider presses the honk action
    /// </summary>
    private void OnHonkAction(EntityUid uid, VehicleComponent vehicle, HonkActionEvent args)
    {
        if (args.Handled || vehicle.HornSound == null)
            return;

        // TODO: Need audio refactor maybe, just some way to null it when the stream is over.
        // For now better to just not loop to keep the code much cleaner.
        vehicle.HonkPlayingStream = _audioSystem.PlayPredicted(vehicle.HornSound, uid, uid)?.Entity;
        args.Handled = true;
    }

    /// <summary>
    /// Handle adding keys to the ignition, give stuff the InVehicleComponent so it can't be picked
    /// up by people not in the vehicle.
    /// </summary>
    private void OnEntInserted(EntityUid uid, VehicleComponent component, EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID != KeySlot ||
            !_tagSystem.HasTag(args.Entity, "VehicleKey"))
            return;

        // Enable vehicle
        var inVehicle = EnsureComp<InVehicleComponent>(args.Entity);
        inVehicle.Vehicle = component;

        component.HasKey = true;

        var msg = Loc.GetString("vehicle-use-key",
            ("keys", args.Entity), ("vehicle", uid));
        if (_netManager.IsServer)
            _popupSystem.PopupEntity(msg, uid, args.OldParent, PopupType.Medium);

        // Audiovisual feedback
        _ambientSound.SetAmbience(uid, true);
        _modifier.RefreshMovementSpeedModifiers(uid);
    }

    /// <summary>
    /// Turn off the engine when key is removed.
    /// </summary>
    private void OnEntRemoved(EntityUid uid, VehicleComponent component, EntRemovedFromContainerMessage args)
    {
        if (args.Container.ID != KeySlot || !RemComp<InVehicleComponent>(args.Entity))
            return;

        // Disable vehicle
        component.HasKey = false;
        _ambientSound.SetAmbience(uid, false);
        _modifier.RefreshMovementSpeedModifiers(uid);
    }

    private void OnRefreshMovementSpeedModifiers(EntityUid uid, VehicleComponent component, RefreshMovementSpeedModifiersEvent args)
    {
        if (!component.HasKey)
        {
            args.ModifySpeed(0f, 0f);
        }
    }

    // TODO: Shitcode, needs to use sprites instead of actual offsets.
    private void OnMoveEvent(EntityUid uid, VehicleComponent component, ref MoveEvent args)
    {
        if (args.NewRotation == args.OldRotation)
            return;

        // This first check is just for safety
        if (component.AutoAnimate && !HasComp<InputMoverComponent>(uid))
        {
            UpdateAutoAnimate(uid, false);
            return;
        }

        UpdateBuckleOffset(uid, args.Component, component);
        if (TryComp<InputMoverComponent>(uid, out var mover))
            UpdateDrawDepth(uid, GetDrawDepth(args.Component, component, mover.RelativeRotation));
    }

    private void OnGettingPickedUpAttempt(EntityUid uid, InVehicleComponent component, GettingPickedUpAttemptEvent args)
    {
        if (component.Vehicle == null || component.Vehicle.Rider != null && component.Vehicle.Rider != args.User)
            args.Cancel();
    }

    /// <summary>
    /// Depending on which direction the vehicle is facing,
    /// change its draw depth. Vehicles can choose between special drawdetph
    /// when facing north or south. East and west are easy.
    /// </summary>
    private int GetDrawDepth(TransformComponent xform, VehicleComponent component, Angle cameraAngle)
    {
        var itemDirection = cameraAngle.GetDir() switch
        {
            Direction.South => xform.LocalRotation.GetDir(),
            Direction.North => xform.LocalRotation.RotateDir(Direction.North),
            Direction.West => xform.LocalRotation.RotateDir(Direction.East),
            Direction.East => xform.LocalRotation.RotateDir(Direction.West),
            _ => Direction.South
        };

        return itemDirection switch
        {
            Direction.North => component.NorthOver
                ? (int)DrawDepth.DrawDepth.Doors
                : (int)DrawDepth.DrawDepth.WallMountedItems,
            Direction.South => component.SouthOver
                ? (int)DrawDepth.DrawDepth.Doors
                : (int)DrawDepth.DrawDepth.WallMountedItems,
            Direction.West => component.WestOver
                ? (int)DrawDepth.DrawDepth.Doors
                : (int)DrawDepth.DrawDepth.WallMountedItems,
            Direction.East => component.EastOver
                ? (int)DrawDepth.DrawDepth.Doors
                : (int)DrawDepth.DrawDepth.WallMountedItems,
            _ => (int)DrawDepth.DrawDepth.WallMountedItems
        };
    }


    /// <summary>
    /// Change the buckle offset based on what direction the vehicle is facing and
    /// teleport any buckled entities to it. This is the most crucial part of making
    /// buckled vehicles work.
    /// </summary>
    private void UpdateBuckleOffset(EntityUid uid, TransformComponent xform, VehicleComponent component)
    {
        if (!TryComp<StrapComponent>(uid, out var strap))
            return;

        // TODO: Strap should handle this but buckle E/C moment.
        var oldOffset = strap.BuckleOffset;

        strap.BuckleOffset = xform.LocalRotation.Degrees switch
        {
            < 45f => new(0, component.SouthOverride),
            <= 135f => component.BaseBuckleOffset,
            < 225f => new(0, component.NorthOverride),
            <= 315f => new(component.BaseBuckleOffset.X * -1, component.BaseBuckleOffset.Y),
            _ => new(0, component.SouthOverride)
        };

        if (!oldOffset.Equals(strap.BuckleOffset))
            Dirty(uid, strap);

        foreach (var buckledEntity in strap.BuckledEntities)
        {
            _transform.SetLocalPositionNoLerp(buckledEntity, strap.BuckleOffset);
        }
    }

    private void OnGetAdditionalAccess(EntityUid uid, VehicleComponent component, ref GetAdditionalAccessEvent args)
    {
        if (component.Rider == null)
            return;
        args.Entities.Add(component.Rider.Value);
    }

    /// <summary>
    /// Set the draw depth for the sprite.
    /// </summary>
    private void UpdateDrawDepth(EntityUid uid, int drawDepth)
    {
        Appearance.SetData(uid, VehicleVisuals.DrawDepth, drawDepth);
    }

    /// <summary>
    /// Set whether the vehicle's base layer is animating or not.
    /// </summary>
    private void UpdateAutoAnimate(EntityUid uid, bool autoAnimate)
    {
        Appearance.SetData(uid, VehicleVisuals.AutoAnimate, autoAnimate);
    }
}

/// <summary>
/// Stores the vehicle's draw depth mostly
/// </summary>
[Serializable, NetSerializable]
public enum VehicleVisuals : byte
{
    /// <summary>
    /// What layer the vehicle should draw on (assumed integer)
    /// </summary>
    DrawDepth,
    /// <summary>
    /// Whether the wheels should be turning
    /// </summary>
    AutoAnimate,
    HideRider
}

/// <summary>
/// Raised when someone honks a vehicle horn
/// </summary>
public sealed partial class HonkActionEvent : InstantActionEvent
{
}
