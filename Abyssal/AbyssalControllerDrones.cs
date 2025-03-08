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
using System.Windows.Input;
using EVESharpCore.Cache;
using EVESharpCore.Controllers.ActionQueue.Actions.Base;
using EVESharpCore.Controllers.Base;
using EVESharpCore.Framework;
using SC::SharedComponents.EVE.ClientSettings;
using SC::SharedComponents.EVE.ClientSettings.Abyssal.Main;
using SC::SharedComponents.EVE.DatabaseSchemas;
using SC::SharedComponents.Extensions;
using SC::SharedComponents.SQLite;
using SC::SharedComponents.Utility;
using ServiceStack.OrmLite;
using SharpDX.Direct2D1;
using static EVESharpCore.Controllers.Abyssal.AbyssalController;


namespace EVESharpCore.Controllers.Abyssal
{
    public partial class AbyssalController : AbyssalBaseController
    {
        private int _droneRecallsStage;

        public class AbyssalDrone
        {
            public long DroneId { get; set; }
            public int TypeId { get; set; }
            public string TypeName { get; set; }
            public double Bandwidth { get; set; }
            public DroneActionState ActionState { get; set; }
            public int StackSize { get; set; }

            public double ShieldPercent { get; set; }
            public double ArmorPercent { get; set; }
            public double StructurePercent { get; set; }

            public enum DroneActionState
            {
                InSpace,
                DeployingLimbo,
                InBay,
            }

            public AbyssalDrone(DirectItem directItem)
            {
                DroneId = directItem.ItemId;
                TypeName = directItem.TypeName;
                TypeId = directItem.IsDynamicItem ? directItem.OrignalDynamicItem.TypeId : directItem.TypeId;
                Bandwidth = directItem.TryGet<double>("droneBandwidthUsed");
                ActionState = DroneActionState.InBay;
                StackSize = directItem.Stacksize;
                ShieldPercent = directItem.GetDroneInBayDamageState()?.X ?? 0d;
                ArmorPercent = directItem.GetDroneInBayDamageState()?.Y ?? 0d;
                StructurePercent = directItem.GetDroneInBayDamageState()?.Z ?? 0d;
            }

            public AbyssalDrone(DirectEntity directEntity)
            {
                DroneId = directEntity.Id;
                TypeName = directEntity.TypeName;
                TypeId = directEntity.IsDynamicItem ? directEntity.OrignalDynamicItem.TypeId : directEntity.TypeId;
                Bandwidth = directEntity.TryGet<double>("droneBandwidthUsed");
                ActionState = DroneActionState.InSpace;
                StackSize = 1;
                ShieldPercent = directEntity.ShieldPct;
                ArmorPercent = directEntity.ArmorPct;
                StructurePercent = directEntity.StructurePct;
            }

            /// <summary>
            /// Can be null!
            /// </summary>
            public DirectItem? GetDirectItem => ESCache.Instance.DirectEve.GetShipsDroneBay()?.Items
                .FirstOrDefault(e => e.ItemId == DroneId);

            /// <summary>
            /// Can be null!
            /// </summary>
            public DirectEntity? GetDirectEntity =>
                ESCache.Instance.DirectEve.ActiveDrones.FirstOrDefault(e => e.Id == DroneId);

            public DirectInvType? GetInvType => ActionState == DroneActionState.InSpace ? (DirectInvType)GetDirectEntity : ActionState == DroneActionState.InBay ? (DirectInvType)GetDirectItem : null;
        }

        private List<AbyssalDrone> _limboDeployingAbyssalDrones = new List<AbyssalDrone>();
        private Dictionary<long, DateTime> _recentlyDeployedDronesTimer = new Dictionary<long, DateTime>();
        private Dictionary<long, DateTime> _recentlyRecalledDronesTimer = new Dictionary<long, DateTime>();
        private Dictionary<long, DateTime> _limboDeployingDronesTimer = new Dictionary<long, DateTime>();

        private List<long> _previouslyDeployedDronesDebug = new List<long>();

        private int _droneRecallsDueEnemiesBeingInASpeedCloud = 0;
        private DateTime _nextDroneRecallDueEnemiesBeingInASpeedCloud = DateTime.MinValue;

        public void UpdateDroneStateForDeployed(AbyssalDrone abyssalDrone)
        {
            if (abyssalDrone.StackSize > 1)
            {
                // Drone ids are created when stack is split, cannot track.
                return;
            }

            abyssalDrone.ActionState = AbyssalDrone.DroneActionState.DeployingLimbo;
            _limboDeployingDronesTimer[abyssalDrone.DroneId] = DateTime.UtcNow;
            _recentlyDeployedDronesTimer[abyssalDrone.DroneId] = DateTime.UtcNow;
            _limboDeployingAbyssalDrones.Add(abyssalDrone);
        }

        public void UpdateDroneStateForRecall(long droneId)
        {
            //_recentlyDeployedDronesTimer.Remove(droneId);
            _recentlyRecalledDronesTimer[droneId] = DateTime.UtcNow;
        }

        private bool IsValidDroneHealth(AbyssalDrone drone)
        {
            return drone.ActionState switch
            {
                AbyssalDrone.DroneActionState.InBay => drone.ShieldPercent >=
                                                       (_droneLaunchShieldPerc[(int)drone.Bandwidth] / 100d),
                AbyssalDrone.DroneActionState.DeployingLimbo => true,
                AbyssalDrone.DroneActionState.InSpace => drone.ShieldPercent >=
                                                         (_droneRecoverShieldPerc[(int)drone.Bandwidth] / 100d),
            };
        }

