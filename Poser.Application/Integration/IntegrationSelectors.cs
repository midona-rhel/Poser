using Poser.Domain.Operations;
using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>
/// Applies Penumbra collections, Glamourer appearance, and Customize+ profiles.
/// Keeps original values in IntegrationOwnership for Reset and captures appearance
/// for undo. Operations that call McdfGate refuse changes during an MCDF import.
/// </summary>
public sealed class IntegrationSelectors
{
    private readonly IIntegrationResolutionPort _actors;
    private readonly IPenumbraPort _penumbra;
    private readonly IGlamourerPort _glamourer;
    private readonly ICustomizePlusPort _customizePlus;
    private readonly IntegrationOwnership _ownership;
    private readonly McdfTransaction _mcdf;

    public IntegrationSelectors(
        IIntegrationResolutionPort actors,
        IPenumbraPort penumbra,
        IGlamourerPort glamourer,
        ICustomizePlusPort customizePlus,
        IntegrationOwnership ownership,
        McdfTransaction mcdf)
    {
        _actors = actors;
        _penumbra = penumbra;
        _glamourer = glamourer;
        _customizePlus = customizePlus;
        _ownership = ownership;
        _mcdf = mcdf;
    }

    public IntegrationAvailability Penumbra => _penumbra.Penumbra;
    public IntegrationAvailability Glamourer => _glamourer.Glamourer;
    public IntegrationAvailability CustomizePlus => _customizePlus.CustomizePlus;

    public GlamourerAccess AppearanceAccess(ActorId actor) => _glamourer.ProbeGlamourerAccess(actor);

    private IntegrationResult EditLook(ActorId actor, Func<IntegrationResult> edit)
    {
        if (McdfGate(actor) is { } gate)
            return gate;
        var access = AppearanceAccess(actor);
        return access.CanEdit ? edit() : IntegrationResult.Refused(access);
    }

    public IntegrationOverrides OverridesFor(ActorId actor) => _ownership.OverridesFor(actor);

    /// <summary>The body profile the actor wears, Poser-owned contents first.</summary>
    public IntegrationValue<string?> CaptureBodyProfile(ActorId actor) => _ownership.CaptureBodyProfile(actor);

    /// <summary>Loads the pickable lists; each picker calls this on open.</summary>
    public IntegrationValue<IReadOnlyList<ExternalItem>> ListCollections() => _penumbra.GetCollections();
    public IntegrationValue<IReadOnlyList<ExternalItem>> ListDesigns() => _glamourer.GetDesigns();
    public IntegrationValue<IReadOnlyList<ExternalItem>> ListBodyProfiles() => _customizePlus.GetBodyProfiles();

    /// <summary>The collection currently affecting the actor (for the
    /// trigger readout).</summary>
    public IntegrationValue<CollectionAssignment> ReadCollection(ActorId actor) =>
        _penumbra.GetCollectionAssignment(actor);

    public IntegrationValue<Guid> ReadPlayerCollection() => _penumbra.GetPlayerCollection();

