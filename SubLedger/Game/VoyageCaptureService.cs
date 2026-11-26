using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Network;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using SubLedger.Core;
using SubLedger.Core.Capture;

namespace SubLedger.Game;

/// <summary>
/// Notices completed voyages and hands them to the ledger. Observation only: it never sends input, never changes
/// game state, and always lets the game finish its own work first.
/// <para>
/// Two independent triggers feed the same ledger, which keeps only the first of any pair:
/// the voyage result window opening (a plain UI event, the approach Argus uses), and the server message that
/// carries the result (the approach SubmarineTracker uses). Either alone is enough; having both means a voyage is
/// still recorded if another plugin closes the window instantly or the window event is ever missed.
/// </para>
/// </summary>
internal sealed unsafe class VoyageCaptureService : IDisposable
{
    private const string ResultAddon = "AirShipExplorationResult";

    /// <summary>Event id of the submarine voyage result message (SubmarineTracker, HookManager.cs).</summary>
    private const uint VoyageResultEventId = 721343;

    private const string Source = "Capture";

    private readonly Plugin plugin;
    private readonly Dictionary<(ulong, uint), long> sessionReturn = [];
    private Hook<PacketDispatcher.Delegates.HandleEventYieldPacket>? packetHook;
    private DateTime nextPoll = DateTime.MinValue;
    private ulong identityFc;
    private bool wasInWorkshop;