        public List<AbyssalDrone> GetWantedDronesInSpace()
        {
            var droneBay = ESCache.Instance.DirectEve.GetShipsDroneBay();
            // Reset any timer state
            // Remove any drones from recently deployed that are no longer recent

            var totalBandwidth = (double)ESCache.Instance.DirectEve.ActiveShip.DroneBandwidth;
            var remainingBandwidth = (double)ESCache.Instance.DirectEve.ActiveShip.GetRemainingDroneBandwidth();

            // we need to change this dynamically on what is current in space, i would suggest to use the remaining ships bandwidth as factor
            // (so if we don't occupy all or bandwidth, we lose damage). so if only 50 bandwidth is used with 5 drones, swap quicker
            // than if 5 drones and 110 bandwidth used for example
            var recentlyDeployedDelay = 5 + (20 * ((totalBandwidth - remainingBandwidth) / totalBandwidth));

            _recentlyDeployedDronesTimer = _recentlyDeployedDronesTimer
                .Where(entry => entry.Value > DateTime.UtcNow.AddSeconds(-recentlyDeployedDelay))
                .ToDictionary(entry => entry.Key, entry => entry.Value);
            _limboDeployingDronesTimer = _limboDeployingDronesTimer
                .Where(entry => entry.Value > DateTime.UtcNow.AddSeconds(-15))
                .ToDictionary(entry => entry.Key, entry => entry.Value);
            _limboDeployingAbyssalDrones = _limboDeployingAbyssalDrones
                .Where(d => _limboDeployingDronesTimer.ContainsKey(d.DroneId))
                .ToList();

            // Get all drones 
            var dronesInSpace = allDronesInSpace.Select(d => new AbyssalDrone(d)).ToList();
            var dronesInBay = alldronesInBay.Select(d => new AbyssalDrone(d)).ToList();
            var dronesInLimbo = _limboDeployingAbyssalDrones;

            // Clean up duplicates where a limbo drone now exists in space
            var allDrones = Enumerable.Empty<AbyssalDrone>()
                .Concat(dronesInSpace)
                .Concat(dronesInBay)
                .Concat(dronesInLimbo)
                .ToList();

            foreach (var drone in dronesInBay)
            {
                _limboDeployingDronesTimer[drone.DroneId] = DateTime.MinValue;
                _recentlyDeployedDronesTimer[drone.DroneId] = DateTime.MinValue;
            }

            // Remove duplicates that might exists in multiple lists
            // Space > Limbo > Inbay
            // If in space and in limbo then should be removed from limbo as it's now outside limbo
            // If in limbo and in bay use limbo as it's deploying
            allDrones = allDrones
                .GroupBy(d => d.DroneId)
                .Select(g => g.OrderBy(d => d.ActionState).First())
                .ToList();

            var validDronesToPick = allDrones.Where(IsValidDroneHealth);


            var entities = GetSortedTargetList(TargetsOnGridWithoutLootTargets);
            double PrioritizeDrone(AbyssalDrone drone)
            {
                // TODO: We should create a priority list once, at the beginning of each stage and use that throughout the current stage -- that way we reduce the possibility of constant drone swaps (is that even a problem?)
                var spawnDpsBandwidthRatio = Math.Round(DirectEntity.CalculateEffectiveDPS(drone.GetInvType?.GetDroneDPS() ?? new Dictionary<DirectDamageType, float>(), entities) / drone.Bandwidth, 2);
                return  4 + spawnDpsBandwidthRatio / 1000;
            }

            var selectableDrones = validDronesToPick
                .OrderByDescending(PrioritizeDrone)
                .ThenByDescending(x => x.DroneId)
                .ThenBy(x =>
                    _recentlyDeployedDronesTimer.ContainsKey(x.DroneId)
                        ? _recentlyDeployedDronesTimer[x.DroneId]
                        : DateTime.MinValue)
                .ThenBy(x =>
                    _recentlyRecalledDronesTimer.ContainsKey(x.DroneId)
                        ? _recentlyRecalledDronesTimer[x.DroneId]
                        : DateTime.MinValue)
                .ThenBy(x => x.StackSize)
                .ThenByDescending(x => x.ArmorPercent)
                .ThenByDescending(x => x.StructurePercent)
                .ToList();

            var dronesIWant = new List<AbyssalDrone>();

            // Handle always adding drones that are in deploying limbo
            //var alwaysSelectedDrones = selectableDrones.Where(drone => drone.ActionState == AbyssalDrone.DroneActionState.DeployingLimbo);
            var alwaysSelectedDrones = selectableDrones.ToList();
            var droneBandwidthAvaliable = (double)ESCache.Instance.DirectEve.ActiveShip.DroneBandwidth;
            var droneCountAvaliable = 5;

            foreach (var alwaysSelectedDrone in alwaysSelectedDrones)
            {
                // Cannot deploy anymore drones
                if (droneCountAvaliable == 0 || alwaysSelectedDrone.Bandwidth > droneBandwidthAvaliable)
                {
                    continue;
                }
                droneBandwidthAvaliable -= alwaysSelectedDrone.Bandwidth;
                droneCountAvaliable--;
                dronesIWant.Add(alwaysSelectedDrone);
            }

            // Handle any additional drones in-bay that could be deployed
            var avaliableDrones = selectableDrones.Except(dronesIWant.ToList()).Where(drone => drone.ActionState == AbyssalDrone.DroneActionState.InBay);
            var dl = GetHighestBandwidthAndAmountDrones(avaliableDrones.ToList(), droneCountAvaliable, (int)droneBandwidthAvaliable);
            //Log($"droneCountAvaliable {droneCountAvaliable} droneBandwidthAvaliable {droneBandwidthAvaliable} GetHighestBandwidthAndAmountDrones_Count {dl.Count}");

            dronesIWant = dronesIWant.Concat(dl).ToList();
            var selectableDronesCopy = selectableDrones.ToList();

            const bool LogState = true;
            if (LogState)
            {
                var changed = dronesIWant.Select(x => x.DroneId).Except(_previouslyDeployedDronesDebug).Any();

                if (changed)
                {
                    foreach (var drone in allDrones)
                    {
                        var isWanted = dronesIWant.Any(e => e.DroneId == drone.DroneId);
                        var isValid = validDronesToPick.Any(e => e.DroneId == drone.DroneId);
                        var recentlyDeployedTimer = _recentlyDeployedDronesTimer.GetValueOrDefault(drone.DroneId);
                        var limboTimer = _limboDeployingDronesTimer.GetValueOrDefault(drone.DroneId);

                        var sb = new StringBuilder();
                        sb.Append("Id=").Append(drone.DroneId).Append("|");
                        sb.Append("TypeName=").Append(drone.TypeName).Append("|");
                        sb.Append("State=").Append(drone.ActionState).Append("|");
                        sb.Append("IsValid=").Append(isValid).Append("|");
                        sb.Append("TypeId=").Append(drone.TypeId).Append("|");
                        sb.Append("Bandwidth=").Append(drone.Bandwidth).Append("|");
                        sb.Append("Wanted=").Append(isWanted).Append("|");
                        sb.Append("Shield=").Append(drone.ShieldPercent).Append("|");
                        sb.Append("RDP=").Append(recentlyDeployedTimer).Append("|");
                        sb.Append("LT=").Append(limboTimer).Append("|");
                        Log(sb.ToString());
                    }

                    _previouslyDeployedDronesDebug.Clear();
                    _previouslyDeployedDronesDebug.AddRange(dronesIWant.Select(x => x.DroneId));
                }
            }

            return dronesIWant;
        }