    /// <summary>
    /// Captures appearance for undo. By default an unreadable Glamourer state fails
    /// the capture. Removal callers can instead omit unavailable appearance so the
    /// actor remains removable; the snapshot records that omission.
    /// </summary>
    public IntegrationValue<ActorAppearanceSnapshot> TryCaptureHistory(ActorId actor, bool captureCollection = true,
        bool omitUnreadableLook = false)
    {
        if (_mcdf.Busy) return IntegrationValue<ActorAppearanceSnapshot>.Fail("Wait for the current character-file operation to finish.");
        var owned = OverridesFor(actor);
        Guid? resources = null;
        if (owned.Mcdf is { } mcdf)
        {
            if (mcdf.OperationDirectory is not { } directory || mcdf.SourcePath == null)
                return IntegrationValue<ActorAppearanceSnapshot>.Fail("The imported appearance resources are unavailable.");
            var retained = _mcdf.RetainHistory(directory);
            if (!retained.Success) return IntegrationValue<ActorAppearanceSnapshot>.Fail(retained.Detail!);
            resources = retained.Value;
            if (mcdf.RedrawPending)
                return IntegrationValue<ActorAppearanceSnapshot>.Fail("The actor's previous redraw has not completed.");
        }
        string? state = null;
        bool lookOmitted = false;
        CollectionAssignment? collection = null;
        SpawnCollectionSnapshot? inherited = null;
        if (owned.Mcdf == null)
        {
            if (Glamourer.Available || owned.DesignOwned)
            {
                var look = GetStateJson(actor);
                if (omitUnreadableLook && look.AppearanceRefusal == GlamourerAccessKind.Unavailable)
                    lookOmitted = true;
                else if (!look.Success || look.Value == null)
                    return IntegrationValue<ActorAppearanceSnapshot>.Fail(look.Detail ?? "The actor's appearance could not be captured.");
                else
                    state = look.Value;
            }
            if (captureCollection && (Penumbra.Available || owned.CollectionOwned))
            {
                var read = ReadCollection(actor);
                if (!read.Success || read.Value == null)
                    return IntegrationValue<ActorAppearanceSnapshot>.Fail(read.Detail ?? "The actor's collection could not be captured.");
                if (_ownership.ForeignTemporaryCollection(owned, read.Value) is { } foreign)
                {
                    var captured = _penumbra.CaptureInheritedCollection(actor);
                    if (!captured.Success || captured.Value == null)
                        return IntegrationValue<ActorAppearanceSnapshot>.Fail(captured.Detail ?? foreign);
                    inherited = captured.Value;
                }
                collection = read.Value;
            }
        }
        var body = _ownership.CaptureBodyProfile(actor);
        return body.Success
            ? IntegrationValue<ActorAppearanceSnapshot>.Ok(new(state, collection, body.Value,
                owned.BodyProfileName, owned.Mcdf?.SourcePath, resources, inherited, lookOmitted))
            : IntegrationValue<ActorAppearanceSnapshot>.Fail(body.Detail ?? "The Customize+ profile could not be captured.");
    }

    public async Task<IntegrationResult> RestoreHistoryAndWait(ActorId actor, ActorAppearanceSnapshot snapshot,
        Func<bool> stillCurrent, CancellationToken cancellation)
    {
        await _mcdf.CurrentCompletion.WaitAsync(cancellation);
        // Ordinary state cannot be applied while an imported package owns the
        // actor. Release it through its existing transaction and await cleanup.
        if (snapshot.McdfPath == null)
        {
            var reset = await _actors.OnFrameworkThread(() =>
                cancellation.IsCancellationRequested || !stillCurrent()
                    ? IntegrationResult.Fail("The actor restoration is no longer current.")
                    : _mcdf.Reset(actor));
            if (!reset.Success) return reset;
            await _mcdf.CurrentCompletion.WaitAsync(cancellation);
        }
        Task pending = Task.CompletedTask;
        Guid? operation = null;
        var started = await _actors.OnFrameworkThread(() =>
        {
            if (cancellation.IsCancellationRequested || !stillCurrent())
                return IntegrationResult.Fail("The actor restoration is no longer current.");
            IntegrationResult result;
            if (snapshot.McdfPath is { } path)
            {
                result = snapshot.McdfResources is { } resources
                    ? _mcdf.RestoreHistory(actor, resources, path) : _mcdf.BeginImport(actor, path);
                operation = _mcdf.Receipt?.OperationId;
            }
            else
                result = RestoreHistory(actor, snapshot, redraw: false);
            pending = _mcdf.CurrentCompletion;
            return result;
        });
        if (!started.Success) return started;
        try { await pending.WaitAsync(cancellation); }
        catch (OperationCanceledException)
        {
            if (operation is { } admitted)
                await _actors.OnFrameworkThread(() => _mcdf.CancelIfCurrent(admitted));
            throw;
        }
        return await _actors.OnFrameworkThread(() =>
        {
            if (cancellation.IsCancellationRequested || !stillCurrent())
                return IntegrationResult.Fail("The actor restoration is no longer current.");
            return operation == null || _mcdf.Receipt is { State: OperationReceiptState.Applied } receipt
                && receipt.OperationId == operation
                ? IntegrationResult.Ok()
                : IntegrationResult.Fail(_mcdf.Receipt?.Detail ?? "The character-file restoration failed.");
        });
    }

