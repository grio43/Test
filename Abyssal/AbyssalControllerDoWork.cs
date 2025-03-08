//
// (c) duketwo 2022
//

extern alias SC;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EVESharpCore.Cache;
using EVESharpCore.Controllers.ActionQueue.Actions.Base;
using EVESharpCore.Controllers.Base;
using EVESharpCore.Controllers.Questor.Core.States;
using EVESharpCore.Framework;
using EVESharpCore.Framework.Events;
using EVESharpCore.Framework.Lookup;
using SC::SharedComponents.EVE;
using SC::SharedComponents.EVE.ClientSettings;
using SC::SharedComponents.EVE.DatabaseSchemas;
using SC::SharedComponents.Extensions;
using SC::SharedComponents.IPC;
using SC::SharedComponents.SQLite;
using SC::SharedComponents.Utility;
using ServiceStack.OrmLite;
using SC::SharedComponents.Events;
using SharpDX.Direct2D1;
using SC::SharedComponents.EVE.ClientSettings.Abyssal.Main;
using System.Diagnostics;
using System.Windows.Controls;

namespace EVESharpCore.Controllers.Abyssal
{
    public partial class AbyssalController : AbyssalBaseController
    {


        private DirectBookmark FilamentSpotBookmark
        {
            get
            {
                return ESCache.Instance.DirectEve.Bookmarks.OrderByDescending(e => e.IsInCurrentSystem).OrderByDescending(e => e.LocationId == _homeSystemId).FirstOrDefault(b => b.Title == _filamentSpotBookmarkName);
            }
        }

        private bool AreWeAtTheFilamentSpot
        {
            get
            {
                var bm = FilamentSpotBookmark;

                if (bm != null && !bm.IsInCurrentSystem)
                    return false;

                if (ESCache.Instance.InSpace && bm != null && bm.DistanceTo(ESCache.Instance.ActiveShip.Entity) < 149_000)
                    return true;
                return false;
            }
        }

        private bool ShouldWeGoIdle()
        {
            if (ManualPause)
            {
                if (DirectEve.Interval(15000)) Log($"ManualPause is active, going idle.");
                return true;
            }
            var spanTotalSeconds = (DateTime.UtcNow - _abyssalControllerStarted).TotalSeconds;
            Log($"_abyssalControllerStarted [{_abyssalControllerStarted}] spanTotalSeconds [{spanTotalSeconds}]");

            if (spanTotalSeconds < 600.0d)
                return false;

            if (_sessionChangeIdleCheck)
                return false;

            var idleDurationMin = 60;
            var idleDurationMax = 240;
            _sessionChangeIdleCheck = true;


            var rnd = Rnd.NextDouble();

            if (rnd >= 0.20d)
            {

                rnd = Rnd.NextDouble();

                if (rnd >= 0.40d)
                {
                    _idleUntil = DateTime.UtcNow.AddSeconds(Rnd.Next(idleDurationMin, idleDurationMax));
                }
                else
                {
                    _idleUntil = DateTime.UtcNow.AddSeconds(Rnd.Next(idleDurationMin / 2, idleDurationMax / 2));
                }
                return true;
            }
            else if (rnd >= 0.10d)
            {
                _idleUntil = DateTime.UtcNow.AddSeconds(Rnd.Next(idleDurationMin * 4, idleDurationMax * 4));
                return true;
            }
            else
            {
                return false;
            }
        }

        private bool _sessionChangeIdleCheck = false;
        private DateTime _idleUntil = DateTime.MinValue;
        private int _activationErrorTickCount = 0;
        private DateTime _nextActionAfterAbyTraceDespawn = DateTime.MinValue;
        private bool _leftInvulnAfterAbyssState = false;
        private bool _abandoningDrones = false;
        private string _targetPrioCache;
        private int _fleetInvAttempts;
        public bool ManualPause { get; set; } = false;