        public List<AbyssalDrone> GetHighestBandwidthAndAmountDrones(List<AbyssalDrone> drones, int maxDrones, int maxBandwidth)
        {

            if (!drones.Any())
                return new List<AbyssalDrone>();

            if (maxDrones <= 0)
                return new List<AbyssalDrone>();

            if (maxBandwidth <= 0)
                return new List<AbyssalDrone>();

            if (drones.Count <= maxDrones)
                maxDrones = drones.Count;

            if (drones.Sum(d => d.Bandwidth) <= maxBandwidth)
                maxBandwidth = drones.Sum(d => (int)d.Bandwidth);

            // Sort the original list in descending order by bandwidth
            drones = drones.OrderByDescending(d => d.Bandwidth).ToList();

            // Create a 2D array to store the dynamic programming results
            int[,] dp = new int[maxDrones + 1, maxBandwidth + 1];

            // Iterate through each drone in the sorted list
            for (int i = 0; i < drones.Count; i++)
            {
                AbyssalDrone currentDrone = drones[i];

                // Iterate from the maximum number of drones down to 1
                for (int j = maxDrones; j >= 1; j--)
                {
                    // Iterate from the maximum bandwidth down to the current drone's bandwidth
                    for (int k = maxBandwidth; k >= (int)currentDrone.Bandwidth; k--)
                    {
                        // Calculate the new bandwidth value if the current drone is selected
                        int newBandwidth = dp[j - 1, k - (int)currentDrone.Bandwidth] + (int)currentDrone.Bandwidth;

                        // Update the maximum bandwidth value in the DP array
                        if (newBandwidth > dp[j, k])
                        {
                            dp[j, k] = newBandwidth;
                        }
                    }
                }
            }

            List<AbyssalDrone> returnedDrones = new List<AbyssalDrone>();
            List<List<AbyssalDrone>> dronesCache = new List<List<AbyssalDrone>>();

            for (int j = drones.Count - 1; j >= 0; j--)
            {
                // Initialize variables to track remaining bandwidth and number of drones
                int remainingBandwidth = maxBandwidth;
                int remainingDrones = maxDrones;
                // Make a copy of the original list to choose drones from
                var dronesToChooseFrom = drones.ToList();
                // Create a list to store the selected drones
                List<AbyssalDrone> selectedDrones = new List<AbyssalDrone>();
                // Iterate through the sorted list in reverse order
                for (int i = j; i >= 0; i--)
                {
                    AbyssalDrone currentDrone = drones[i % drones.Count];

                    // Check if the current drone can be selected based on remaining bandwidth, remaining drones,
                    // and if selecting it leads to the maximum bandwidth value
                    if (currentDrone.Bandwidth <= remainingBandwidth && remainingDrones > 0 &&
                dp[remainingDrones, remainingBandwidth] == dp[remainingDrones - 1, remainingBandwidth - (int)currentDrone.Bandwidth] + (int)currentDrone.Bandwidth)
                    {
                        // Find the corresponding drone from the original list using the bandwidth value
                        var d = dronesToChooseFrom.FirstOrDefault(x => x.TypeId == currentDrone.TypeId);
                        if (d != null)
                        {
                            // Add the selected drone to the list
                            selectedDrones.Add(d);

                            // Remove the selected drone from the drones to choose from
                            dronesToChooseFrom.Remove(d);

                            // Update remaining bandwidth and number of drones
                            remainingBandwidth -= (int)currentDrone.Bandwidth;
                            remainingDrones--;
                        }
                    }
                }
                if (selectedDrones.Any())
                {
                    dronesCache.Add(selectedDrones);
                }
            }

            if (dronesCache.Count > 0)
            {
                var highestBW = dronesCache.Where(e => e.Sum(d => d.Bandwidth) <= maxBandwidth).OrderByDescending(e => e.Sum(d => d.Bandwidth)).FirstOrDefault().Sum(d => d.Bandwidth);
                // Select the highest drone amount from the group with highest bandwidth
                //Debug.WriteLine($"Highest BW {highestBW}");
                returnedDrones = dronesCache.Where(e => e.Sum(d => d.Bandwidth) == highestBW).OrderByDescending(e => e.Count).FirstOrDefault();
            }

            // Return the list of selected drones
            return returnedDrones;
        }