    /// <summary>MCDF packages restore through BeginImport first; ordinary looks replay these captured values.</summary>
    public IntegrationResult RestoreHistory(ActorId actor, ActorAppearanceSnapshot snapshot)
        => RestoreHistory(actor, snapshot, redraw: true);

    private IntegrationResult RestoreHistory(ActorId actor, ActorAppearanceSnapshot snapshot, bool redraw)
    {
        if (snapshot.McdfPath is not null)
            return snapshot.McdfResources is { } resources
                ? _mcdf.RestoreHistory(actor, resources, snapshot.McdfPath) : _mcdf.BeginImport(actor, snapshot.McdfPath);
        var failures = new List<string>();
        void Check(IntegrationResult result)
        {
            if (!result.Success) failures.Add(result.Detail ?? "Appearance restore failed.");
        }
        if (snapshot.InheritedCollection is { } inherited)
        {
            var restored = _penumbra.RestoreInheritedCollection(actor, inherited);
            if (!restored.Success) return restored;
            if (redraw) Check(_penumbra.RequestRedraw(actor));
        }
        else if (snapshot.Collection is { } collection)
        {
            if (collection.HasIndividualAssignment)
                Check(SetCollection(actor, collection.EffectiveId, collection.EffectiveName, redraw));
            else
            {
                var restored = _penumbra.RestoreCollection(actor, new(false, null));
                Check(restored);
                if (restored.Success)
                {
                    var current = OverridesFor(actor);
                    _ownership.Mutate(actor, current with
                    {
                        Baseline = current.Baseline with { Collection = null },
                        CollectionOwned = false,
                        CollectionName = null,
                    });
                    if (redraw) Check(_penumbra.RequestRedraw(actor));
                }
            }
        }
        if (snapshot.BodyProfileJson is { } profile)
            Check(ApplyBodyProfileJson(actor, profile, snapshot.BodyProfileName ?? "Restored profile"));
        else
            Check(ResetBodyProfile(actor));
        if (snapshot.StateJson is { } json)
        {
            var ownership = OwnLook(actor);
            Check(ownership);
            if (ownership.Success) Check(ApplyStateJson(actor, json));
        }
        return failures.Count == 0 ? IntegrationResult.Ok() : IntegrationResult.Fail(string.Join("; ", failures));
    }

    public IntegrationResult OpenGlamourer(ActorId actor) => _glamourer.OpenGlamourer(actor);

    // ── the wardrobe ─────────────────────────────

    public IntegrationResult SetItem(ActorId actor, EquipSlot slot, ulong itemId, byte dye1, byte dye2) =>
        EditLook(actor, () => _glamourer.SetItem(actor, slot, itemId, dye1, dye2));

    public IntegrationResult SetFacewear(ActorId actor, ulong bonusItemId) => EditLook(actor, () => _glamourer.SetFacewear(actor, bonusItemId));

    public IntegrationResult SetMetaSwitch(ActorId actor, MetaSwitch which, bool on) => EditLook(actor, () => _glamourer.SetMetaSwitch(actor, which, on));

    private IntegrationValue<T> ReadLook<T>(ActorId actor, Func<IntegrationValue<T>> read)
    {
        var access = AppearanceAccess(actor);
        return access.CanEdit ? read() : IntegrationValue<T>.Refused(access);
    }

    public IntegrationValue<string> GetStateJson(ActorId actor) => ReadLook(actor, () => _glamourer.GetGlamourerStateJson(actor));

    public IntegrationValue<WardrobeState> ReadWardrobe(ActorId actor) => ReadLook(actor, () => _glamourer.GetWardrobeState(actor));

    public IntegrationValue<CustomizeState> ReadCustomize(ActorId actor) => ReadLook(actor, () => _glamourer.GetCustomizeState(actor));