        public override void DoWork()
        {

            if (DroneDebugState && Framework.Session.IsInSpace && !Framework.Me.IsInAbyssalSpace())
            {
                var dronesIWant = GetWantedDronesInSpace();

                Log($"-- DronesIWant --");
                foreach (var drone in dronesIWant)
                {
                    Log($"TypeName {drone.TypeName}");
                }
                Log($"-- DronesIWant -- End");

                if (LaunchDrones(dronesIWant))
                    return;

                if (ReturnDrones(dronesIWant))
                    return;

                return;
            }

            // Ensure DPS values are populated within the game (mutated drones). If we don't check before they are launched, they have no dps value while in space. So we need to check while they are still in bay.
            if (!_droneDPSUpdate)
            {
                if (!allDronesInSpace.Any())
                {
                    var droneBay = Framework.GetShipsDroneBay();
                    if (droneBay != null)
                    {
                        if (!droneBay.Items.Any())
                            _droneDPSUpdate = true;

                        foreach (var d in Framework.GetShipsDroneBay()?.Items)
                        {
                            var k = d.GetDroneDPS();
                        }
                        _droneDPSUpdate = true;
                    }
                }
            }

            Stopwatch sw = new Stopwatch();
            bool logDurations = false;
            sw.Start();
            try
            {
                DoOnceOnStartup();

                if (ESCache.Instance.State.CurrentTravelerState == TravelerState.Error && this.State != AbyssalState.Error)
                {
                    Log($"Traveller error state.");
                    this.State = AbyssalState.Error;
                }

                if (DirectEve.Interval(1500, 2500))
                {
                    int currentAbyssStage = (int)CurrentAbyssalStage;
                    // Update current abyss stage to be able to recover from a crash/disconnect
                    Task.Run(() =>
                    {
                        try
                        {
                            if (ESCache.Instance.EveAccount.AbyssStage != currentAbyssStage)
                                WCFClient.Instance.GetPipeProxy.SetEveAccountAttributeValue(ESCache.Instance.CharName, nameof(EveAccount.AbyssStage), currentAbyssStage);
                        }
                        catch (Exception ex)
                        {
                            Log(ex.ToString());
                        }
                    });
                }

                if (DirectEve.Me.IsInAbyssalSpace() && State != AbyssalState.AbyssalEnter)
                    State = AbyssalState.AbyssalClear;

                // Set the PVP state if players are attacking us
                if (Framework.Session.IsInSpace && IsAnyPlayerAttacking)
                {
                    State = AbyssalState.PVP;
                }

                // Handle responsive mode while being engaged in PVP
                if (true)
                {
                    if (State == AbyssalState.PVP && ControllerManager.Instance.ResponsiveMode == false)
                    {
                        ControllerManager.Instance.ResponsiveMode = true;
                        Log($"Set ControllerManager ResponsiveMode to TRUE.");
                    }

                    if (State != AbyssalState.PVP && ControllerManager.Instance.ResponsiveMode == true)
                    {
                        ControllerManager.Instance.ResponsiveMode = false;
                        Log($"Set ControllerManager ResponsiveMode to FALSE.");
                    }
                }

                if (ESCache.Instance.DirectEve.Me.IsInvuln && AreWeAtTheFilamentSpot && State != AbyssalState.InvulnPhaseAfterAbyssExit && State != AbyssalState.PVP && _leftInvulnAfterAbyssState == false)
                {
                    if (DirectEve.Interval(1000))
                    {
                        Log($"We are at the abyss filament spot and we are invulnerable, changing state to [{nameof(AbyssalState.InvulnPhaseAfterAbyssExit)}]");
                    }
                    State = AbyssalState.InvulnPhaseAfterAbyssExit;
                    return;
                }

                if (DirectEve.ActiveShip?.GroupId == (int)Group.Capsule)
                {

                    if (ESCache.Instance.DirectEve.Me.IsInAbyssalSpace() && IsAbyssGateOpen)
                    {
                        // Move to the gate and jump
                        if (DirectEntity.MoveToViaAStar(3000, forceRecreatePath: forceRecreatePath, dest: _nextGate.DirectAbsolutePosition, ignoreAbyssEntities: true))
                        {
                            if (DirectEve.Interval(1500, 2500))
                                if (_nextGate.Distance >= 2500)
                                {
                                    Log("We are in a capsule and the abyss gate is open, moving to the gate and trying to jump.");
                                    _nextGate.MoveTo();
                                }
                                else
                                {
                                    if (_isInLastRoom)
                                        _nextGate.ActivateAbyssalEndGate();
                                    else
                                        _nextGate.ActivateAbyssalAccelerationGate();
                                }
                        }
                        return;
                    }

                    if (DirectEve.Me.IsInAbyssalSpace()) // Can't to jackshit there while in a capsule ---> TODO: We can move, maybe we can safe our pod? (very rare occurrence tho, except single room abysses)
                        return;

                    if (DirectEve.Interval(60000) && _abyssStatEntry != null) // Write the stats after we got kicked out of the abyss
                    {
                        Log($"Yaaay. Congratulations! We are in a capsule.");
                        Log($"Writing stats entry. :(");
                        _abyssStatEntry.Died = true;
                        WriteStatsToDB();
                        _abyssStatEntry = null;
                    }

                    if (ESCache.Instance.DirectEve.Session.IsInSpace && State != AbyssalState.TravelToHomeLocation && State != AbyssalState.PVP) // if we somehow managed to escape with a pod, let's safe it
                    {
                        // TODO: What do we do about the agression timer (i.e can we dock directly after someone popped our ship?) Edit: We can't dock 10 seconds after we got popped (session change timer, nothing else)
                        // TODO: This sucks, the traveler is very slow, we need to warp to a celesital before
                        Log($"Trying to save our pod. Going to the home station bookmark.");
                        State = AbyssalState.TravelToHomeLocation;
                        ESCache.Instance.Traveler.Destination = null;
                        ESCache.Instance.State.CurrentTravelerState = TravelerState.Idle;
                    }

                    if (ESCache.Instance.DirectEve.Session.IsInDockableLocation)
                    {
                        Log("We are docked and in a capsule, disabling this instance.");
                        ControllerManager.Instance.SetPause(true);
                        ESCache.Instance.DisableThisInstance();
                        return;
                    }

                }

            }
            finally
            {
                if (sw.ElapsedMilliseconds != 0 && logDurations)
                {
                    Log($"Elapsed (ms) [DoWork-Init]: [{sw.ElapsedMilliseconds}]");
                }
                sw.Restart();
            }

            try
            {
                if (ManageOverheat())
                    return;
            }
            finally
            {
                if (sw.ElapsedMilliseconds != 0 && logDurations)
                {
                    Log($"Elapsed (ms) [ManageOverheat]: [{sw.ElapsedMilliseconds}]");
                }
                sw.Restart();
            }

            try
            {

                if (ManageModules())
                    return;
            }
            finally
            {
                if (sw.ElapsedMilliseconds != 0 && logDurations)
                {
                    Log($"Elapsed (ms) [ManageModules]: [{sw.ElapsedMilliseconds}]");
                }
                sw.Restart();
            }

            try
            {
                if (ManageDrugs())
                    return;
            }
            finally
            {
                if (sw.ElapsedMilliseconds != 0 && logDurations)
                {
                    Log($"Elapsed (ms) [ManageDrugs]: [{sw.ElapsedMilliseconds}]");
                }
                sw.Restart();
            }


            switch (State)
            {
                case AbyssalState.Error:

                    DirectEventManager.NewEvent(new DirectEvent(DirectEvents.KEEP_ALIVE, "Abyss error state KEEP_ALIVE."));

                    if (DirectEve.Interval(30000))
                        Log($"Abyss error state. Docked [{Framework.Session.IsInDockableLocation}]");

                    if (DirectEve.Interval(480000))
                    {
                        try
                        {
                            DirectEventManager.NewEvent(new DirectEvent(DirectEvents.ERROR, $"Abyssal error state. Current ship typename: [{Framework.ActiveShip.TypeName}] Docked [{Framework.Session.IsInDockableLocation}]"));
                        }
                        catch { }
                    }

                    if (Framework.Session.IsInDockableLocation)
                        return;

                    if (ESCache.Instance.InSpace)
                    {
                        if (ESCache.Instance.State.CurrentTravelerState != TravelerState.AtDestination)
                        {
                            ESCache.Instance.Traveler.TravelToBookmark(ESCache.Instance.DirectEve.Bookmarks.OrderByDescending(e => e.IsInCurrentSystem).OrderByDescending(e => e.LocationId == _homeSystemId).FirstOrDefault(b => b.Title == _homeStationBookmarkName));
                        }
                        else
                        {
                            ESCache.Instance.Traveler.Destination = null;
                            ESCache.Instance.State.CurrentTravelerState = TravelerState.Idle;
                            Log($"Arrived at the home station.");
                            State = AbyssalState.Error;
                        }
                    }

                    break;

                case AbyssalState.InvulnPhaseAfterAbyssExit:

                    _leftInvulnAfterAbyssState = true;
                    if (IsAnyOtherNonFleetPlayerOnGrid || !CanAFilamentBeOpened(true))
                    {
                        State = AbyssalState.PVP;
                    }
                    else
                    {
                        State = AbyssalState.Start;
                    }
                    break;

                case AbyssalState.PVP:
                    PVPState();
                    break;
                case AbyssalState.Start:
                    if (ManualPause)
                    {
                        Log($"ManualPause is active, We should arm to go back to station");
                        State = AbyssalState.Arm;
                        break;
                    }

                    var shipCargoCont = DirectEve.GetShipsCargo();
                    var needRepair = NeedRepair();

                    if (AreWeAtTheFilamentSpot && !NeedRepair(true) && !IsAnyOtherNonFleetPlayerOnGrid)
                    {
                        // If we are at the spot with damaged modules and have nanite in the cargo, repair it!
                        if (!ESCache.Instance.DirectEve.Modules.All(m => m.HeatDamagePercent == 0)
                            && ESCache.Instance.DirectEve.Modules.All(m => m.HeatDamagePercent <= 99)
                            && shipCargoCont.Items.Any(i => i.TypeId == _naniteRepairPasteTypeId && i.Stacksize > 30))
                        {
                            foreach (var mod in DirectEve.Modules.Where(m => m.HeatDamagePercent > 0 && !m.IsBeingRepaired))
                            {

                                if ((mod.IsInLimboState || mod.IsActive) && mod.IsActivatable)
                                {
                                    if (!mod.IsDeactivating)
                                    {
                                        if (DirectEve.Interval(1500, 2200))
                                        {
                                            Log($"Deactivating TypeName[{mod.TypeName}].");
                                            mod.Click();
                                        }
                                    }
                                    return;
                                }

                                if (!mod.IsBeingRepaired)
                                {
                                    // repair
                                    if (mod.Repair())
                                    {
                                        Log($"Repairing TypeName[{mod.TypeName}].");
                                        continue;
                                    }
                                }
                            }

                            if (DirectEve.Interval(5000))
                            {
                                Log($"Waiting for repairs to finish.");
                            }

                            return;
                        }
                    }

                    if (ESCache.Instance.InSpace && !needRepair)
                    {
                        if (FilamentSpotBookmark == null)
                        {
                            Log($"Filamentspot bookmark is null. Error.");
                            State = AbyssalState.Error;
                            return;
                        }

                        if (AreWeAtTheFilamentSpot)
                        {
                            // Check cargo space
                            Log($"CargoCapacity: {shipCargoCont.Capacity} UsedCapacity {shipCargoCont.UsedCapacity}");
                            if (shipCargoCont.Capacity - shipCargoCont.UsedCapacity >= shipCargoCont.Capacity * 0.25)
                            {
                                var shipBayItemCheck = true;
                                foreach (var t in _shipsCargoBayList)
                                {

                                    if (t.Item1 == _filamentTypeId)
                                    {
                                        if (
                                            (_activeShip.IsFrigate || _activeShip.IsDestroyer)
                                            &&
                                            (!shipCargoCont.Items.Any(e => e.TypeId == _filamentTypeId)
                                            || shipCargoCont.Items.Where(e => e.TypeId == _filamentTypeId).OrderByDescending(e => e.Stacksize).FirstOrDefault().Stacksize < _filaStackSize)
                                            )
                                        {
                                            Log($"Not enough filaments in cargo. Going to re-arm.");
                                            shipBayItemCheck = false;
                                        }

                                    }

                                    if (t.Item1 == _ammoTypeId)
                                    {
                                        var ammoThreshold = t.Item2 * 0.2d;

                                        if (ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.ShipType == AbyssShipType.Cerberus)
                                            ammoThreshold = (6 / 2.2) * 60 * 20;

                                        if (!shipCargoCont.Items.Any(e => e.TypeId == _ammoTypeId) || shipCargoCont.Items.Where(e => e.TypeId == _ammoTypeId).Sum(e => e.Stacksize) < ammoThreshold)
                                        {
                                            Log($"Not enough ammo left in the cargo. We need {ammoThreshold} [{DirectEve.GetInvType(t.Item1).TypeName}]");
                                            shipBayItemCheck = false;
                                        }
                                    }

                                    if (!shipCargoCont.Items.Any(e => e.TypeId == t.Item1))
                                    {
                                        shipBayItemCheck = false;
                                        Log($"ShipBayItemCheck missing the following item [{t.Item1}] TypeName [{ESCache.Instance.DirectEve.GetInvType(t.Item1).TypeName}], going back to the base");
                                        break;
                                    }
                                }

                                if (shipBayItemCheck)
                                {
                                    var db = DirectEve.GetShipsDroneBay(); // TODO: Fix me - what does this return on ships with no drone bay?
                                    var remainingCap = db.Capacity - db.UsedCapacity;

                                    if (db.Capacity == 0d || remainingCap <= 5)
                                    {
                                        Log($"We don't need to repair and have enough of the required cargo items / drones in the bays / no drone bay.");
                                        State = AbyssalState.TravelToFilamentSpot;
                                        return;
                                    }
                                    else
                                    {
                                        Log($"We lost too many drones, going back to the base.");
                                    }
                                }
                            }
                            else
                            {
                                Log($"There is not enough cargo space left, going back to the base.");
                            }
                        }
                    }

                    if (ESCache.Instance.InSpace && needRepair)
                    {
                        Log($"Looks like we need to repair, going to the station.");
                    }
                    State = AbyssalState.Arm;
                    break;
                case AbyssalState.IdleInStation:
                    if (ManualPause)
                    {
                        if (DirectEve.Interval(15000))
                        {
                            Log($"ManualPause is active, staying idle.");
                        }
                        break;
                    }

                    if (DirectEve.Interval(15000))
                    {
                        Log($"Idle in station until [{_idleUntil}].");
                    }

                    if (_idleUntil <= DateTime.UtcNow)
                    {
                        State = AbyssalState.Arm;
                    }

                    break;
                case AbyssalState.Arm:


                    var homeBm = ESCache.Instance.DirectEve.Bookmarks.OrderByDescending(e => e.IsInCurrentSystem).OrderByDescending(e => e.LocationId == _homeSystemId).FirstOrDefault(b => b.Title == _homeStationBookmarkName);
                    if (homeBm == null)
                    {
                        Log($"Home bookmark name not found Error.");
                        State = AbyssalState.Error;
                        return;
                    }

                    if (homeBm.ItemId != ESCache.Instance.DirectEve.Session.LocationId)
                    {
                        Log($"We are not in the home station, travelling to the home station");
                        State = AbyssalState.TravelToHomeLocation;
                        ESCache.Instance.Traveler.Destination = null;
                        ESCache.Instance.State.CurrentTravelerState = TravelerState.Idle;
                        return;
                    }

                    if (!ESCache.Instance.DirectEve.Session.IsInDockableLocation)
                    {
                        Log($"Error: Not in dockable location during arm.");
                        State = AbyssalState.Error;
                        return;
                    }

                    // Here we are in the home station

                    if (_boosterFailedState)
                    {
                        Log($"Error: Booster failed state, changing to error state.");
                        State = AbyssalState.Error;
                        return;
                    }

                    var shipHangar = ESCache.Instance.DirectEve.GetShipHangar();

                    if (shipHangar == null)
                        return;

                    // Activate the correct ship if necessary
                    if (ESCache.Instance.DirectEve.ActiveShip.TypeId != _shipTypeId)
                    {
                        Log($"Activating ship [{_shipTypeId}] TypeName [{ESCache.Instance.DirectEve.GetInvType(_shipTypeId).TypeName}].");
                        var ship = ESCache.Instance.DirectEve.GetShipHangar().Items.FirstOrDefault(e => e.TypeId == _shipTypeId && e.IsSingleton);
                        if (ship != null)
                        {
                            ship.ActivateShip();
                            Log($"Ship activated.");
                            LocalPulse = UTCNowAddMilliseconds(1500, 3500);
                            return;
                        }
                        else
                        {
                            Log($"Ship type was not found in the ship hangar.");
                            State = AbyssalState.Error;
                            return;
                        }
                    }

                    var hangar = ESCache.Instance.DirectEve.GetItemHangar();
                    var shipsCargo = ESCache.Instance.DirectEve.GetShipsCargo();
                    var droneBay = ESCache.Instance.DirectEve.GetShipsDroneBay();

                    if (hangar == null || shipsCargo == null || droneBay == null)
                        return;

                    if (NeedToDumpDatabaseSurveys())
                    {
                        State = AbyssalState.DumpSurveyDatabases;
                        return;
                    }

                    if (DoWeNeedToBuyItems)
                    {
                        State = AbyssalState.BuyItems;
                        return;
                    }

                    if (ShouldWeGoIdle())
                    {
                        State = AbyssalState.IdleInStation;
                        return;
                    }

                    var seconds = 25200;
                    var env = Environment.GetEnvironmentVariable("ES_MAX_S", EnvironmentVariableTarget.User);
                    if (env != null && int.TryParse(env, out seconds)) { }

                    if (ESCache.Instance.EveAccount.AbyssSecondsDaily > seconds)
                    {

                        //Log($"Calm down miner. You've been running today for more than [{Math.Round(TimeSpan.FromSeconds(ESCache.Instance.EveAccount.AbyssSecondsDaily).TotalHours, 2)}] hours today.");
                        State = AbyssalState.Error;
                        return;
                    }

                    if (hangar.Items.Count > _itemHangarTrashItemsThreshold)
                    {
                        Log($"Itemhangar is full, more than [{_itemHangarTrashItemsThreshold}] items. Trying to trash the BPCs.");
                        State = AbyssalState.TrashItems;
                        return;
                    }

                    // Check if there is any other type loaded in the drone bay, clear the bay if that is the case. 
                    if (!droneBay.Items.All(d => _droneBayItemList.Any(e => (e.Item1 == d.TypeId && !e.Item4) || (d.IsDynamicItem && e.Item4 && d.OrignalDynamicItem.TypeId == e.Item1))))
                    {
                        Log($"Wrong amount or unknown type found in the drone bay. Moving dronebay items to the itemhangar.");
                        hangar.Add(droneBay.Items);
                        LocalPulse = DateTime.UtcNow.AddSeconds(3);
                        return;
                    }

                    // Check if there is any other type loaded in the ships cargo bay, clear the bay if that is the case
                    if (!shipsCargo.Items.All(d => _shipsCargoBayList.Any(e => e.Item1 == d.TypeId)))
                    {
                        Log($"Unknown type found in ships cargo bay. Moving ships cargo bay items to the itemhangar");
                        var items = shipsCargo.Items;
                        items.RemoveAll(x => _shipsCargoBayList.Any(e => e.Item1 == x.TypeId));
                        hangar.Add(items);
                        LocalPulse = DateTime.UtcNow.AddSeconds(3);
                        return;
                    }

                    // Iterate over drones and check if they are enough in the dronebay
                    foreach (var t in _droneBayItemList.RandomPermutation()) // Change the order
                    {
                        var typeId = t.Item1;
                        var amount = t.Item2;
                        var mutated = t.Item4;

                        Log($"[ARM-DEBUG] Processing DroneBayItem: TypeId={typeId}, Amount={amount}, Mutated={mutated}, TypeName={DirectEve.GetInvType(typeId)?.TypeName}"); // ADDED LOGGING

                        int missingInDroneBay = 0;
                        int availInHangar = 0;

                        if (!mutated)
                        {
                            Log($"[ARM-DEBUG]  -- Non-Mutated Drone Logic --"); // ADDED LOGGING
                            missingInDroneBay = amount - droneBay.Items.Where(d => !d.IsDynamicItem && d.TypeId == typeId).Sum(d => d.Stacksize);
                            availInHangar = hangar.Items.Where(d => !d.IsDynamicItem && d.TypeId == typeId).Sum(d => d.Stacksize);
                        }
                        else
                        {
                            Log($"[ARM-DEBUG]  -- Mutated Drone Logic --"); // ADDED LOGGING
                            missingInDroneBay = amount - droneBay.Items.Where(d => d.IsDynamicItem && d.OrignalDynamicItem.TypeId == typeId).Sum(d => d.Stacksize);
                            availInHangar = hangar.Items.Where(d => d.IsDynamicItem && d.OrignalDynamicItem.TypeId == typeId).Sum(d => d.Stacksize);
                        }

                        Log($"[ARM-DEBUG]  -- Counts -- MissingInDroneBay={missingInDroneBay}, AvailInHangar={availInHangar}"); // ADDED LOGGING

                        //Log($"TypeId [{typeId}] AvaiableInHangar [{avaiableInHangar}] missingInDroneBay [{missingInDroneBay}]");

                        if (missingInDroneBay > availInHangar)
                        {
                            // ... Not enough available
                            Log($"Error: Not enough drones left available. TypeId [{typeId}] TypeName [{DirectEve.GetInvType(typeId)?.TypeName}] AvaiableInHangar [{availInHangar}] missingInDroneBay [{missingInDroneBay}]");
                            State = AbyssalState.Error;
                            return;
                        }

                        // ... (rest of the loading logic)
                    }

                    // Iterate over cargobay item list
                    foreach (var t in _shipsCargoBayList.RandomPermutation()) // Change the order
                    {
                        var typeId = t.Item1;
                        var amount = t.Item2;

                        var missingInShipsCargo = amount - shipsCargo.Items.Where(d => d.TypeId == typeId).Sum(d => d.Stacksize);
                        var avaiableInHangar = hangar.Items.Where(d => d.TypeId == typeId).Sum(d => d.Stacksize);


                        //Log($"TypeId [{typeId}] AvaiableInHangar [{avaiableInHangar}] missingInShipsCargo [{missingInShipsCargo}]");

                        if (missingInShipsCargo > avaiableInHangar)
                        {
                            // ... Not enough available 
                            Log($"Error: Missing type in the hangar. TypeId [{typeId}] TypeName [{DirectEve.GetInvType(typeId)?.TypeName}] AvaiableInHangar [{avaiableInHangar}] missingInShipsCargo [{missingInShipsCargo}]");
                            State = AbyssalState.Error;
                            return;
                        }

                        if (missingInShipsCargo > 0)
                        {
                            // Move them
                            var item = hangar.Items.Where(d => d.TypeId == typeId).OrderByDescending(d => d.Stacksize).FirstOrDefault();
                            shipsCargo.Add(item, Math.Min(item.Stacksize, missingInShipsCargo));
                            LocalPulse = UTCNowAddMilliseconds(500, 1500);
                            return;
                        }
                    }

                    // At this point we are ready to go
                    Log("Arm finished!.");
                    if (hangar.CanBeStacked)
                    {
                        Log("Stacking item hangar.");
                        hangar.StackAll();
                        LocalPulse = DateTime.UtcNow.AddSeconds(3);
                        return;
                    }

                    if (!droneBay.Items.All(d =>
                        {
                            // Check if there exists an expected item that matches both TypeId and dynamic status
                            var expectedItem = _droneBayItemList.FirstOrDefault(e =>
                                    e.Item4 == d.IsDynamicItem && // Match dynamic status
                                    e.Item1 == (d.IsDynamicItem ? d.OrignalDynamicItem.TypeId : d.TypeId) // Match TypeId based on dynamic status
                            );

                            if (expectedItem == default)
                                return false; // No matching expected item

                            // Calculate the total stack size for items of the same dynamic status and TypeId
                            int actualTotal = droneBay.Items
                                .Where(e =>
                                        e.IsDynamicItem == d.IsDynamicItem && // Same dynamic status
                                        (e.IsDynamicItem ? e.OrignalDynamicItem.TypeId : e.TypeId) ==
                                        (d.IsDynamicItem ? d.OrignalDynamicItem.TypeId : d.TypeId) // Same TypeId logic
                                )
                                .Sum(e => e.Stacksize);

                            return expectedItem.Item2 == actualTotal;
                        }))
                    {
                        Log($"Wrong amount or unknown type found in the drone bay. Moving dronebay items to the item hangar.");
                        hangar.Add(droneBay.Items);
                        LocalPulse = DateTime.UtcNow.AddSeconds(3);
                        return;
                    }


                    State = AbyssalState.TravelToRepairLocation;
                    ESCache.Instance.Traveler.Destination = null;
                    ESCache.Instance.State.CurrentTravelerState = TravelerState.Idle;

                    break;
                case AbyssalState.TravelToFilamentSpot:

                    if (ESCache.Instance.InWarp)
                    {
                        if (ESCache.Instance.GroupWeapons())
                        {
                            Log($"Grouped weapons");
                        }
                    }

                    if (FilamentSpotBookmark == null)
                    {
                        Log($"Filamentspot bookmark is null. Error.");
                        State = AbyssalState.Error;
                        return;
                    }

                    if (ESCache.Instance.State.CurrentTravelerState != TravelerState.AtDestination)
                    {
                        ESCache.Instance.Traveler.TravelToBookmark(FilamentSpotBookmark, 0, true);
                    }
                    else
                    {
                        ESCache.Instance.Traveler.Destination = null;
                        ESCache.Instance.State.CurrentTravelerState = TravelerState.Idle;
                        Log($"Arrived at the filament spot.");
                        State = AbyssalState.UseFilament;
                    }

                    break;
                case AbyssalState.TravelToBuyLocation:
                    break;
                case AbyssalState.TravelToHomeLocation:

                    var hbm = ESCache.Instance.DirectEve.Bookmarks.OrderByDescending(e => e.IsInCurrentSystem).OrderByDescending(e => e.LocationId == _homeSystemId).FirstOrDefault(b => b.Title == _homeStationBookmarkName);
                    if (ESCache.Instance.State.CurrentTravelerState != TravelerState.AtDestination)
                    {
                        ESCache.Instance.Traveler.TravelToBookmark(hbm);
                    }
                    else
                    {
                        ESCache.Instance.Traveler.Destination = null;
                        ESCache.Instance.State.CurrentTravelerState = TravelerState.Idle;
                        Log($"Arrived at the home station.");
                        State = AbyssalState.Arm;
                    }

                    break;

                case AbyssalState.TravelToRepairLocation:

                    if (ESCache.Instance.InSpace && !NeedRepair())
                    {
                        Log($"Apparently we don't need to repair, skipping repair.");
                        State = AbyssalState.TravelToFilamentSpot;
                        ESCache.Instance.Traveler.Destination = null;
                        ESCache.Instance.State.CurrentTravelerState = TravelerState.Idle;
                        return;
                    }

                    var rbm = ESCache.Instance.DirectEve.Bookmarks.OrderByDescending(e => e.IsInCurrentSystem).OrderByDescending(e => e.LocationId == _homeSystemId).FirstOrDefault(b => b.Title == _repairLocationBookmarkName);
                    if (ESCache.Instance.State.CurrentTravelerState != TravelerState.AtDestination)
                    {
                        ESCache.Instance.Traveler.TravelToBookmark(rbm);
                    }
                    else
                    {
                        ESCache.Instance.Traveler.Destination = null;
                        ESCache.Instance.State.CurrentTravelerState = TravelerState.Idle;
                        Log($"Arrived at the repair location.");
                        State = AbyssalState.RepairItems;
                    }

                    break;
                case AbyssalState.RepairItems:

                    if (!ESCache.Instance.RepairItems())
                        return;

                    ESCache.Instance.Traveler.Destination = null;
                    ESCache.Instance.State.CurrentTravelerState = TravelerState.Idle;
                    State = AbyssalState.TravelToFilamentSpot;
                    Log($"Repair done.");

                    break;
                case AbyssalState.ReplaceShip:
                    break;
                case AbyssalState.ActivateShip:

                    break;
                case AbyssalState.UseFilament:

                    // Ensure we are in a fleet if we don't use the abyssalguard controller and are in a frigate or in a destroyer
                    if (_fleetInvAttempts > 5)
                    {
                        // change to error state
                        Log($"Error: Fleet invite attempts exceeded.");
                        State = AbyssalState.Error;
                        return;
                    }

                    if (DateTime.UtcNow.Hour == 10 && DateTime.UtcNow.Minute >= 37 || DateTime.UtcNow.Hour == 11 && DateTime.UtcNow.Minute <= 01)
                    {
                        Log($"Error: Time is between 10:37 and 11:01 UTC. Going back to base.");
                        State = AbyssalState.Error;
                        return;
                    }

                    if (String.IsNullOrEmpty(ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.AbyssalGuardCharacterName))
                    {
                        if (_activeShip.IsFrigate || _activeShip.IsDestroyer)
                        {
                            if (!Framework.IsInFleet)
                            {
                                if (DirectEve.Interval(3000, 5000))
                                {
                                    Log($"We are not in a fleet, creating a fleet.");
                                    Framework.FormFleetWithSelf();
                                    _fleetInvAttempts++;
                                }
                                return;
                            }
                        }
                    }

                    _fleetInvAttempts = 0;

                    if (DirectEve.Session.IsInDockableLocation)
                    {
                        if (DirectEve.Interval(3000, 5000))
                        {
                            Log($"Can't use a filament in a station");
                        }
                        return;
                    }

                    if (ESCache.Instance.DirectEve.ActiveShip.TypeId != _shipTypeId)
                    {
                        Log($"You are trying to run this in a wrong ship.");
                        State = AbyssalState.Error;
                        return;
                    }

                    if (IsAnyOtherNonFleetPlayerOnGrid)
                    {
                        Log($"Error: There is another player on our abyss safespot, going to the base.");
                        foreach (var p in ESCache.Instance.EntitiesNotSelf.Where(e => e.IsPlayer && e.Distance < 1000000))
                        {
                            Log($"Name [{p.Name}] TypeName [{p.TypeName}] Distance [{p.Distance}]");
                        }
                        State = AbyssalState.Error;
                        return;
                    }

                    if (DirectEve.Me.IsInAbyssalSpace())
                    {
                        if (DirectEve.Interval(200, 5000))
                        {
                            State = AbyssalState.AbyssalClear;
                            return;
                        }
                    }

                    if (DirectEve.Entities.Any(e => e.Distance < 1000000 && e.GroupId == (int)Group.AbyssalTrace))
                    {
                        if (DirectEve.Interval(4000, 6000))
                        {
                            Log($"Waiting for the old abyssal trace to fade away.");
                        }
                        _nextActionAfterAbyTraceDespawn = DateTime.UtcNow.AddMilliseconds(GetRandom(2500, 4500));
                        return;
                    }

                    if (_nextActionAfterAbyTraceDespawn > DateTime.UtcNow)
                    {
                        Log($"Waiting until [{_nextActionAfterAbyTraceDespawn}] to continue.");
                        return;
                    }

                    var currentShipCargo = DirectEve.GetShipsCargo();
                    if (currentShipCargo == null)
                        return;

                    var activationWnd = DirectEve.Windows.OfType<DirectKeyActivationWindow>().FirstOrDefault();
                    if (activationWnd != null)
                    {
                        Log($"Key activation window found.");
                        State = AbyssalState.AbyssalEnter;
                        LocalPulse = UTCNowAddMilliseconds(2500, 3000);
                        return;
                    }

                    if (currentShipCargo.Items.Any(e => e.TypeId == _filamentTypeId))
                    {
                        var filament = currentShipCargo.Items.OrderByDescending(e => e.Stacksize).FirstOrDefault(e => e.TypeId == _filamentTypeId);
                        if (filament.Stacksize < 2 && _activeShip.IsDestroyer || filament.Stacksize < 3 && _activeShip.IsFrigate)
                        {
                            Log($"Stacksize is too small. Stack size [{filament.Stacksize}] Going back to base to re-arm.");
                            State = AbyssalState.Start;
                            return;
                        }

                        if (DirectEve.Interval(1500, 4000))
                        {
                            Log($"Actvating abyssal key.");
                            filament.ActivateAbyssalKey();

                            if (ESCache.Instance.DirectEve.ActiveShip.Entity.Velocity > 10)
                            {
                                Log($"Stopping the ship.");
                                DirectEve.ExecuteCommand(DirectCmd.CmdStopShip);
                            }
                        }
                    }
                    else
                    {
                        Log($"Error: No filaments left. Changing state to start.");
                        State = AbyssalState.Start;
                    }

                    break;

                case AbyssalState.AbyssalEnter:

                    if (ESCache.Instance.Modules.Count(m => !m.IsOnline) > 0)
                    {
                        Log($"Error: Some modules are offline. Going back to base.");
                        State = AbyssalState.Error;
                        return;
                    }

                    DirectEntity.AStarErrors = 0;
                    activationWnd = DirectEve.Windows.OfType<DirectKeyActivationWindow>().FirstOrDefault();
                    if (activationWnd != null)
                    {
                        if (IsAnyOtherNonFleetPlayerOnGrid)
                        {
                            Log($"Error: There is another player on our abyss safespot, going to the base.");
                            foreach (var p in ESCache.Instance.EntitiesNotSelf.Where(e => e.IsPlayer && e.Distance < 1000000))
                            {
                                Log($"Name [{p.Name}] TypeName [{p.TypeName}] Distance [{p.Distance}]");
                            }
                            State = AbyssalState.Error;
                            return;
                        }

                        if (CanAFilamentBeOpened())
                        {
                            if (ESCache.Instance.DirectEve.Me.IsInvuln && !IsAnyOtherNonFleetPlayerOnGrid)
                            {
                                if (DirectEve.Interval(1500, 4000))
                                {
                                    //ESCache.Instance.DirectEve.ActiveShip.SetSpeedFraction(1.0f);
                                    //ESCache.Instance.Star.AlignTo();
                                    ESCache.Instance.ActiveShip.MoveToRandomDirection();
                                    Log($"Moving into a random direction to break the abyss invuln timer.");
                                    LocalPulse = UTCNowAddMilliseconds(1000, 2500);
                                    return;
                                }
                            }

                            if (activationWnd.AnyError)
                            {
                                _activationErrorTickCount++;
                                if (DirectEve.Interval(1500, 4000))
                                {
                                    Log($"There is an activation error. Waiting.");
                                    if (DirectEve.Interval(10000, 15000) && ESCache.Instance.DirectEve.ActiveShip.Entity.Velocity > 10)
                                    {
                                        Log($"Stopping the ship.");
                                        DirectEve.ExecuteCommand(DirectCmd.CmdStopShip);
                                    }
                                }

                                if (_activationErrorTickCount > 150)
                                {
                                    Log($"Error: _activationErrorTickCount > 150.");
                                    State = AbyssalState.Error;
                                    _activationErrorTickCount = 0;
                                }

                                return;
                            }

                            if (activationWnd.IsReady)
                            {
                                if (Framework.Modules.Any(m => !m.IsOnline))
                                {
                                    Log($"Error: Not all modules are online.");
                                    State = AbyssalState.Error;
                                    return;
                                }

                                Log($"Activation window is ready.");
                                if (DirectEve.Interval(1500, 4000) && activationWnd.Activate())
                                {
                                    _activationErrorTickCount = 0;
                                    Log($"Activating the filament.");
                                }
                            }
                            return;
                        }
                        else
                        {
                            Log($"Error: An entity on the grid is preventing us from opening a filament. Going to the home station.");
                            State = AbyssalState.Error;
                            _activationErrorTickCount = 0;
                            return;
                        }
                        return;
                    }

                    if (_attemptsToJumpFrigateDestroyerAbyss > 12)
                    {
                        Log($"Error: _attemptsToJumpFrigateDestroyerAbyss > 12");
                        State = AbyssalState.Error;
                        _activationErrorTickCount = 0;
                        return;
                    }

                    if (DirectEve.Me.IsInAbyssalSpace())
                    {
                        Log($"We are now in the abyss space!");
                        _lastFilamentActivation = DateTime.UtcNow;
                        State = AbyssalState.AbyssalClear;
                        _attemptsToJumpMidgate = 0; // Reset attempts to jump midgate to be able to know in which stage we are currently
                        AreWeResumingFromACrash = false;
                        _abyssStatEntry = null;
                    }
                    else
                    {
                        Log($"Not yet in abyss space, waiting.");
                    }

                    if (ESCache.Instance.DirectEve.ActiveShip.Entity.IsFrigate || ESCache.Instance.DirectEve.ActiveShip.Entity.IsDestroyer)
                    {
                        var trace = ESCache.Instance.EntitiesOnGrid.FirstOrDefault(i => i.GroupId == (int)Group.AbyssalTrace);

                        if (trace == null)
                        {
                            if (DirectEve.Interval(1500, 4000))
                            {
                                Log($"There is no trace yet, waiting.");
                            }
                            return;
                        }

                        if (trace.Distance > 800)
                        {
                            if (DirectEve.Interval(4000, 10000))
                            {
                                Log($"Stopping the shop.");
                                Framework.ExecuteCommand(DirectCmd.CmdStopShip);
                            }
                        }

                        if (trace.Distance > (double)Distances.GateActivationRange)
                        {
                            if (!trace.IsOrbitedByActiveShip)
                            {
                                Log($"Orbiting the abyssal trace at [{trace.Distance}]m.");
                                trace.Orbit(_gateMTUOrbitDistance);
                            }
                            return;
                        }
                        else
                        {
                            var wnd = Framework.Windows.OfType<DirectAbyssActivationWindow>().FirstOrDefault();
                            if (wnd != null)
                            {
                                if (wnd.Activate())
                                {
                                    Log($"Jumping into the abyss.");
                                    _attemptsToJumpFrigateDestroyerAbyss++;
                                }
                                return;
                            }

                            Log($"We are close enough to jump, trying to open the jump window. Distance [{trace.Distance}]m. Attempts [{_attemptsToJumpFrigateDestroyerAbyss}]");
                            trace.ActivateAbyssalEntranceAccelerationGate();
                            _attemptsToJumpFrigateDestroyerAbyss++;
                            LocalPulse = UTCNowAddMilliseconds(1500, 3500);
                        }
                    }
                    break;


                case AbyssalState.AbyssalClear:

                    try
                    {
                        LogAbyssState();

                        try
                        {
                            var t = $"TargetPriorities [{string.Join(", ", GetSortedTargetList(TargetsOnGridWithoutLootTargets.Where(e => e.GroupId != 2009)).OrderBy(e => e.AbyssalTargetPriority).Select(e => $"{e.TypeName}:{e.AbyssalTargetPriority}:{e.Id}"))}]";
                            if (_targetPrioCache != t)
                            {
                                Log(_targetPrioCache);
                                Log(t);
                                _targetPrioCache = t;
                            }
                        }
                        catch { }

                        forceRecreatePath = false;

                        // Force recreate a-star path every [29,30] seconds
                        if (DirectEve.Interval(29000, 38000))
                            forceRecreatePath = true;

                        if (!DirectEve.Me.IsInAbyssalSpace())
                        {
                            if (DirectEve.Interval(3000, 4000))
                                Log($"We are not in abyss space. Starting over again.");
                            State = AbyssalState.Start;
                            return;
                        }

                        PrintDroneEstimatedKillTimePerStage();

                        var sc = ESCache.Instance.DirectEve.GetShipsCargo();
                        if (sc != null && _nextStack < DateTime.UtcNow)
                        {
                            _nextStack = DateTime.UtcNow.AddSeconds(GetRandom(50, 180));
                            if (sc.CanBeStacked)
                            {
                                Log($"Stacking ships cargo container.");
                                if (sc.StackAll())
                                    return;
                            }
                        }

                        CaptureHP();
                        // Play notification sounds
                        try
                        {
                            if (PlayNotificationSounds && (CurrentStageRemainingSecondsWithoutPreviousStages < 0 || ESCache.Instance.ActiveShip.ArmorPercentage < 50 || ESCache.Instance.ActiveShip.CapacitorPercentage < 20))
                            {
                                Util.PlayNoticeSound();
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex);
                        }

                        try
                        {
                            Util.MeasureTime(() =>
                            {
                                ManageStats();
                            }, true, "ManageStats");
                        }
                        catch (Exception ex)
                        {

                            if (DirectEve.Interval(5000))
                                Log($"ManageStats exception: {ex}");
                        }

                        // Update window labels
                        Util.MeasureTime(() =>
                        {
                            if (DirectEve.Interval(4500, 4900))
                            {
                                var currentAbysStage = CurrentAbyssalStage;
                                var currentStageRemainingSecondsWithoutPreviousStages = (int)CurrentStageRemainingSecondsWithoutPreviousStages;
                                var estimatedClearGrid = GetEstimatedStageRemainingTimeToClearGrid() ?? 0;
                                var secondsNeededToRetrieveWrecks = Convert.ToInt32(_secondsNeededToRetrieveWrecks);
                                var abyssRemainingSeconds = Convert.ToInt32(_abyssRemainingSeconds);
                                var secondsNeededToReachTheGate = Convert.ToInt32(_secondsNeededToReachTheGate);
                                var ignoreAbyss = IgnoreAbyssEntities;
                                Task.Run(() =>
                                {
                                    try
                                    {
                                        UpdateStageLabel(currentAbysStage);
                                        UpdateStageRemainingSecondsLabel(currentStageRemainingSecondsWithoutPreviousStages);
                                        UpdateStageKillEstimatedTime(estimatedClearGrid);
                                        UpdateStageEHPValues(_currentStageMaximumEhp, _currentStageCurrentEhp);
                                        UpdateWreckLootTime(secondsNeededToRetrieveWrecks);
                                        UpdateAbyssTotalTime(abyssRemainingSeconds);
                                        UpdateTimeNeededToGetToTheGate(secondsNeededToReachTheGate);
                                        UpdateIgnoreAbyssEntities(ignoreAbyss);
                                        //UpdateCurrentTargetEHPValues(currentTargetTotalArmor + currentTargetTotalShield + currentTargetTotalStructure, currentTargetArmor + currentTargetShield + currentTargetStructure);
                                    }
                                    catch (Exception ex)
                                    {
                                        Log(ex.ToString());
                                        Console.WriteLine(ex.ToString());
                                    }

                                });
                            }
                        }, true, "UpdateUI");


                        if (_lastMoveOnGrid.AddSeconds(5) < DateTime.UtcNow || _lastMoveOnGrid.AddSeconds(2) < DateTime.UtcNow && MoveToOverride != null)
                        {
                            Log("MoveOnGridWatchdog");
                            MoveOnGrid();
                            return;
                        }

                        if (_lastHandleDrones.AddSeconds(2) < DateTime.UtcNow && ManageDrones())
                        {
                            Log("HandleDroneWatchdog");
                            return;
                        }

                        if (_lastHandleTarget.AddSeconds(15) < DateTime.UtcNow && ManageTargetLocks())
                        {
                            Log("HandleTargetWatchdog");
                            return;
                        }

                    }
                    finally
                    {
                        if (sw.ElapsedMilliseconds != 0 && logDurations)
                        {
                            Log($"Elapsed (ms) [AbyssalClear-Start]: [{sw.ElapsedMilliseconds}]");
                        }
                        sw.Restart();
                    }

                    try
                    {
                        if (ManageDrones())
                            return;
                    }
                    finally
                    {
                        if (sw.ElapsedMilliseconds != 0 && logDurations)
                        {
                            Log($"Elapsed (ms) [ManageDrones]: [{sw.ElapsedMilliseconds}]");
                        }
                        sw.Restart();
                    }

                    try
                    {
                        if (ManagePropMod())
                        {
                            return;
                        }
                    }
                    finally
                    {
                        if (sw.ElapsedMilliseconds != 0 && logDurations)
                        {
                            Log($"Elapsed (ms) [ManagePropMod]: [{sw.ElapsedMilliseconds}]");
                        }
                        sw.Restart();
                    }


                    try
                    {
                        if (ManageTargetLocks())
                            return;
                    }
                    finally
                    {
                        if (sw.ElapsedMilliseconds != 0 && logDurations)
                        {
                            Log($"Elapsed (ms) [ManageTargetLocks]: [{sw.ElapsedMilliseconds}]");
                        }
                        sw.Restart();
                    }

                    try
                    {
                        if (ManageWeapons())
                            return;
                    }
                    finally
                    {
                        if (sw.ElapsedMilliseconds != 0 && logDurations)
                        {
                            Log($"Elapsed (ms) [ManageWeapons]: [{sw.ElapsedMilliseconds}]");
                        }
                        sw.Restart();
                    }

                    try
                    {
                        if (HandleLooting())
                            return;
                    }
                    finally
                    {

                        if (sw.ElapsedMilliseconds != 0 && logDurations)
                        {
                            Log($"Elapsed (ms) [HandleMTU]: [{sw.ElapsedMilliseconds}]");
                        }
                        sw.Restart();
                    }
                    try
                    {


                        if (DirectEve.Interval(15000, 20000))
                        {
                            var mtuDistane = _getMTUInSpace != null ? _getMTUInSpace.Distance : -1;
                            var mtuGateDistance = _getMTUInSpace != null ? _getMTUInSpace.DirectAbsolutePosition.GetDistance(_nextGate.DirectAbsolutePosition) : -1;
                            Log($"Abyssal remaining time: [{(_abyssRemainingSeconds > 60 ? (int)_abyssRemainingSeconds / 60 : 0)}] Minutes [{_abyssRemainingSeconds % 60}] seconds. IsSingleRoomAbyss [{_singleRoomAbyssal}] CurrentStageRemainingSecondsWithoutPreviousStages [{CurrentStageRemainingSecondsWithoutPreviousStages}] Stage [{CurrentAbyssalStage}] SecondsNeededToReachTheGate [{_secondsNeededToReachTheGate}] DistanceToGate [{_nextGate.Distance}] ActiveShip -> MTU [{mtuDistane}] MTU -> Gate  [{mtuGateDistance}] MaxVelocity [{_maxVelocity}] EnemiesRemaining [{TargetsOnGrid.Count}]");
                            var activeShip = ESCache.Instance.ActiveShip.Entity;
                            Log($"Shield [{activeShip.ShieldPct}] Armor [{activeShip.ArmorPct}] Structure [{activeShip.StructurePct}] NeutsOnGridCount [{_neutsOnGridCount}] MarshalsOnGridCount [{_marshalsOnGridCount}]");
                        }

                        if (!DirectEve.Session.IsInSpace)
                        {
                            if (DirectEve.Interval(15000, 20000))
                                Log($"Not in space? We probably are pod spinning in a station and borrowed our ship to someone. GGWP");
                            return;
                        }

                        LogNextGateState();

                        //  ------------------------------------------------------------------------------------ Single room abyss handling
                        if (_singleRoomAbyssal)
                        {
                            if (_getMTUInSpace == null && !allDronesInSpace.Any())
                            {
                                if (DirectEve.Interval(2500, 3000) && _nextGate.Distance <= 2300 && DirectSession.LastSessionChange.AddMilliseconds(4500) < DateTime.UtcNow)
                                {
                                    if (IsAbyssGateOpen)
                                    {

                                        _abyssStatEntry.MTULost = _getMTUInBay == null;
                                        _attemptsToJumpMidgate = 0;
                                        WriteStatsToDB();
                                        AreWeResumingFromACrash = false;
                                        _abyssStatEntry = null;
                                        Log($"ActivateAbyssalEndGate - SingleRoomAbyssal");
                                        _nextGate.ActivateAbyssalEndGate();
                                        DirectEventManager.NewEvent(new DirectEvent(DirectEvents.KEEP_ALIVE, "Keep alive."));
                                        State = AbyssalState.Start;

                                        // Here we write the abyss stats
                                    }
                                }
                            }
                            return; // Don't proceed to do anything in a single room abyss
                        }
                        //  ------------------------------------------------------------------------------------- END Single room abyss handling

                        if (!TargetsOnGrid.Any() || _mtuAlreadyDroppedDuringThisStage && TargetsOnGrid.All(e => e.GroupId == 2009) && _getMTUInSpace == null || _getMTUInSpace != null && _mtuScoopAttempts >= 11) // no targets || remainings targets only loot || leave mtu behind
                        {
                            if (DirectEve.Interval(15000, 20000))
                                Log($"No targets left.");

                            if (RecallDrones())
                                return;

                            if (DirectEve.ActiveDrones.Any())
                            {
                                var abandonDrones = false;

                                if (DirectEve.Interval(4000, 5000))
                                    Log($"Waiting for drones to return.");

                                // When all drones are returning for too long, we might abandom them! TODO: any better alternative?
                                if (DirectEve.ActiveDrones.All(d => d.DroneState == 4) && (DirectEve.ActiveDrones.Any(e => e.Distance < 22000) || _abyssRemainingSeconds < 9 && _nextGate.Distance <= 3500))
                                {
                                    if (_startedToRecallDronesWhileNoTargetsLeft == null)
                                    {
                                        _startedToRecallDronesWhileNoTargetsLeft = DateTime.UtcNow;
                                        Log($"Time started recalling drones [{_startedToRecallDronesWhileNoTargetsLeft}]");
                                    }

                                    var secondsSince = (DateTime.UtcNow - _startedToRecallDronesWhileNoTargetsLeft.Value).TotalSeconds;

                                    if (DirectEve.Interval(3000, 4500))
                                        Log($"Recalling drones since: [{secondsSince}] seconds.");

                                    if (
                                        secondsSince > 45 && IsAbyssGateOpen
                                        || secondsSince > 25 && CurrentStageRemainingSecondsWithoutPreviousStages - _secondsNeededToReachTheGate + 10 < 0
                                        || secondsSince > 19 && IsAbyssGateOpen && CurrentAbyssalStage == AbyssalStage.Stage3 && _abyssRemainingSeconds < 15
                                        || IsAbyssGateOpen && CurrentAbyssalStage == AbyssalStage.Stage3 && _abyssRemainingSeconds < 9
                                        )
                                    {
                                        abandonDrones = true;
                                        _abandoningDrones = true;
                                    }
                                }

                                if (!abandonDrones)
                                    return;
                                else
                                {
                                    if (DirectEve.Interval(3000, 4500))
                                    {
                                        Log($"We are abandoning our drones. They took too long to recover. Lost drones: ");
                                        foreach (var d in DirectEve.ActiveDrones)
                                        {
                                            Log($"Drone - Typename: [{d.TypeName}] TypeId {d.TypeId}");
                                        }
                                    }
                                }
                            }

                            if (_mtuScoopAttempts < 11 && (_getMTUInSpace != null || _lastMTUScoop.AddSeconds(3) > DateTime.UtcNow || (_getMTUInSpace == null && _lastMTULaunch.AddSeconds(7) > DateTime.UtcNow)))
                            {
                                if (DirectEve.Interval(4000, 5000))
                                {
                                    Log($"MTU is still in space waiting. IsMTUInspace [{_getMTUInSpace != null}] DistanceToMTU[{(_getMTUInSpace != null ? _getMTUInSpace.Distance : -1)}] _lastMTUScoop.AddSeconds(3) > DateTime.UtcNow  [{_lastMTUScoop.AddSeconds(3) > DateTime.UtcNow}] _lastMTULaunch.AddSeconds(7) > DateTime.UtcNow) [{_lastMTULaunch.AddSeconds(7) > DateTime.UtcNow}]");
                                }
                                return;
                            }

                            // We looted all, move to the gate and jump
                            if (DirectEve.Interval(15000, 20000))
                                Log($"Done. Move to the gate and jump.");


                            var gate = _endGate ?? _midGate;
                            if (gate.Distance > 2300)
                            {
                                if (DirectEve.Interval(10000, 15000))
                                {
                                    gate.MoveTo();
                                }
                                return;
                            }

                            // At this point we are close to the gate and can jump based on mid/end gate
                            if (_isInLastRoom)
                            {
                                if (DirectEve.Interval(2500, 3000) && IsAbyssGateOpen && DirectSession.LastSessionChange.AddMilliseconds(4500) < DateTime.UtcNow)
                                {
                                    Log($"ActivateAbyssalEndGate");
                                    gate.ActivateAbyssalEndGate();
                                    AreWeResumingFromACrash = false;
                                    _abyssStatEntry.MTULost = _getMTUInBay == null;

                                    _abyssStatEntry.LostDronesRoom3 += DirectEve.ActiveDrones.Count();
                                    _abyssStatEntry.SmallDronesLost += smallDronesInSpace.Count();
                                    _abyssStatEntry.MediumDronesLost += mediumDronesInSpace.Count();
                                    _abyssStatEntry.LargeDronesLost += largeDronesInSpace.Count();

                                    WriteStatsToDB();
                                    _abyssStatEntry = null;
                                    _attemptsToJumpMidgate = 0;
                                    DirectEntity.AStarErrors = 0;
                                    State = AbyssalState.Start;
                                    DirectEventManager.NewEvent(new DirectEvent(DirectEvents.KEEP_ALIVE, "Keep alive."));
                                    return;
                                }
                            }
                            else
                            {
                                if (DirectEve.Interval(2500, 3000) && IsAbyssGateOpen && DirectSession.LastSessionChange.AddMilliseconds(4500) < DateTime.UtcNow)
                                {
                                    _attemptsToJumpMidgate++;
                                    Log($"ActivateAbyssalAccelerationGate");
                                    gate.ActivateAbyssalAccelerationGate();

                                    if (CurrentAbyssalStage == AbyssalStage.Stage1)
                                        _abyssStatEntry.LostDronesRoom1 += DirectEve.ActiveDrones.Count();
                                    else
                                        _abyssStatEntry.LostDronesRoom2 += DirectEve.ActiveDrones.Count();

                                    _abyssStatEntry.SmallDronesLost += smallDronesInSpace.Count();
                                    _abyssStatEntry.MediumDronesLost += mediumDronesInSpace.Count();
                                    _abyssStatEntry.LargeDronesLost += largeDronesInSpace.Count();

                                    DirectEventManager.NewEvent(new DirectEvent(DirectEvents.KEEP_ALIVE, "Keep alive."));
                                    return;
                                }
                            }
                        }

                        MoveOnGrid(); // Keep this at the end
                    }

                    finally
                    {
                        if (sw.ElapsedMilliseconds != 0 && logDurations)
                        {
                            Log($"Elapsed (ms) [AbyssalClear-End]: [{sw.ElapsedMilliseconds}]");
                        }
                        sw.Restart();
                    }
                    break;
                case AbyssalState.UnloadLoot:
                    break;

                case AbyssalState.DumpSurveyDatabases:
                    DumpDatabaseSurveys();
                    break;

                case AbyssalState.BuyItems:
                    BuyItems();
                    break;

                case AbyssalState.TrashItems:

                    if (_trashItemAttempts > 3)
                    {
                        Log("We tried to trash items 3 times. Error.");
                        State = AbyssalState.Error;
                        return;
                    }

                    var itemHangar = ESCache.Instance.DirectEve.GetItemHangar();
                    if (itemHangar == null)
                        return;

                    if (itemHangar.Items.Count > _itemHangarTrashItemsThreshold)
                    {
                        var bpcItems = itemHangar.Items.Where(i => i.IsBlueprintCopy).ToList();
                        Log($"Trying to trash [{bpcItems.Count}] BPCs.");
                        Framework.TrashItems(bpcItems);
                        LocalPulse = UTCNowAddSeconds(25, 35);
                        _trashItemAttempts++;
                    }
                    else
                    {
                        Log($"Trashed items. Starting over again.");
                        _state = AbyssalState.Start;
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}