        private bool LaunchDrones(List<AbyssalDrone> dronesIWant)
        {
            if (!IsOurShipWithintheAbyssBounds(0) && Framework.Me.IsInAbyssalSpace())
                return false;

            var dronesToDeploy = dronesIWant.Where(d => alldronesInBay.Any(dib => dib.ItemId == d.DroneId)).ToList();

            // remove all drones which are below drone launch perc
            dronesToDeploy.RemoveAll(x => (x.ShieldPercent < (_droneLaunchShieldPerc[(int)x.Bandwidth] / 100d)));

            if (!TargetsOnGrid.Any() && Framework.Me.IsInAbyssalSpace())
                return false;

            if (!dronesToDeploy.Any())
                return false;

            if (ESCache.Instance.DirectEve.ActiveDrones.Count >= 5)
                return false;

            var remainingBandwidth = _shipsRemainingBandwidth;
            while (dronesToDeploy.Sum(d => d.Bandwidth) > remainingBandwidth && dronesToDeploy.Any())
            {
                var droneToRemove = dronesToDeploy.OrderBy(e => e.Bandwidth).FirstOrDefault();
                if (droneToRemove == null)
                    break;
                dronesToDeploy.Remove(droneToRemove);
            }

            if (Framework.Me.IsInAbyssalSpace() &&
                (
                _secondsSinceLastSessionChange <= _minSecondsToLaunchDronesAfterSessionChange ||
                 (_secondsSinceLastSessionChange <= 15 && !_majorityOnNPCsAreAgressingCurrentShip))
                )
            {
                Log($"-- The majority of the enemies on grid are NOT attacking us yet, waiting launching the drones. _secondsSinceLastSessionChange [{_secondsSinceLastSessionChange}] _majorityOnNPCsAreAgressingCurrentShip [{_majorityOnNPCsAreAgressingCurrentShip}]");
                return false;
            }

            var droneItems = dronesToDeploy.Select(e => e.GetDirectItem).Where(e => e != null).ToList();
            var droneDeploySuccess = ESCache.Instance.ActiveShip.LaunchDrones(droneItems);

            if (!droneDeploySuccess)
                return false;

            Log($"Launching the following drones:");
            foreach (var drone in dronesToDeploy)
            {
                Log($"Drone TypeName {drone.TypeName} Id {drone.DroneId}");
                UpdateDroneStateForDeployed(drone);
            }

            return true;
        }

        private bool ReturnDrones(List<AbyssalDrone> dronesIWant)
        {
            if (Framework.Me.IsInAbyssalSpace())
            {
                var anyVorton = (TargetsOnGrid.Any(e => e.NPCHasVortonProjectorGuns));
                var timeNeededToGate = _secondsNeededToReachTheGate + _secondsNeededToRetrieveWrecks;
                var timeNeededToClearGrid = GetEstimatedStageRemainingTimeToClearGrid();
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

                if (lostDronesCurrentRoom < 0)
                    lostDronesCurrentRoom = 0;

                if (lostDronesCurrentRoom > 20)
                    lostDronesCurrentRoom = 20;

                if ((CurrentStageRemainingSecondsWithoutPreviousStages <=
                     timeNeededToGate + timeNeededToClearGrid + (lostDronesCurrentRoom * 8)) &&
                    (anyVorton && _droneRecallsStage >= 21) &&
                    Framework.Me.IsInAbyssalSpace() && TargetsOnGrid.Any())
                {
                    Log(
                        $"Blocking to return drones due too many recalls and not enough time left. DroneRecallsStage [{_droneRecallsStage}] TimeNeededToGate [{timeNeededToGate}] TimeNeededToClearGrid [{timeNeededToClearGrid}] CurrentStageRemainingSecondsWithoutPreviousStages [{CurrentStageRemainingSecondsWithoutPreviousStages}]");
                    return false;
                }
            }

            if (!IsOurShipWithintheAbyssBounds() && Framework.Me.IsInAbyssalSpace())
                return false;

            var dronesToRecall = allDronesInSpace
                .Where(dis => !dronesIWant.Any(d => d.DroneId == dis.Id))
                .Where(dis => dis.DroneState != 4)
                .ToList();

            if (!dronesToRecall.Any())
                return false;

            var droneIdsToRecall = dronesToRecall.Select(d => d.Id).ToList();
            var droneRecallSuccess = ESCache.Instance.DirectEve.ActiveShip.ReturnDronesToBay(droneIdsToRecall);
            if (!droneRecallSuccess)
                return false;

            Log($"Recalling the following drones:");
            foreach (var drone in dronesToRecall)
            {
                UpdateDroneStateForRecall(drone.Id);
                Log($"Drone TypeName {drone.TypeName} Id {drone.Id}");
            }

            _droneRecallsStage += dronesToRecall.Count;
            return true;
        }

        private DateTime _lastServerDownDroneRecallStarted = DateTime.MinValue;

