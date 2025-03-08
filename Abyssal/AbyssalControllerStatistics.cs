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
using EVESharpCore.Framework;
using EVESharpCore.Framework.Lookup;
using SC::SharedComponents.EVE;
using SC::SharedComponents.EVE.ClientSettings;
using SC::SharedComponents.EVE.ClientSettings.Abyssal.Main;
using SC::SharedComponents.EVE.DatabaseSchemas;
using SC::SharedComponents.Extensions;
using SC::SharedComponents.IPC;
using SC::SharedComponents.SQLite;
using SC::SharedComponents.Utility;
using ServiceStack.OrmLite;
using SharpDX.Direct2D1;


namespace EVESharpCore.Controllers.Abyssal
{
    public partial class AbyssalController : AbyssalBaseController
    {

        internal DateTime _nextOverheatDisablePropMod = DateTime.MinValue;
        internal DateTime _nextOverheatDisableShieldHardner = DateTime.MinValue;
        internal DateTime _nextOverheatDisableShieldBooster = DateTime.MinValue;

        internal AbyssStatEntry _abyssStatEntry;
        internal DateTime _lastDrugUsage = DateTime.MinValue;

        internal double _currentStageMaximumEhp = 0;
        internal double _currentStageCurrentEhp = 0;


        private DateTime _lastAbyssLogState;
        /// <summary>
        /// Prints information about the current abyss state every 30 seconds
        /// </summary>
        internal void LogAbyssState()
        {
            if (!DirectEve.Me.IsInAbyssalSpace())
                return;

            if (_lastAbyssLogState.AddSeconds(30) > DateTime.UtcNow)
                return;

            _lastAbyssLogState = DateTime.UtcNow;

            try
            {
                var ship = ESCache.Instance.DirectEve.ActiveShip;
                var de = ESCache.Instance.DirectEve;
                var droneRecalls = _abyssStatEntry != null ? CurrentAbyssalStage == AbyssalStage.Stage1 ? _abyssStatEntry.DroneRecallsStage1 : CurrentAbyssalStage == AbyssalStage.Stage2 ? _abyssStatEntry.DroneRecallsStage2 : _abyssStatEntry.DroneRecallsStage3 : -1;

                var lostDronesCurrentRoom = 0;
                try
                {
                    lostDronesCurrentRoom = CurrentAbyssalStage switch
                    {
                        AbyssalStage.Stage1 => _abyssStatEntry.LostDronesRoom1,
                        AbyssalStage.Stage2 => _abyssStatEntry.LostDronesRoom2,
                        AbyssalStage.Stage3 => _abyssStatEntry.LostDronesRoom3,
                        _ => 0,
                    };
                }
                catch (Exception)
                {
                }

                Log($"<------- Abyss            - Stage [{CurrentAbyssalStage}] GateDistance [{_nextGate.Distance:F}] EstimatedGridClearTime [{(GetEstimatedStageRemainingTimeToClearGrid() ?? 0):F}] TimeNeededToTheGate [{_secondsNeededToReachTheGate:F}] StageRemainingSeconds [{CurrentStageRemainingSecondsWithoutPreviousStages:F}] TotalRemainingSeconds [{ESCache.Instance.DirectEve.Me.AbyssalRemainingSeconds:F}] SecondsSinceLastSessionChange [{_secondsSinceLastSessionChange:F}] IsAbyssGateOpen [{IsAbyssGateOpen}] NextGateInASpeedCloud [{IsNextGateInASpeedCloud}]");
                Log($"-------- TargetInfo       - TargetPriorities [{string.Join(", ", GetSortedTargetList(TargetsOnGridWithoutLootTargets.Where(e => e.Distance < _maxDroneRange && e.GroupId != 2009)).OrderBy(e => e.AbyssalTargetPriority).Select(e => $"{e.TypeName}:{e.AbyssalTargetPriority}"))}] CurrentlyLockedTargets [{string.Join(", ", TargetsOnGridWithoutLootTargets.Select(e => e.TypeName))}]");
                Log($"-------- Ship             - Shield [{ship.ShieldPercentage:F}] Armor [{ship.ArmorPercentage:F}] Structure [{ship.StructurePercentage:F}] Capacitor [{ship.CapacitorPercentage:F}] Speed [{ship.Entity.Velocity:F}] TargetingRange [{_maxTargetRange}] MaxDroneRange [{_maxDroneRange}] MaxRange [{_maxRange}] HudStatusEffects [{string.Join(", ", de.Me.ActiveHudStatusEffects.Select(d => d.ToString()).ToArray())}] DistanceAbyssCenter [{Math.Round(ship.Entity.DistanceTo(AbyssalCenter), 2)}]");
                Log($"-------- Drones           - DronesLostThisStage [{lostDronesCurrentRoom}]DroneRecallsThisStage [{droneRecalls}] DronesInBay [{alldronesInBay.Sum(e => e.Stacksize):D2}] DronesInSpace [{allDronesInSpace.Count:D2}] InSpaceBandwidth [{_currentlyUsedDroneBandwidth:F}] AmountDronesInSpeedCloud [{allDronesInSpace.Count(e => e.IsInSpeedCloud)}]");
                Log($"-------- DroneInfo        - (Drone,State,FollowEnt,DistanceToFollowEnt) [{string.Join(", ", allDronesInSpace.Select(d => de.EntitiesById.ContainsKey(d.FollowId) ? $"({d.TypeName},{d.DroneState},{de.EntitiesById[d.FollowId].TypeName},{Math.Round(de.EntitiesById[d.FollowId].DistanceTo(de.EntitiesById[d.Id]), 2)})" : $"({d.TypeName},{d.DroneState},Unknown,0)").ToArray())}]");
                Log($"-------- Targ. our Drones - [{string.Join(", ", TargetsOnGrid.Where(e => e.IsTargetingDrones).Select(e => e.TypeName).ToArray())}]");
                Log($"-------- Player           - Boosters [{string.Join(", ", de.Me.Boosters.Select(b => de.GetInvType(b.TypeID).TypeName).ToArray())}] InSpeedCloud [{ship.Entity.IsInSpeedCloud}] SideEffects [{string.Join(", ", de.Me.GetAllNegativeBoosterEffects().Select(d => d.ToString()).ToArray())}] AbyssDailyHours [{TimeSpan.FromSeconds(ESCache.Instance.EveAccount.AbyssSecondsDaily).TotalHours:F}]");
                Log($"-------- Entities         - EnemiesOnGrid [{_targetOnGridCount}] AmountEnemiesOnGridInASpeedCloud [{TargetsOnGridWithoutLootTargets.Count(e => e.IsInSpeedCloud)}] GJ/s [{TargetsOnGrid.Sum(e => e.GigaJouleNeutedPerSecond):F}] Kikimoras [{TargetsOnGrid.Count(e => e.TypeName.ToLower().Contains("kikimora"))}] Marshals [{_marshalsOnGridCount}] Karen [{TargetsOnGrid.Any(e => e.IsKaren)}] Battleships [{TargetsOnGrid.Count(e => e.IsNPCBattleship)}] Neuts [{_neutsOnGridCount}] StageHPMax/Remaining [{_currentStageMaximumEhp:F}|{_currentStageCurrentEhp:F}]");
                Log($"-------- Outside boundary - [{string.Join(", ", de.Entities.Where(d => !IsSpotWithinAbyssalBounds(d.DirectAbsolutePosition)).Select(b => $"Dist({b.Distance}),Name({b.TypeName})").ToArray())}]");
                Log($"-------> EntDists         - {GetEnemiesAndTheirDistances()}");
            }
            catch (Exception ex)
            {
                Log($"{ex}");
            }
        }