    public VoyageCaptureService(Plugin plugin)
    {
        this.plugin = plugin;

        Service.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, ResultAddon, OnResultWindow);
        Service.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, ResultAddon, OnResultWindow);
        Service.Framework.Update += OnFrameworkUpdate;

        try
        {
            packetHook = Service.GameInterop.HookFromAddress<PacketDispatcher.Delegates.HandleEventYieldPacket>(
                (nint)PacketDispatcher.MemberFunctionPointers.HandleEventYieldPacket, OnEventYieldPacket);
            packetHook.Enable();
        }
        catch (Exception ex)
        {
            packetHook = null;
            plugin.Log.Warning(Source, $"The result-message trigger could not be set up ({ex.Message}). The result-window trigger still works.");
        }
    }

    public bool PacketTriggerAvailable => packetHook != null;

    public void Dispose()
    {
        Service.Framework.Update -= OnFrameworkUpdate;
        Service.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, ResultAddon, OnResultWindow);
        Service.AddonLifecycle.UnregisterListener(AddonEvent.PostRefresh, ResultAddon, OnResultWindow);
        packetHook?.Dispose();
        packetHook = null;
    }

    private void OnEventYieldPacket(EventId eventId, short scene, byte yieldId, int* intParams, byte intParamCount)
    {
        // The game always goes first, untouched.
        packetHook!.Original(eventId, scene, yieldId, intParams, intParamCount);

        // Everything below is inside try/catch: an exception escaping a hook would take the game down with it.
        try
        {
            if (eventId.Id != VoyageResultEventId || !plugin.Config.CaptureFromPacket)
                return;

            Capture("packet");
        }
        catch (Exception ex)
        {
            plugin.Log.Error(Source, "Reading the voyage result message failed", ex);
        }
    }

    private void OnResultWindow(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!plugin.Config.CaptureFromWindow)
                return;

            // The same window shows airship results; only submarines are tracked.
            var agent = AgentSubmersibleExplorationResult.Instance();
            if (agent == null || !agent->IsAgentActive())
                return;

            Capture("window");
        }
        catch (Exception ex)
        {
            plugin.Log.Error(Source, "Reading the voyage result window failed", ex);
        }
    }

    private void Capture(string trigger)
    {
        // Make sure the ledger has seen the workshop's current state before the result is judged against it.
        PollWorkshop(force: true);

        var capture = WorkshopReader.ReadVoyageResult(trigger);
        if (capture is null)
        {
            plugin.Log.Verbose(Source, $"Result seen via {trigger}, but no submarine result was readable yet.");
            return;
        }

        plugin.Log.Debug(Source, $"Result seen via {trigger}: '{capture.SubName}' {capture.Fingerprint()}");
        plugin.Worker.Enqueue("Record voyage", () => plugin.RecordCapture(capture));
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            PollWorkshop(force: false);
        }
        catch (Exception ex)
        {
            plugin.Log.Error(Source, "Reading the workshop failed", ex);
            nextPoll = DateTime.UtcNow.AddSeconds(30);
        }
    }

    /// <summary>
    /// While in a workshop, twice a second: notes each submarine's name, rank and scheduled return. The return time
    /// is what later tells one voyage from the next, and the game clears it just before showing a result.
    /// </summary>
    private void PollWorkshop(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && now < nextPoll)
            return;
        // Often enough that a return time is seen before a fast collect-and-redeploy plugin clears it.
        nextPoll = now.AddMilliseconds(100);

        if (!Service.ClientState.IsLoggedIn || !WorkshopReader.IsInWorkshop())
        {
            if (wasInWorkshop)
            {
                // Whatever happens to the submarines while away was not watched, so the next visit starts fresh:
                // a return time found already set then is not mistaken for a submarine leaving "just now".
                wasInWorkshop = false;
                sessionReturn.Clear();
                identityFc = 0;
                plugin.Log.Verbose(Source, "Left the workshop.");
            }

            return;
        }

        var fcId = WorkshopReader.CurrentFreeCompanyId();
        if (fcId == 0)
            return;

        var nowUnix = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds();
        if (!wasInWorkshop)
        {
            wasInWorkshop = true;
            plugin.Log.Verbose(Source, "Entered a workshop; watching submarines.");
        }

        if (identityFc != fcId)
        {
            identityFc = fcId;
            NoteIdentity(fcId, nowUnix);
        }

        foreach (var seen in WorkshopReader.ReadSubmarines(fcId, nowUnix))
        {
            var key = (seen.FcId, seen.Register);
            var known = sessionReturn.TryGetValue(key, out var previous);
            if (known && previous == seen.ReturnUnix)
                continue;

            sessionReturn[key] = seen.ReturnUnix;

            // Watching the return time change, rather than finding it already set, means the submarine left just now.
            var observation = seen with { LiveDispatch = known && seen.ReturnUnix != 0 };
            plugin.Worker.Enqueue("Note submarine", () =>
            {
                if (!plugin.Store.ObserveSubmarine(observation))
                    return;

                var when = observation.ReturnUnix == 0
                    ? "docked"
                    : $"returns {DateTimeOffset.FromUnixTimeSeconds(observation.ReturnUnix).LocalDateTime:g}";
                plugin.Log.Debug(Source, $"Submarine '{observation.Name}' (rank {observation.Rank}): {when}{(observation.LiveDispatch ? ", just sent out" : string.Empty)}.");
            });
        }
    }

    private void NoteIdentity(ulong fcId, long nowUnix)
    {
        var name = WorkshopReader.CurrentFreeCompanyName();
        var tag = Service.ObjectTable.LocalPlayer?.CompanyTag.TextValue ?? string.Empty;
        var world = string.Empty;
        var character = string.Empty;
        ulong contentId = 0;

        if (Service.PlayerState.IsLoaded)
        {
            contentId = Service.PlayerState.ContentId;
            character = Service.PlayerState.CharacterName;
            world = Service.PlayerState.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty;
        }

        plugin.Worker.Enqueue("Note Free Company", () =>
        {
            if (plugin.Store.ObserveFreeCompany(fcId, name, tag, world, nowUnix))
                plugin.Log.Info(Source, $"Free Company noted: {(string.IsNullOrEmpty(name) ? tag : name)}{(string.IsNullOrEmpty(world) ? string.Empty : $" ({world})")}.");
            plugin.Store.ObserveCharacter(contentId, character, world, fcId, nowUnix);
        });
    }
}