        public bool HandleDroneRecallAndDeployment(List<AbyssalDrone> dronesIWant)
        {

            var dronesInSpace = allDronesInSpace;
            var dronesInBay = alldronesInBay;

            if (ESCache.Instance.DirectEve.Me.TimeTillDownTime.TotalSeconds <= 40 &&
                ESCache.Instance.DirectEve.Me.TimeTillDownTime.TotalSeconds >= 0 && allDronesInSpace.Any() ||
                _lastServerDownDroneRecallStarted.AddSeconds(45) > DateTime.UtcNow)
            {
                if (DirectEve.Interval(5000))
                {
                    Log(
                        $"Server will be DOWN in [{ESCache.Instance.DirectEve.Me.TimeTillDownTime.TotalSeconds}] seconds.");
                }

                var droneMaxDist = allDronesInSpace.Any() ? allDronesInSpace.Max(e => e.Distance) : 0;
                var minDroneSpeed = 2000d;
                if (ESCache.Instance.DirectEve.Me.TimeTillDownTime.TotalSeconds < 15 ||
                    ESCache.Instance.DirectEve.Me.TimeTillDownTime.TotalSeconds <= ((droneMaxDist / minDroneSpeed) + 10) ||
                    _lastServerDownDroneRecallStarted.AddSeconds(90) > DateTime.UtcNow)
                {
                    var ds = dronesInSpace.Where(d => d.DroneState != 4).Select(d => d.Id).ToList();
                    if (ds.Any())
                    {
                        Log(
                            $"-- Server will shutdown in less than [{ESCache.Instance.DirectEve.Me.TimeTillDownTime.TotalSeconds}] seconds, recalling all drones.");
                        ESCache.Instance.DirectEve.ActiveShip.ReturnDronesToBay(ds);
                        // keep track of the last recall, because the timer will be gone if it reaches 0
                        _lastServerDownDroneRecallStarted = DateTime.UtcNow;
                        return true;
                    }

                    return false;
                }
            }

            if (ReturnDrones(dronesIWant))
                return true;

            if (!IsOurShipWithintheAbyssBounds())
            {
                if (DirectEve.Interval(15000))
                {
                    Log($"Warn: Ship is not within abyss bounds.");
                }
                return false;
            }

            if (LaunchDrones(dronesIWant))
                return true;

            return false;
        }

        private Dictionary<long, DateTime> _droneRecallTimers = new Dictionary<long, DateTime>();

        private void TrackDroneRecalls()
        {
            var allDronesInSpaceIds = allDronesInSpace.Select(d => d.Id).ToList();
            // we only allow any of the in space drones to be part of the dict
            foreach (var key in _droneRecallTimers.Keys.ToList())
            {
                if (!allDronesInSpaceIds.Any(e => e == key))
                {
                    _droneRecallTimers.RemoveKey(key);
                }
            }

            // add returning drones to the dict
            foreach (var d in allDronesInSpace.Where(e => e.DroneState == 4))
            {
                if (!_droneRecallTimers.ContainsKey(d.Id))
                {
                    _droneRecallTimers[d.Id] = DateTime.UtcNow;
                }
            }
            // enforce list only contains state == 4
            foreach (var d in allDronesInSpace.Where(e => e.DroneState != 4))
            {
                if (_droneRecallTimers.ContainsKey(d.Id))
                {
                    _droneRecallTimers.Remove(d.Id);
                }
            }
        }

        private int DroneReturningSinceSeconds(long droneId)
        {
            if (_droneRecallTimers.ContainsKey(droneId))
            {
                return Math.Abs((int)(DateTime.UtcNow - _droneRecallTimers[droneId]).TotalSeconds);
            }

            return 0;
        }

        private bool ManageDrones()
        {
            if (!DirectEve.HasFrameChanged())
                return false;

            _lastHandleDrones = DateTime.UtcNow;
            TrackDroneRecalls();

            // If we are in a single room abyss, don't do anything currently
            if (_singleRoomAbyssal)
            {
                if (allDronesInSpace.Any())
                {
                    if (RecallDrones())
                        return true;
                }

                return false;
            }

            // If drones are outside of the bounds, recall them.
            if (!AreDronesWithinAbyssBounds)
            {
                if (RecallDronesOutsideAbyss())
                    return true;
            }

            var droneBay = ESCache.Instance.DirectEve.GetShipsDroneBay();
            if (droneBay == null)
            {
                if (DirectEve.Interval(5000))
                {
                    Log($"Warn: DroneBay == null!");
                }
                return false;
            }

            var dronesIWant = GetWantedDronesInSpace();

            if (HandleDroneRecallAndDeployment(dronesIWant))
                return true;

            // TODO: We maybe want to be more specific here? Instead of all enemies, we maybe want to only check for enemies that are in hard to hit in a speed cloud, i.e frig sized ships (done)
            // TODO: Fix this, this is bad
            if (_droneRecallsDueEnemiesBeingInASpeedCloud < 1 && !AreWeInASpeedCloud && AreAllFrigatesInASpeedCloud &&
                TargetsOnGridWithoutLootTargets.Any() && _nextDroneRecallDueEnemiesBeingInASpeedCloud < DateTime.UtcNow && AreWeCurrentlyAttackingAFrigate)
            {
                _droneRecallsDueEnemiesBeingInASpeedCloud++;
                _nextDroneRecallDueEnemiesBeingInASpeedCloud = DateTime.UtcNow.AddSeconds(Rnd.Next(35, 45));
                Log(
                    $"-- All frigate targets are in a speed cloud and we aren't, recalling drones. Amount of recalls this stage including this recall [{_droneRecallsDueEnemiesBeingInASpeedCloud}]");
                if (RecallDrones())
                    return true;
            }

            if (DronesEngageTargets(dronesIWant))
                return true;

            // If the mtu was dropped and scooped again, we don't launch the drones again for the remaining cache
            // Maybe still want to do that?
            if (IsOurShipWithintheAbyssBounds() && _mtuAlreadyDroppedDuringThisStage &&
                TargetsOnGrid.All(e => e.GroupId == 2009) && _getMTUInSpace == null)
            {
                if (allDronesInSpace.Any())
                {
                    if (RecallDrones())
                        return true;
                }
            }

            return false;
        }


        internal bool RecallDrones()
        {
            if (DirectEve.ActiveDrones.Any()
                && (IsOurShipWithintheAbyssBounds() || !Framework.Me.IsInAbyssalSpace())
               )
            {
                if (DirectEve.Interval(2500, 3500))
                {
                    if (DirectEve.ActiveShip.ReturnDronesToBay(DirectEve.ActiveDrones.Where(e => e.DroneState != 4)
                            .Select(e => e.Id).ToList()))
                    {
                        Log($"Calling non returning drones to return to the bay.");
                        _startedToRecallDronesWhileNoTargetsLeft = null;
                        return true;
                    }
                }
            }

            return false;
        }