        private String GetEnemiesAndTheirDistances()
        {
            var sb = new StringBuilder();
            var targetsOrderedByDist = TargetsOnGrid.OrderBy(e => e.Distance);
            foreach (var target in targetsOrderedByDist)
            {
                if (target == targetsOrderedByDist.Last())
                    sb.Append($"{target.TypeName} [{target.Distance:F}]");
                else
                    sb.Append($"{target.TypeName} [{target.Distance:F}], ");
            }

            return sb.ToString();
        }

        private double? GetEstimatedStageRemainingTimeToClearGrid()
        {
            if (_currentStageMaximumEhp == _currentStageCurrentEhp)
                return 1200 / 3; // assume stage maximum time at the start

            if ((long)_secondsSinceLastSessionChange == 0)
                return null;

            var dps = (_currentStageMaximumEhp - _currentStageCurrentEhp) / _secondsSinceLastSessionChange;

            var timeToClearGridWithDrones = GetSecondsToKillWithActiveDrones();

            if ((long)dps == 0)
            {
                if (timeToClearGridWithDrones > 0 && timeToClearGridWithDrones < 400)
                    return timeToClearGridWithDrones;
                return null;
            }
            var remaining = _currentStageCurrentEhp / dps;

            if (timeToClearGridWithDrones > 0 && timeToClearGridWithDrones < 400)
            {
                remaining = (remaining + timeToClearGridWithDrones) / 2;
            }

            return remaining;
        }
        private DateTime _lastStateWrite;
        public void WriteStatsToDB()
        {
            if (_lastStateWrite.AddMinutes(1) > DateTime.UtcNow)
            {
                Log($"We already did write stats in the past minute, skipping.");
                return;
            }
            _lastStateWrite = DateTime.UtcNow;

            if (_abyssStatEntry != null)
            {
                using (var wc = WriteConn.Open())
                {
                    try
                    {
                        var newTotal = _abyssStatEntry.TotalSeconds + ESCache.Instance.EveAccount.AbyssSecondsDaily;
                        Log($"New total daily runtime: {TimeSpan.FromSeconds(newTotal)}");
                        WCFClient.Instance.GetPipeProxy.SetEveAccountAttributeValue(ESCache.Instance.CharName, nameof(EveAccount.AbyssSecondsDaily), newTotal);
                        Log("Writing stats to SQLIte DB.");
                        wc.DB.Insert(_abyssStatEntry);
                        Log("Finished writing stats to SQLIte DB.");
                    }
                    catch (Exception e)
                    {
                        Log($"SQLDB Write Exception: {e.ToString()}");
                    }
                }
            }
        }