    public IntegrationResult SetCustomize(ActorId actor, IReadOnlyDictionary<CustomizeKey, int> values) =>
        EditLook(actor, () => _glamourer.SetCustomize(actor, values));

    public IntegrationResult ApplyStateJson(ActorId actor, string stateJson) => EditLook(actor, () => _glamourer.ApplyGlamourerStateJson(actor, stateJson));

    public IntegrationResult RevertState(ActorId actor) => EditLook(actor, () => _glamourer.RevertGlamourerState(actor));

    public IntegrationValue<Guid> SaveActorDesign(ActorId actor, string name)
    {
        var access = AppearanceAccess(actor);
        if (!access.CanEdit)
            return IntegrationValue<Guid>.Refused(access);
        var state = GetStateJson(actor);
        if (!state.Success || state.Value is null)
            return new(false, default, state.Detail, state.AppearanceRefusal);
        // Recheck access after reading the state, before creating the saved design.
        access = AppearanceAccess(actor);
        return access.CanEdit ? _glamourer.AddDesign(state.Value, name) : IntegrationValue<Guid>.Refused(access);
    }

    public IntegrationValue<Guid> SaveDesign(string stateJson, string name) => _glamourer.AddDesign(stateJson, name);

    // ── Selectors ────────────────────────────────────────────────────────

    /// <summary>
    /// Requests a Penumbra redraw using the actor's current collection.
    /// </summary>
    public IntegrationResult Redraw(ActorId actor)
    {
        var redraw = _penumbra.RequestRedraw(actor);
        return redraw.Success
            ? IntegrationResult.Ok()
            : IntegrationResult.Fail($"The redraw failed: {redraw.Detail}");
    }

    public IntegrationResult SetCollection(ActorId actor, Guid collection, string name)
        => SetCollection(actor, collection, name, redraw: true);

    public async Task<IntegrationResult> SetCollectionAndWait(ActorId actor, Guid collection,
        string name, TimeSpan timeout, CancellationToken cancellation)
    {
        var applied = await _actors.OnFrameworkThread(() => SetCollection(actor, collection, name, redraw: false));
        if (!applied.Success) return applied;
        return await _penumbra.RedrawAndWait(actor, timeout, cancellation);
    }

    private IntegrationResult SetCollection(ActorId actor, Guid collection, string name, bool redraw)
    {
        if (McdfGate(actor) is { } gate)
            return gate;
        var current = OverridesFor(actor);

        var incoming = _penumbra.GetCollectionAssignment(actor);
        if (!incoming.Success || incoming.Value is not { } assignment)
            return IntegrationResult.Fail(incoming.Detail ?? "The incoming collection could not be captured.");
        SpawnCollectionSnapshot? inherited = null;
        if (_ownership.ForeignTemporaryCollection(current, assignment) is { } foreign)
        {
            // Duplicate collections belong to our spawn owner, not MCDF.
            // Capture their resources before releasing the temporary assignment.
            var captured = _penumbra.CaptureInheritedCollection(actor);
            if (!captured.Success || captured.Value == null)
                return IntegrationResult.Fail(captured.Detail ?? foreign);
            inherited = captured.Value;
        }
        var baseline = current.Baseline.Collection ?? new CollectionBaseline(
            assignment.HasIndividualAssignment,
            assignment.HasIndividualAssignment ? assignment.EffectiveId : null)
        { InheritedCollection = inherited };

        var applied = _penumbra.SetIndividualCollection(actor, collection);
        if (!applied.Success)
            return applied;

        _ownership.Mutate(actor, current with
        {
            Baseline = current.Baseline with { Collection = baseline },
            CollectionOwned = true,
            CollectionName = name,
        });
        // Penumbra applies a changed assignment on the next redraw; a
        // failed request is reported, not swallowed — the assignment
        // itself stands and stays owned either way.
        if (!redraw) return IntegrationResult.Ok();
        var request = _penumbra.RequestRedraw(actor);
        return request.Success
            ? IntegrationResult.Ok()
            : IntegrationResult.Fail(
                $"The collection was assigned, but the redraw failed: {request.Detail}");
    }