        internal bool RecallDronesOutsideAbyss()
        {
            if (DirectEve.ActiveDrones.Any() && Framework.Me.IsInAbyssalSpace() && !AreDronesWithinAbyssBounds)
            {
                if (DirectEve.Interval(2500, 3500))
                {
                    var dronesToRecall = DirectEve.ActiveDrones.Where(e => e.DroneState != 4 && !IsSpotWithinAbyssalBounds(e.DirectAbsolutePosition)).ToList();

                    foreach (var recalledDrone in dronesToRecall.ToList())
                    {
                        if (recalledDrone.FollowEntity != null)
                        {
                            if (IsSpotWithinAbyssalBounds(recalledDrone.FollowEntity.DirectAbsolutePosition))
                            {
                                dronesToRecall.Remove(recalledDrone);
                            }
                        }
                    }

                    if (DirectEve.ActiveShip.ReturnDronesToBay(dronesToRecall.Select(e => e.Id).ToList()))
                    {
                        Log($"Drones are not within bounds. Recalling. Furthest drone distance to center [{allDronesInSpace.OrderByDescending(e => e.Distance).FirstOrDefault().DirectAbsolutePosition.GetDistance(AbyssalCenter.DirectAbsolutePosition)}]");
                        _startedToRecallDronesWhileNoTargetsLeft = null;
                        return true;
                    }
                }
            }
            return false;
        }

        private bool _toggle;

        private int _droneEngageCount;

        private bool _isAnyDroneTargetingACache => allDronesInSpace.Any(d => d.FollowEntity?.GroupId == 2009);

        private static long _group1OrSingleTargetId = -1;
        private static long _group2TargetId = -1;

        private DirectEntity _groupTarget1OrSingleTarget => Framework.EntitiesById.ContainsKey(_group1OrSingleTargetId) ? Framework.EntitiesById[_group1OrSingleTargetId] : null;
        private DirectEntity _groupTarget2 => Framework.EntitiesById.ContainsKey(_group2TargetId) ? Framework.EntitiesById[_group2TargetId] : null;