        private void CaptureHP()
        {
            int ms = 0;
            using (new DisposableStopwatch(t => ms = (int)t.TotalMilliseconds))
            {
                var ehp = 0.0d;
                foreach (var target in TargetsOnGrid)
                {
                    var currEhp = 0d;
                    currEhp = target.CurrentArmor.Value + target.CurrentShield.Value + target.CurrentStructure.Value;
                    ehp += currEhp;
                }

                _currentStageMaximumEhp = Math.Max(ehp, _currentStageMaximumEhp);
                _currentStageCurrentEhp = ehp;
            }

            if (DirectEve.Interval(4000, 5000))
            {
                Log($"EHP Calculation took: [{ms}] ms. TimeSinceLastSessionChange [{_secondsSinceLastSessionChange}] " +
                    $"RemainingStageClearGrindSeconds [{GetEstimatedStageRemainingTimeToClearGrid()}] ExpectedFinishDate [{DateTime.UtcNow.AddSeconds(GetEstimatedStageRemainingTimeToClearGrid() ?? 1000)}] maxEHP [{_currentStageMaximumEhp}] currEHP [{_currentStageCurrentEhp}]");
            }
        }

        private DateTime? _lastDroneInOptimal = null;
        private double _dronesInOptimalStage;

        // to not add stats logic overhead within the code, we just call this on every frame while in abyss space.
        internal void ManageStats()
        {
            var dronesInOptimal = DronesInOptimalCount();

            if (_lastDroneInOptimal != null)
            {
                var duration = (DateTime.UtcNow - _lastDroneInOptimal.Value).TotalSeconds;

                if (dronesInOptimal > 0)
                {
                    _dronesInOptimalStage += duration * (double)dronesInOptimal / 5.0d;
                }

                _lastDroneInOptimal = DateTime.UtcNow;
            }
            else
            {
                _lastDroneInOptimal = DateTime.UtcNow;
            }

            if (!DirectEve.Interval(2000))
                return;

            if (!DirectEve.Me.IsInAbyssalSpace())
                return;

            if (AreWeResumingFromACrash)
            {
                if (DirectEve.Interval(30000))
                    Log($"We are resuming from a crash. Skipping stats.");
                return;
            }

            //ensure necessary containers are open
            var shipsCargo = ESCache.Instance.DirectEve.GetShipsCargo();
            if (shipsCargo == null)
                return;

            var droneBay = ESCache.Instance.DirectEve.GetShipsDroneBay();
            if (droneBay == null)
                return;

            if (_abyssStatEntry == null)
            {
                _abyssStatEntry = new AbyssStatEntry();
                _abyssStatEntry.LowestStructure1 = 1;
                _abyssStatEntry.LowestArmor1 = 1;
                _abyssStatEntry.LowestShield1 = 1;
                _abyssStatEntry.LowestStructure2 = 1;
                _abyssStatEntry.LowestArmor2 = 1;
                _abyssStatEntry.LowestShield2 = 1;
                _abyssStatEntry.LowestStructure3 = 1;
                _abyssStatEntry.LowestArmor3 = 1;
                _abyssStatEntry.LowestShield3 = 1;
                _abyssStatEntry.LowestCap = 1;
            }

            _abyssStatEntry.StartedDate = DateTime.UtcNow.AddSeconds(-(20 * 60 - _abyssRemainingSeconds));
            var largeDronesLeftInbay = _droneBayItemList.Where(x => x.Item3 == DroneSize.Large || x.Item3 == DroneSize.Gecko).Sum(x => GetAmountOfTypeIdLeftInDroneBay(x.Item1, x.Item4));
            var medDronesLeftInbay = _droneBayItemList.Where(x => x.Item3 == DroneSize.Medium).Sum(x => GetAmountOfTypeIdLeftInDroneBay(x.Item1, x.Item4));
            var smallDronesLeftInbay = _droneBayItemList.Where(x => x.Item3 == DroneSize.Small).Sum(x => GetAmountOfTypeIdLeftInDroneBay(x.Item1, x.Item4));

            var amountOfAllDronesInBay = largeDronesLeftInbay + medDronesLeftInbay + smallDronesLeftInbay;
            var amountOfAllDronesInBayAfterArm = _droneBayItemList.Sum(d => d.Item2);

            var analBurner = DirectEve.Modules.Where(e => e.GroupId == (int)Group.Afterburner);
            var shieldHardener = DirectEve.Modules.Where(e => e.GroupId == (int)Group.ShieldHardeners);
            var shieldBooster = DirectEve.Modules.Where(e => e.GroupId == (int)Group.ShieldBoosters);
            var modules = analBurner.Concat(shieldHardener).Concat(shieldBooster);

            var noDronesInSpace = !allDronesInSpace.Any();

            _abyssStatEntry.SingleGateAbyss = _singleRoomAbyssal;
            _abyssStatEntry.FilamentTypeId = _filamentTypeId;

            _abyssStatEntry.LowestCap = Math.Round(Math.Min(ESCache.Instance.ActiveShip.CapacitorPercentage / 100, _abyssStatEntry.LowestCap), 2);
            if (_abyssStatEntry.AStarErrors != DirectEntity.AStarErrors)
            {
                Log($"AbyssStatEntry.AStarErrors changed it's value, current value [{DirectEntity.AStarErrors}]");
            }

            _abyssStatEntry.AStarErrors = DirectEntity.AStarErrors;

            switch (CurrentAbyssalStage)
            {
                case AbyssalStage.Stage1:

                    if (_abyssStatEntry.Room1Seconds > 0)
                        _abyssStatEntry.DronePercOptimal1 = Math.Round(_dronesInOptimalStage / _abyssStatEntry.Room1Seconds, 2);
                    _abyssStatEntry.DroneEngagesStage1 = _droneEngageCount;
                    _abyssStatEntry.Room1Hp = Math.Round(_currentStageMaximumEhp, 2);
                    _abyssStatEntry.LowestStructure1 = Math.Min(Math.Round(ESCache.Instance.ActiveShip.Entity.StructurePct, 2), _abyssStatEntry.LowestStructure1);
                    _abyssStatEntry.LowestArmor1 = Math.Min(Math.Round(ESCache.Instance.ActiveShip.Entity.ArmorPct, 2), _abyssStatEntry.LowestArmor1);
                    _abyssStatEntry.LowestShield1 = Math.Min(Math.Round(ESCache.Instance.ActiveShip.Entity.ShieldPct, 2), _abyssStatEntry.LowestShield1);
                    _abyssStatEntry.DroneRecallsStage1 = _droneRecallsStage;
                    _abyssStatEntry.Room1Seconds = (int)GetCurrentStageStageSeconds;

                    if (IsAbyssGateOpen)
                    {

                        if (noDronesInSpace)
                        {
                            _abyssStatEntry.LostDronesRoom1 = amountOfAllDronesInBayAfterArm - amountOfAllDronesInBay;
                        }

                        if (_abyssStatEntry.PenaltyStrength == default(double))
                        {
                            _abyssStatEntry.PenaltyStrength = Framework.Me.GetAbyssResistsDebuff()?.Item2 ?? 0;
                        }

                        if (_alreadyLootedItemIds.Any() && String.IsNullOrEmpty(_abyssStatEntry.LootTableRoom1))
                        {
                            _abyssStatEntry.LootTableRoom1 = string.Join(",", _alreadyLootedItems);
                        }

                        if (!_singleRoomAbyssal)
                        {
                            _abyssStatEntry.ClearDoneGateDist1 = Math.Round(Math.Max(_nextGate.Distance, _abyssStatEntry.ClearDoneGateDist1), 2);
                        }
                        _abyssStatEntry.Room1CacheMiss = _remainingNonEmptyWrecksAndCacheCount;
                    }
                    else
                    {
                        

                        _abyssStatEntry.OverheatRoom1 = _abyssStatEntry.OverheatRoom1 || modules.Any(m => m.IsOverloaded);
                        _abyssStatEntry.DrugsUsedRoom1 = _abyssStatEntry.DrugsUsedRoom1 || _lastDrugUsage.AddSeconds(4) > DateTime.UtcNow;
                        _abyssStatEntry.Room1Neuts = Math.Max(_abyssStatEntry.Room1Neuts, _neutsOnGridCount);
                        _abyssStatEntry.Room1NeutGJs = Math.Max(_abyssStatEntry.Room1NeutGJs, TargetsOnGrid.Sum(e => e.GigaJouleNeutedPerSecond));
                        if (TargetsOnGrid.Any(e => e.GroupId != 2009) && _currentLockedAndLockingTargets.Count() >= 1 && string.IsNullOrEmpty(_abyssStatEntry.Room1Dump))
                        {
                            _abyssStatEntry.Room1Dump = GenerateGridDump(DirectEve.Entities.Where(e => e.IsNPCByBracketType || e.GroupId == 2009));
                            Log($"Generated Dump for room 1: {_abyssStatEntry.Room1Dump}");
                            _abyssStatEntry.PylonsClounds1 = GenerateGridDump(DirectEve.Entities.Where(e => e.IsAbyssSphereEntity), false);
                        }

                    }
                    break;
                case AbyssalStage.Stage2:

                    if (_abyssStatEntry.Room2Seconds > 0)
                        _abyssStatEntry.DronePercOptimal2 = Math.Round(_dronesInOptimalStage / _abyssStatEntry.Room2Seconds, 2);
                    _abyssStatEntry.DroneEngagesStage2 = _droneEngageCount;
                    _abyssStatEntry.Room2Hp = Math.Round(_currentStageMaximumEhp, 2);
                    _abyssStatEntry.LowestStructure2 = Math.Min(Math.Round(ESCache.Instance.ActiveShip.Entity.StructurePct, 2), _abyssStatEntry.LowestStructure2);
                    _abyssStatEntry.LowestArmor2 = Math.Min(Math.Round(ESCache.Instance.ActiveShip.Entity.ArmorPct, 2), _abyssStatEntry.LowestArmor2);
                    _abyssStatEntry.LowestShield2 = Math.Min(Math.Round(ESCache.Instance.ActiveShip.Entity.ShieldPct, 2), _abyssStatEntry.LowestShield2);

                    _abyssStatEntry.Room2Seconds = (int)GetCurrentStageStageSeconds;
                    _abyssStatEntry.DroneRecallsStage2 = _droneRecallsStage;

                    if (IsAbyssGateOpen)
                    {

                        if (noDronesInSpace)
                        {
                            _abyssStatEntry.LostDronesRoom2 = amountOfAllDronesInBayAfterArm - amountOfAllDronesInBay -
                                                              _abyssStatEntry.LostDronesRoom1;
                        }


                        if (_abyssStatEntry.PenaltyStrength == default(double))
                        {
                            _abyssStatEntry.PenaltyStrength = Framework.Me.GetAbyssResistsDebuff()?.Item2 ?? 0;
                        }

                        if (_alreadyLootedItemIds.Any() && String.IsNullOrEmpty(_abyssStatEntry.LootTableRoom2))
                        {
                            _abyssStatEntry.LootTableRoom2 = string.Join(",", _alreadyLootedItems);
                        }

                        if (!_singleRoomAbyssal)
                        {
                            _abyssStatEntry.ClearDoneGateDist2 = Math.Round(Math.Max(_nextGate.Distance, _abyssStatEntry.ClearDoneGateDist2), 2);
                        }
                        _abyssStatEntry.Room2CacheMiss = _remainingNonEmptyWrecksAndCacheCount;
                    }
                    else
                    {
                        
                        _abyssStatEntry.OverheatRoom2 = _abyssStatEntry.OverheatRoom2 || modules.Any(m => m.IsOverloaded);
                        _abyssStatEntry.DrugsUsedRoom2 = _abyssStatEntry.DrugsUsedRoom2 || _lastDrugUsage.AddSeconds(4) > DateTime.UtcNow;
                        _abyssStatEntry.Room2Neuts = Math.Max(_abyssStatEntry.Room2Neuts, _neutsOnGridCount);
                        _abyssStatEntry.Room2NeutGJs = Math.Max(_abyssStatEntry.Room2NeutGJs, TargetsOnGrid.Sum(e => e.GigaJouleNeutedPerSecond));
                        if (TargetsOnGrid.Any(e => e.GroupId != 2009) && _currentLockedAndLockingTargets.Count() >= 1 && string.IsNullOrEmpty(_abyssStatEntry.Room2Dump))
                        {
                            _abyssStatEntry.Room2Dump = GenerateGridDump(DirectEve.Entities.Where(e => e.IsNPCByBracketType || e.GroupId == 2009));
                            Log($"Generated Dump for room 2: {_abyssStatEntry.Room2Dump}");
                            _abyssStatEntry.PylonsClounds2 = GenerateGridDump(DirectEve.Entities.Where(e => e.IsAbyssSphereEntity), false);
                        }

                    }
                    break;
                case AbyssalStage.Stage3:

                    if (_abyssStatEntry.Room3Seconds > 0)
                        _abyssStatEntry.DronePercOptimal3 = Math.Round(_dronesInOptimalStage / _abyssStatEntry.Room3Seconds, 2);

                    _abyssStatEntry.DroneEngagesStage3 = _droneEngageCount;
                    _abyssStatEntry.Room3Hp = Math.Round(_currentStageMaximumEhp, 2);
                    _abyssStatEntry.LowestStructure3 = Math.Min(Math.Round(ESCache.Instance.ActiveShip.Entity.StructurePct, 2), _abyssStatEntry.LowestStructure3);
                    _abyssStatEntry.LowestArmor3 = Math.Min(Math.Round(ESCache.Instance.ActiveShip.Entity.ArmorPct, 2), _abyssStatEntry.LowestArmor3);
                    _abyssStatEntry.LowestShield3 = Math.Min(Math.Round(ESCache.Instance.ActiveShip.Entity.ShieldPct, 2), _abyssStatEntry.LowestShield3);

                    _abyssStatEntry.TotalSeconds = (int)(DateTime.UtcNow - _abyssStatEntry.StartedDate).TotalSeconds;
                    _abyssStatEntry.Room3Seconds = (int)GetCurrentStageStageSeconds;
                    _abyssStatEntry.DroneRecallsStage3 = _droneRecallsStage;


                    if (IsAbyssGateOpen)
                    {

                        if (noDronesInSpace)
                        {
                            _abyssStatEntry.LostDronesRoom3 = amountOfAllDronesInBayAfterArm - amountOfAllDronesInBay -
                                                              _abyssStatEntry.LostDronesRoom1 -
                                                              _abyssStatEntry.LostDronesRoom2;

                            _abyssStatEntry.LargeDronesLost =
                                _droneBayItemList.Where(x => x.Item3 == DroneSize.Large).Sum(x => x.Item2) - largeDronesLeftInbay;
                            _abyssStatEntry.MediumDronesLost = _droneBayItemList.Where(x => x.Item3 == DroneSize.Medium).Sum(x => x.Item2) - medDronesLeftInbay;
                            _abyssStatEntry.SmallDronesLost =
                                _droneBayItemList.Where(x => x.Item3 == DroneSize.Small).Sum(x => x.Item2) - smallDronesLeftInbay;
                        }

                        if (_abyssStatEntry.PenaltyStrength == default(double))
                        {
                            _abyssStatEntry.PenaltyStrength = Framework.Me.GetAbyssResistsDebuff()?.Item2 ?? 0;
                        }

                        if (_alreadyLootedItemIds.Any() && String.IsNullOrEmpty(_abyssStatEntry.LootTableRoom3))
                        {
                            _abyssStatEntry.LootTableRoom3 = string.Join(",", _alreadyLootedItems);
                        }

                        if (!_singleRoomAbyssal)
                        {
                            _abyssStatEntry.ClearDoneGateDist3 = Math.Round(Math.Max(_nextGate.Distance, _abyssStatEntry.ClearDoneGateDist3), 2);
                        }
                        _abyssStatEntry.Room3CacheMiss = _remainingNonEmptyWrecksAndCacheCount;
                    }
                    else
                    {
                        

                        _abyssStatEntry.OverheatRoom3 = _abyssStatEntry.OverheatRoom3 || modules.Any(m => m.IsOverloaded);
                        _abyssStatEntry.DrugsUsedRoom3 = _abyssStatEntry.DrugsUsedRoom3 || _lastDrugUsage.AddSeconds(4) > DateTime.UtcNow;
                        _abyssStatEntry.Room3Neuts = Math.Max(_abyssStatEntry.Room3Neuts, _neutsOnGridCount);
                        _abyssStatEntry.Room3NeutGJs = Math.Max(_abyssStatEntry.Room3NeutGJs, TargetsOnGrid.Sum(e => e.GigaJouleNeutedPerSecond));

                        if (TargetsOnGrid.Any(e => e.GroupId != 2009) && _currentLockedAndLockingTargets.Count() >= 1 && string.IsNullOrEmpty(_abyssStatEntry.Room3Dump))
                        {
                            _abyssStatEntry.Room3Dump = GenerateGridDump(DirectEve.Entities.Where(e => e.IsNPCByBracketType || e.GroupId == 2009));
                            _abyssStatEntry.PylonsClounds3 = GenerateGridDump(DirectEve.Entities.Where(e => e.IsAbyssSphereEntity), false);
                            Log($"Generated Dump for room 3: {_abyssStatEntry.Room3Dump}");
                        }
                    }

                    break;
                default:
                    break;
            }

            if (DirectEve.Interval(5000))
            {
                var act = new Action(() =>
                {

                    try
                    {
                        var frm = this.Form as AbyssalControllerForm;
                        var dgv = frm.GetDataGridView1;
                        frm.Invoke(new Action(() =>
                        {
                            var scrollingIndex = 0;
                            var colIndex = 0;

                            if (dgv.RowCount != 0)
                            {
                                scrollingIndex = dgv.FirstDisplayedCell.RowIndex;
                                colIndex = dgv.FirstDisplayedScrollingColumnIndex;
                            }

                            dgv.DataSource = new List<AbyssStatEntry> { _abyssStatEntry };

                            if (dgv.RowCount != 0)
                            {
                                dgv.FirstDisplayedScrollingRowIndex = scrollingIndex;
                                dgv.FirstDisplayedScrollingColumnIndex = colIndex;
                            }
                        }));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex);
                        Log(ex.ToString());
                    }


                });


                Task.Run(act);
            }
        }

        public override void ReceiveBroadcastMessage(BroadcastMessage broadcastMessage)
        {

        }
    }
}