    public IntegrationResult ResetCollection(ActorId actor)
    {
        var current = OverridesFor(actor);
        if (!current.CollectionOwned)
            return IntegrationResult.Ok();
        if (current.Baseline.Collection is not { } baseline)
            return IntegrationResult.Fail("No captured collection baseline exists.");

        var restored = _penumbra.RestoreCollection(actor, baseline);
        if (!restored.Success)
            return restored;

        _ownership.Mutate(actor, current with
        {
            Baseline = current.Baseline with { Collection = null },
            CollectionOwned = false,
            CollectionName = null,
        });
        var redraw = _penumbra.RequestRedraw(actor);
        return redraw.Success
            ? IntegrationResult.Ok()
            : IntegrationResult.Fail(
                $"The assignment was restored, but the redraw failed: {redraw.Detail}");
    }

    public IntegrationResult ApplyDesign(ActorId actor, Guid design, string name)
    {
        if (McdfGate(actor) is { } gate)
            return gate;
        var access = AppearanceAccess(actor);
        if (!access.CanEdit)
            return IntegrationResult.Refused(access);
        var current = OverridesFor(actor);

        var state = current.Baseline.GlamourerState;
        if (state == null)
        {
            // Capture must succeed before applying the new design.
            var incoming = _glamourer.CaptureGlamourerState(actor);
            if (!incoming.Success || incoming.Value is not { } captured)
                return new(false, incoming.Detail ?? "The incoming Glamourer state could not be captured.", incoming.AppearanceRefusal);
            state = captured;
        }

        var applied = _glamourer.ApplyDesign(actor, design);
        if (!applied.Success)
            return applied;

        _ownership.Mutate(actor, current with
        {
            Baseline = current.Baseline with { GlamourerState = state },
            DesignOwned = true,
            DesignName = name,
            DesignActorName = current.DesignActorName ?? NameOf(actor),
        });
        return IntegrationResult.Ok();
    }

    /// <summary>
    /// Saves the current Glamourer state once, before wardrobe or character edits.
    /// Reset can restore that state instead of reverting to the game's appearance.
    /// </summary>
    public IntegrationResult OwnLook(ActorId actor)
    {
        if (McdfGate(actor) is { } gate)
            return gate;
        var access = AppearanceAccess(actor);
        if (!access.CanEdit)
            return IntegrationResult.Refused(access);
        var current = OverridesFor(actor);
        if (current.DesignOwned && current.Baseline.GlamourerState != null)
            return IntegrationResult.Ok();
        var state = current.Baseline.GlamourerState;
        if (state == null)
        {
            var incoming = _glamourer.CaptureGlamourerState(actor);
            if (!incoming.Success || incoming.Value is not { } captured)
                return new(false, incoming.Detail ?? "The Glamourer state could not be captured.", incoming.AppearanceRefusal);
            state = captured;
        }
        _ownership.Mutate(actor, current with
        {
            Baseline = current.Baseline with { GlamourerState = state },
            DesignOwned = true,
            DesignName = current.DesignName ?? "Edited look",
            DesignActorName = current.DesignActorName ?? NameOf(actor),
        });
        return IntegrationResult.Ok();
    }

    private string? NameOf(ActorId actor)
    {
        var named = _actors.GetActorName(actor);
        return named.Success && !string.IsNullOrWhiteSpace(named.Value) ? named.Value : null;
    }

    public IntegrationResult ResetDesign(ActorId actor)
    {
        if (McdfGate(actor) is { } gate)
            return gate;
        var access = AppearanceAccess(actor);
        if (!access.CanEdit)
            return IntegrationResult.Refused(access);
        var current = OverridesFor(actor);
        if (!current.DesignOwned)
            return IntegrationResult.Ok();
        if (current.Baseline.GlamourerState is not { } state)
            return IntegrationResult.Fail("No captured Glamourer state exists.");

        // Reapply the captured incoming state exactly — not a revert to the
        // game's own appearance.
        var restored = _glamourer.RestoreGlamourerState(actor, state);
        if (!restored.Success)
            return restored;

        _ownership.Mutate(actor, current with
        {
            Baseline = current.Baseline with { GlamourerState = null },
            DesignOwned = false,
            DesignName = null,
        });
        return IntegrationResult.Ok();
    }