        private bool UseSmallDronesOnCaches(List<DirectEntity> dronesToUse)
        {
            // use small drones on caches
            if (_currentLockedTargets.Any(e => IsEntityWeWantToLoot(e) && e.Distance < _maxDroneRange) &&
                dronesToUse.Count > 0)
            {
                var dronesInSpaceOrderedBySize = dronesToUse
                    .OrderBy(d => d.TryGet<double>("droneBandwidthUsed"))
                    .ThenBy(d => d.Id)
                    .First();
                dronesToUse.Remove(dronesInSpaceOrderedBySize);

                //dronesToUse = largeMedDronesInSpace.Where(i => i.DroneState != 4).OrderBy(d => d.Id).ToList(); 

                var lockedEnts = _currentLockedTargets;
                var caches = lockedEnts.Where(e => IsEntityWeWantToLoot(e) && (e.Distance <= _maxDroneRange))
                    .OrderBy(e => DirectEntity.AnyIntersectionAtThisPosition(e.DirectAbsolutePosition, false,
                        true, true, true, true, true, false).Count()) // pref caches in non speed clouds first
                    .ThenBy(e => e.Id);
                var cacheIds = caches.Select(e => e.Id).OrderByDescending(e => e);
                foreach (var cache in caches)
                {
                    var smallDronesInSpaceWhichAreNotReturning = new List<DirectEntity>()
                        { dronesInSpaceOrderedBySize };
                    // is any drone already targeting this cache
                    if (smallDronesInSpaceWhichAreNotReturning.Any(e => e.FollowId == cache.Id))
                        continue;

                    // get a drone which is NOT targeting a cache
                    var unusedDrone =
                        smallDronesInSpaceWhichAreNotReturning.FirstOrDefault(e => !cacheIds.Contains(e.FollowId));

                    // if there is none left pick one of a group which has the same follow id
                    if (unusedDrone == null)
                    {
                        var followIds = smallDronesInSpaceWhichAreNotReturning.Where(e => e.FollowId > 0)
                            .Select(e => e.FollowId);
                        foreach (var id in followIds)
                        {
                            if (smallDronesInSpaceWhichAreNotReturning.Count(e => e.FollowId == id) > 1)
                            {
                                unusedDrone =
                                    smallDronesInSpaceWhichAreNotReturning.FirstOrDefault(e => e.FollowId == id);
                                break;
                            }
                        }
                    }

                    // pwn that thing
                    if (unusedDrone != null)
                    {
                        if (DirectEve.Interval(1800, 2200))
                        {
                            if (cache.EngageTargetWithDrones(new List<long>() { unusedDrone.Id }))
                            {
                                _droneEngageCount++;
                                Log(
                                    $"(CACHE) Engaging target [{cache.Id}|{cache.TypeName}] with drone [{unusedDrone.Id}|{unusedDrone.TypeName}]");
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private bool DronesEngageTargets(List<AbyssalDrone> dronesIWant)
        {

            var dronesToUse =
                allDronesInSpace.Where(i => i.DroneState != 4).OrderBy(d => d.Id)
                    .ToList(); // All drones which are not returning

            // Let returning drones be able to attack again if they are healthy and wanted
            var returningDrones = allDronesInSpace.Where(i => i.DroneState == 4).OrderBy(d => d.Id).ToList();

            if (returningDrones.Any())
            {
                foreach (var drone in returningDrones)
                {
                    if (!IsSpotWithinAbyssalBounds(drone.DirectAbsolutePosition))
                        continue;

                    if (dronesIWant.All(d => d.DroneId != drone.Id))
                        continue;

                    if (!IsValidDroneHealth(dronesIWant.First(d => d.DroneId == drone.Id)))
                        continue;
                    dronesToUse.Add(drone);
                    Log($"---- Allowing the returning drone [{drone.Id}] to attack again.");
                }
            }

            // Use small drones on caches
            if (UseSmallDronesOnCaches(dronesToUse))
                return true;

            // Engage target
            if (DirectEve.Interval(800, 1200) && _currentLockedTargets.Where(e => e.GroupId != 2009).Any())
            {
                var dronesToChooseFrom = dronesToUse.OrderByDescending(e => e.TryGet<double>("droneBandwidthUsed"))
                    .ThenBy(e => e.Id)
                    .ToList();

                var group1 = dronesToChooseFrom.Where((x, i) => i % 2 == 0).ToList();
                var group2 = dronesToChooseFrom.Where((x, i) => i % 2 == 1).ToList();

                var lockedTargetsAndAttackedByDrones = _currentLockedTargets
                    .Where(e => e.Distance < _maxDroneRange)
                    .Concat(_entitiesCurrentlyAttackedByOurDrones).Where(e => e.GroupId != 2009).DistinctBy(e => e.Id).ToList();

                var target1 = GetSortedTargetList(lockedTargetsAndAttackedByDrones, group1).Skip(0).Take(1).FirstOrDefault();
                var target2 = GetSortedTargetList(lockedTargetsAndAttackedByDrones, group2).Skip(1).Take(1)
                    .FirstOrDefault();

                var t2 = new List<DirectEntity>() { target1, target2 };

                var targetsTargetingDronesCount = TargetsOnGrid.Count(e => e.IsTargetingDrones && e.GroupId != 2009);

                // Drone group splitting
                if (ESCache.Instance.EveAccount.CS.AbyssalMainSetting.SplitDrones &&
                    target1 != null && target2 != null
                    && (t2.All(e => !e.IsRemoteRepairEntity) || (t2.Any(e => e.IsRemoteRepairEntity) && t2.All(e => e.IsNPCFrigate)))
                    && t2.All(e => !e.IsTargetingDrones)
                    && t2.All(e => e.FlatShieldArmorLocalRepairAmountCombined <= 10) // if this is a ent with high local reps, don't split drones
                    && t2.All(e => !e.TypeName.ToLower().Contains("kikimora"))
                    && dronesToUse.Count >= 2
                    && (
                        t2.All(e => e.IsNPCFrigate) // all frigs
                        || (t2.Any(e => e.IsNPCFrigate) && t2.Any(e => e.IsNPCCruiser)) // frigs or cruiser
                        || t2.All(e => e.IsNPCCruiser) // all cruiser
                    )
                   )
                {

                    // Swap targets in case if there is already a member drone of the current group targeting a target of the other group
                    if (group1.Any(e => e.FollowId == target2.Id) || group2.Any(e => e.FollowId == target1.Id))
                    {
                        var tmp = target1;
                        target1 = target2;
                        target2 = tmp;
                    }

                    var asyncDroneId = -1L;
                    // If for whatever reason drones are attacking more than 2 targets and we are currently not attacking a cache
                    var anyDroneAttackingACache = allDronesInSpace.Any(e =>
                        ESCache.Instance.DirectEve.GetEntityById(e.FollowId)?.GroupId == 2009);
                    if (!anyDroneAttackingACache)
                    {
                        var distinctFollowIds = dronesToUse.Where(e => e.FollowId > 0).DistinctBy(e => e.FollowId);
                        if (distinctFollowIds.Count() > 2)
                        {
                            var activeDronesWithAFollowId =
                                dronesToUse.Where(e => e.FollowId > 0).Select(e => e.FollowId);
                            var min = distinctFollowIds
                                .OrderBy(e => activeDronesWithAFollowId.Count(x => x == e.FollowId)).FirstOrDefault();
                            // Remove the third follow id from the list
                            var followIdsCurrentActiveDronesWithoutLowest = activeDronesWithAFollowId
                                .Except(new List<long>() { min.FollowId }).ToList();
                            // Now there are 2 follow id's left, pin them as the current targets 1,2
                            if (followIdsCurrentActiveDronesWithoutLowest.Count() >= 2)
                            {
                                if (ESCache.Instance.DirectEve.EntitiesById.ContainsKey(
                                        followIdsCurrentActiveDronesWithoutLowest[0])
                                    && ESCache.Instance.DirectEve.EntitiesById.ContainsKey(
                                        followIdsCurrentActiveDronesWithoutLowest[1]))
                                {
                                    if (min != null)
                                    {
                                        // Order does not matter, as it's fine if the drones from the least used group attack a target of ANY of the other groups
                                        target1 = ESCache.Instance.DirectEve.EntitiesById[
                                            followIdsCurrentActiveDronesWithoutLowest[0]];
                                        target2 = ESCache.Instance.DirectEve.EntitiesById[
                                            followIdsCurrentActiveDronesWithoutLowest[1]];
                                        asyncDroneId = min.Id;
                                        Log(
                                            $"-- Drones are attacking more than 2 targetes, forcing drone Id [{min.Id}] TypeName [{min.TypeName}] to be again part of the corresponding group.");
                                    }
                                }
                            }
                        }
                    }

                    if (DirectEve.Interval(10000))
                    {
                        Log(
                            $"---------- Group 1 information START ---------- Target TypeName [{target1.TypeName}] Id [{target1.Id}]");
                        foreach (var item in group1)
                        {
                            Log(
                                $"G1 -|- Id [{item.Id}] TypeName [{item.TypeName}] DroneState [{item.DroneState}] FollowId [{item.FollowId}]");
                        }

                        Log($"---------- Group 1 information END ----------");

                        Log(
                            $"---------- Group 2 information START ---------- TypeName [{target2.TypeName}] Target Id [{target2.Id}]");
                        foreach (var item in group2)
                        {
                            Log(
                                $"G2 -|- Id [{item.Id}] TypeName [{item.TypeName}] DroneState [{item.DroneState}] FollowId [{item.FollowId}]");
                        }

                        Log($"---------- Group 2 information END ----------");
                    }

                    int
                        t = _toggle
                            ? 1
                            : 2; // Toggle between group 1 and 2, it works without that, but drone followIds take some time to update, so it should be faster if we toggle between g1 and g2
                    _toggle = !_toggle;

                    for (int i = t; i <= 2; i++)
                    {
                        var currentGroup = i == 1 ? group1 : group2;
                        var newTarget = i == 1 ? target1 : target2;

                        // Make sure every drone of the group is targeting the target
                        var dronesToAttack = new List<DirectEntity>();
                        foreach (var drone in currentGroup)
                        {
                            //if (drone.DroneState == 4) // exclude returning drones
                            //    continue;

                            if ((drone.FollowId == newTarget.Id && drone.DroneState != 0) &&
                                drone.Id != asyncDroneId) // if the drone is already targeting the target, skip it
                                continue;

                            //Log($"G{i}Engage drone Id [{drone.Id}] TypeName [{drone.TypeName}] FollowId [{drone.FollowId}] on target Id [{currentTarget.Id}] TypeName [{currentTarget.TypeName}]");


                            // only engage with non returning drones
                            dronesToAttack.Add(drone);
                        }

                        if (dronesToAttack.Any()) // launch all of each group
                        {
                            var currentTarget = group1 == currentGroup ? _groupTarget1OrSingleTarget : _groupTarget2;
                            var ignore = IgnoreTargetSwap(currentTarget, newTarget, currentGroup) && asyncDroneId == -1L;
                            if (ignore)
                            {
                                Log($"(GROUP) Ignoring to swap drone target. Current target [{currentTarget.TypeName}] Prio [{currentTarget.AbyssalTargetPriority}] New Target [{newTarget.TypeName}] Prio [{newTarget.AbyssalTargetPriority}]");
                            }

                            if (!ignore && newTarget.EngageTargetWithDrones(dronesToAttack.Select(e => e.Id).ToList()))
                            {
                                if (currentGroup == group1)
                                {
                                    _group1OrSingleTargetId = newTarget.Id;
                                }
                                else
                                {
                                    _group2TargetId = newTarget.Id;
                                }

                                _droneEngageCount++;
                                Log(
                                    $"(GROUP) Engaging drones on target Id [{newTarget.Id}] TypeName [{newTarget.TypeName}] Priority [{newTarget.AbyssalTargetPriority}]");
                                return true;
                            }
                        }
                    }
                }
                else
                {
                    // Single target handling
                    var targets = GetSortedTargetList(lockedTargetsAndAttackedByDrones, allDronesInSpace.Where(e => e.DroneState != 4));
                    var target = targets.FirstOrDefault();

                    if (target != null)
                    {
                        var ds = dronesToUse.Where(d => (d.FollowId != target.Id));
                        var ignore = IgnoreTargetSwap(_groupTarget1OrSingleTarget, target, allDronesInSpace);
                        if (ds.Any() && ignore)
                        {
                            Log($"(SINGLE) Ignoring to swap drone taget. Current target [{_groupTarget1OrSingleTarget.TypeName}] Prio [{_groupTarget1OrSingleTarget.AbyssalTargetPriority}] New Target [{target.TypeName}] Prio [{target.AbyssalTargetPriority}]");
                        }

                        if (ds.Any() && !ignore) // is there any drone not attacking our current target
                        {
                            if (target.EngageTargetWithDrones(ds.Select(d => d.Id).ToList()))
                            {
                                _group1OrSingleTargetId = target.Id;
                                //_targetFirstAttackedWhen[target.Id] = DateTime.UtcNow;
                                Log(
                                    $"(SINGLE) Engaging drones on target Id [{target.Id}] TypeName [{target.TypeName}] Priority [{target.AbyssalTargetPriority}]");
                                // only engage with non returning drones
                                _droneEngageCount++;
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }


        private bool IgnoreTargetSwap(DirectEntity oldTarget, DirectEntity newTarget, List<DirectEntity> drones)
        {
            if (newTarget == null)
                return false;

            if (oldTarget == null)
                return false;

            if (oldTarget.Id == newTarget.Id)
                return false;


            // Priority check
            // Reminder: The drone state can fail with auto attack settings ON after they were assigned to a new target after one was killed.
            bool dronesFocusingSingleTarget = drones.Where(e => e.DroneState != 4).All(e => e.DroneState == 1) && drones.Where(e => e.DroneState == 1).Where(e => e.FollowId > 0).Select(e => e.FollowId).Distinct().Count() == 1;
            bool ignoreNewTarget = oldTarget.AbyssalTargetPriority <= newTarget.AbyssalTargetPriority && dronesFocusingSingleTarget;

            // Old target conditions
            var secondsToKillCurrentTargetWithDrones = oldTarget.GetSecondsToKillWithActiveDrones(drones);
            var hasCurrentTargetHighLocalReps = oldTarget.FlatShieldArmorLocalRepairAmountCombined >= 10;

            // New target conditions
            var timeForDronesToGetToTheNewTarget = drones.Count == 0 ? int.MaxValue : (drones.Sum(d => d.DistanceTo(newTarget)) / drones.Count) / 1500;

            Log($"NewTarget [{newTarget.TypeName}] Id [{newTarget.Id}] OldTarget [{oldTarget.TypeName}] Id [{oldTarget.Id}] secondsToKillThatTargetWithDrones [{secondsToKillCurrentTargetWithDrones}] timeForDronesToGetToTheTarget [{timeForDronesToGetToTheNewTarget}] hasTargetHighLocalReps [{hasCurrentTargetHighLocalReps}]");
            ignoreNewTarget = ignoreNewTarget || secondsToKillCurrentTargetWithDrones <= 39 || hasCurrentTargetHighLocalReps || timeForDronesToGetToTheNewTarget > secondsToKillCurrentTargetWithDrones;
            return ignoreNewTarget;
        }
    }
}