    public IntegrationResult SetBodyProfile(ActorId actor, Guid profile, string name)
    {
        if (McdfGate(actor) is { } gate) return gate;
        var json = _customizePlus.GetBodyProfileJson(profile);
        return json.Success && json.Value is { } value
            ? ApplyBodyProfileJson(actor, value, name)
            : IntegrationResult.Fail(json.Detail ?? "The profile could not be read.");
    }

    /// <summary>Replays the captured profile contents, not a mutable saved-profile selection.</summary>
    public IntegrationResult ApplyBodyProfileJson(ActorId actor, string profileJson, string name)
    {
        if (McdfGate(actor) is { } gate)
            return gate;
        var current = OverridesFor(actor);

        var probe = _customizePlus.ProbeBodyProfile(actor);
        if (!probe.Success || probe.Value is not { } bodyState)
            return IntegrationResult.Fail(probe.Detail ?? "The Customize+ state could not be read.");
        if (IntegrationOwnership.ForeignTemporaryBody(current, bodyState))
            return IntegrationResult.Fail(
                "This actor has a temporary Customize+ profile from another plugin; Poser will not displace it.");

        var baseline = current.Baseline;
        if (!baseline.BodyProfileCaptured)
            baseline = baseline with
            {
                SavedBodyProfile = bodyState.ActiveIsSaved ? bodyState.ActiveProfile : null,
                BodyProfileCaptured = true,
            };

        var applied = _customizePlus.ApplyTemporaryBodyProfile(actor, profileJson);
        if (!applied.Success || applied.Value == default)
            return IntegrationResult.Fail(applied.Detail ?? "The temporary profile could not be applied.");

        _ownership.Mutate(actor, current with
        {
            Baseline = baseline,
            TemporaryBodyProfile = applied.Value,
            BodyProfileName = name,
            BodyProfileJson = profileJson,
        });
        return IntegrationResult.Ok();
    }

    public IntegrationResult ResetBodyProfile(ActorId actor)
    {
        var current = OverridesFor(actor);
        if (current.TemporaryBodyProfile is not { } owned)
            return IntegrationResult.Ok();

        // Delete the profile Poser created by its ID. Deleting by actor could
        // remove a replacement profile created by another plugin.
        var deleted = _customizePlus.DeleteTemporaryBodyProfileById(owned);
        if (!deleted.Success)
            return deleted;

        _ownership.Mutate(actor, current with
        {
            Baseline = current.Baseline with { SavedBodyProfile = null, BodyProfileCaptured = false },
            TemporaryBodyProfile = null,
            BodyProfileName = null,
            BodyProfileJson = null,
        });
        return IntegrationResult.Ok();
    }

    /// <summary>
    /// Checks whether applying a profile would replace another plugin's temporary
    /// Customize+ profile. Returns an error when that profile cannot be preserved.
    /// </summary>
    public IntegrationResult CheckBodyProfileDisplaceable(ActorId actor)
    {
        var probe = _customizePlus.ProbeBodyProfile(actor);
        if (!probe.Success || probe.Value is not { } bodyState)
            return IntegrationResult.Fail(probe.Detail ?? "The Customize+ state could not be read.");
        return IntegrationOwnership.ForeignTemporaryBody(OverridesFor(actor), bodyState)
            ? IntegrationResult.Fail(
                "This actor has a temporary Customize+ profile from another plugin; Poser will not displace it.")
            : IntegrationResult.Ok();
    }

    private IntegrationResult? McdfGate(ActorId actor)
    {
        if (_mcdf.Busy)
            return IntegrationResult.Fail("An MCDF operation is running; wait for it to finish.");
        return OverridesFor(actor).Mcdf != null
            ? IntegrationResult.Fail(
                "An imported character file owns this actor's external appearance. Reset MCDF first.")
            : null;
    }
}
