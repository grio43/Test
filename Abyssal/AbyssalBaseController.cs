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
using EVESharpCore.Controllers.Questor.Core.Cache;
using EVESharpCore.Framework;
using EVESharpCore.Framework.Lookup;
using SC::SharedComponents.EVE.ClientSettings;
using SC::SharedComponents.EVE.DatabaseSchemas;
using SC::SharedComponents.Extensions;
using SharpDX.Direct2D1;

namespace EVESharpCore.Controllers.Abyssal
{
    public enum AbyssalState
    {
        Start,
        BuyItems,
        Arm,
        IdleInStation,
        TravelToFilamentSpot,
        TravelToBuyLocation,
        TravelToHomeLocation,
        ReplaceShip,
        ActivateShip,
        UseFilament,
        DumpSurveyDatabases,
        TravelToRepairLocation,
        RepairItems,
        AbyssalEnter,
        AbyssalClear,
        UnloadLoot,
        InvulnPhaseAfterAbyssExit,
        PVP,
        TrashItems,
        Error
    }

    public enum MarketGroup
    {
        LightScoutDrone = 837,
        MediumScoutDrone = 838,
        HeavyAttackDrone = 839
    }

    public enum AbyssalStage
    {
        Stage1 = 1,
        Stage2 = 2,
        Stage3 = 3,
    }

    public abstract class AbyssalBaseController : BaseController
    {

        internal AbyssalState _prevState;
        internal AbyssalState _state;
        public AbyssalState State
        {
            get => _state;
            set
            {
                if (_state != value)
                {
                    _prevState = _state;
                    _state = value;
                    Log($"State changed from [{_prevState}] to [{_state}]");
                }
            }
        }

        internal bool AreWeResumingFromACrash;

        internal AbyssalStage CurrentAbyssalStage => _endGate != null ? AbyssalStage.Stage3 : _attemptsToJumpMidgate > 0 ? AbyssalStage.Stage2 : AbyssalStage.Stage1;

        internal bool IsAnyPlayerAttacking => Framework.Entities.Any(e => e.IsPlayer && e.IsAttacking);

        internal float CurrentStageRemainingSeconds => CurrentAbyssalStage == AbyssalStage.Stage1 ?
            _abyssRemainingSeconds - ((20 * 60 / 3) * 2) : CurrentAbyssalStage == AbyssalStage.Stage2 ?
            _abyssRemainingSeconds - ((20 * 60 / 3) * 1) : _abyssRemainingSeconds;

        internal double CurrentStageRemainingSecondsWithoutPreviousStages
        {
            get
            {
                var minus = CurrentAbyssalStage == AbyssalStage.Stage1 ? 800d : CurrentAbyssalStage == AbyssalStage.Stage2 ? 400d : 0d;
                var a = _abyssRemainingSeconds - minus;
                var b = 400 - _secondsSinceLastSessionChange;
                var ret = Math.Min(a, b);
                return ret;
            }
        }

        internal double GetCurrentStageStageSeconds
        {
            get
            {
                var prevWithout = CurrentStageRemainingSecondsWithoutPreviousStages;
                if (prevWithout > 0)
                    prevWithout = 400 - prevWithout;
                else
                    prevWithout = 400 + prevWithout * -1;

                return Math.Min(_secondsSinceLastSessionChange, prevWithout);
            }
        }
        // are we in a single room abyssal? in those you can just activate the last gate, it is not locked
        // if we crash in the third room, after relog, this would evaluate to true. fixed with: && IsAbyssGateOpen
        // if we crash when no enemies are left, still return true -> are we scooping the mtu?

        // IsAbyssGateOpen and enemies on grid is also an indicator! (until enemies are gone)
        internal bool _singleRoomAbyssal => _endGate != null && _attemptsToJumpMidgate == 0 && IsAbyssGateOpen;

        public DirectEve DirectEve => ESCache.Instance.DirectEve;

        public DirectEntity AbyssalCenter => DirectEve.Entities.FirstOrDefault(e => e.TypeId == 47465);


        public bool IsSpotWithinAbyssalBounds(DirectWorldPosition p, long offset = 0)
        {
            if (!ESCache.Instance.DirectEve.Me.IsInAbyssalSpace())
                return false;

            if (offset == 0)
                return AbyssalCenter.DirectAbsolutePosition.GetDistanceSquared(p) <= DirectEntity.AbyssBoundarySizeSquared;

            return AbyssalCenter.DirectAbsolutePosition.GetDistanceSquared(p) <= (DirectEntity.AbyssBoundarySize + offset) * (DirectEntity.AbyssBoundarySize + offset);
        }

        public ClientSetting ClientSetting => ESCache.Instance.EveAccount.ClientSetting;


        internal float _abyssRemainingSeconds => ESCache.Instance.DirectEve.Me.AbyssalRemainingSeconds;

        internal int _maximumLockedTargets => Math.Min(DirectEve.Me.MaxLockedTargets, DirectEve.ActiveShip.MaxLockedTargets);


        internal IEnumerable<DirectEntity> _currentLockingTargets => DirectEve.Entities.Where(e => e.IsTargeting);

        internal IEnumerable<DirectEntity> _currentLockedTargets => DirectEve.Entities.Where(e => e.IsTarget);

        
        public bool AreDronesWithinAbyssBounds => allDronesInSpace.All(d => IsSpotWithinAbyssalBounds(d.DirectAbsolutePosition));

        internal IEnumerable<DirectEntity> _currentLockedAndLockingTargets => _currentLockedTargets.Concat(_currentLockingTargets);

        internal IEnumerable<DirectEntity> _trigItemCaches => DirectEve.Entities.Where(e => IsEntityWeWantToLoot(e));

        internal IEnumerable<DirectEntity> _entitiesCurrentlyAttackedByOurDrones => allDronesInSpace.Where(e => e.DroneState == 1).Where(e => e.FollowEntity != null).Select(e => e.FollowEntity);

        internal bool forceRecreatePath = false;

        internal IEnumerable<DirectEntity> _wrecks =>
            DirectEve.Entities.Where(e => e.BracketType == BracketType.Wreck_NPC || e.BracketType == BracketType.Wreck);

        internal IEnumerable<DirectEntity> _notEmptyWrecks => _wrecks.Where(e => !e.IsEmpty);

        internal int _remainingNonEmptyWrecksAndCacheCount => _wrecks.Count(w => !w.IsEmpty) + _trigItemCaches.Count();

        internal IEnumerable<DirectEntity> _trigItemCachesAndWrecks => _wrecks.Concat(_trigItemCaches);

        internal double _maxTargetRange => DirectEve.ActiveShip.MaxTargetRange;

        internal bool CanAFilamentBeOpened(bool ignoreAbyssTrace = false) => !DirectEve.Entities.Any(e => (e.GroupId == 1246 || (e.GroupId == (int)Group.AbyssalTrace && !ignoreAbyssTrace) || e.BracketType == BracketType.Station || e.BracketType == BracketType.Stargate) && e.Distance <= 1000001) && !Framework.Me.NPCTimerExists && !Framework.Me.PVPTimerExist;

        //internal bool IsAnyOtherPlayerOnGrid => ESCache.Instance.EntitiesNotSelf.Any(e => e.IsPlayer && e.Distance < 1000001 && DirectEve.Standings.GetCorporationRelationship(e.DirectEntity.CorpId) <= 0);
        internal bool IsAnyOtherNonFleetPlayerOnGrid => ESCache.Instance.EntitiesNotSelf.Any(e => e.IsPlayer && e.Distance < 1000001 && Framework.FleetMembers.All(x => x.CharacterId != e.OwnerID));

        internal List<EntityCache> OtherNonFleetPlayersOnGrid => ESCache.Instance.EntitiesNotSelf.Where(e => e.IsPlayer && e.Distance < 1000001 && Framework.FleetMembers.All(x => x.CharacterId != e.OwnerID)).ToList();

        internal bool SkipExtractionNodes { get; set; } = true;

        internal double _currentlyUsedDroneBandwidth => ESCache.Instance.DirectEve.ActiveDrones.Sum(d => d.TryGet<double>("droneBandwidthUsed"));

        internal double _shipsRemainingBandwidth => ESCache.Instance.DirectEve.ActiveShip.GetRemainingDroneBandwidth();



        internal IEnumerable<DirectUIModule> ShieldBoosters => DirectEve.Modules.Where(e => e.GroupId == (int)Group.ShieldBoosters && e.IsOnline && e.HeatDamagePercent <= 99);

        internal IEnumerable<DirectUIModule> ShieldHardeners => DirectEve.Modules.Where(e => e.GroupId == (int)Group.ShieldHardeners && e.IsOnline && e.HeatDamagePercent <= 99);

        internal IEnumerable<DirectUIModule> PropMods => DirectEve.Modules.Where(e => e.GroupId == (int)Group.Afterburner && e.IsOnline && e.HeatDamagePercent <= 99);



        internal bool IsEntityWeWantToLoot(DirectEntity entity)
        {

            if (SkipExtractionNodes && (entity.TypeId == 49662 || entity.TypeId == 49661)) // Triglavian Extraction Node // Triglavian Extraction SubNode
                return false;

            if (entity.GroupId == 2009)
                return true;

            if (entity.IsEmpty && entity.BracketType == BracketType.Wreck_NPC)
                return false;

            return false;
        }

        internal int _attemptsToJumpMidgate;

        internal const int _smallMutatedDroneTypeId = 60478;
        internal const int _mediumMutatedDroneTypeId = 60479;
        internal const int _heavyMutatedDroneTypeId = 60480;

        internal int _attemptsToJumpFrigateDestroyerAbyss;

        internal DirectEntity _midGate =>
            DirectEve.Entities.FirstOrDefault(e => e.TypeId == 47685 && e.BracketType == BracketType.Warp_Gate);

        internal DirectEntity _endGate =>
            DirectEve.Entities.FirstOrDefault(e => e.TypeId == 47686 && e.BracketType == BracketType.Warp_Gate);

        internal DirectEntity _nextGate => _midGate ?? _endGate;

        internal bool IsAbyssGateOpen => _nextGate.IsAbyssGateOpen();

        internal bool _prevIsAbyssGateOpen;

        internal int _remainingDronesInBay => DirectEve?.GetShipsDroneBay()?.Items?.Sum(i => i.Quantity) ?? 0;

        internal List<DirectItem> _getDronesInBay(MarketGroup marketGroup) => DirectEve?.GetShipsDroneBay()?.Items?.Where(d => d.MarketGroupId == (int)marketGroup)?.ToList() ?? new List<DirectItem>();

        internal List<DirectItem> _getDronesInBayByTypeId(int typeId) => DirectEve?.GetShipsDroneBay()?.Items?.Where(d => d.TypeId == typeId)?.ToList() ?? new List<DirectItem>();

        internal List<DirectEntity> _getDronesInSpace(MarketGroup marketGroup) => DirectEve?.ActiveDrones?.Where(d => d.MarketGroupId == (int)marketGroup)?.ToList() ?? new List<DirectEntity>();

        internal List<DirectEntity> _getDronesInSpaceByTypeId(int typeId) => DirectEve?.ActiveDrones?.Where(d => d.TypeId == typeId)?.ToList() ?? new List<DirectEntity>();

        internal bool _isInLastRoom => _endGate != null;

        internal DateTime _lastFilamentActivation;

        internal DirectItem _getMTUInBay => ESCache.Instance.DirectEve.GetShipsCargo().Items.FirstOrDefault(i => i.GroupId == 1250);

        internal DirectEntity _getMTUInSpace => ESCache.Instance.DirectEve.Entities.FirstOrDefault(i => i.GroupId == 1250);

        internal bool _MTUAvailable => _getMTUInBay != null || _getMTUInSpace != null;

        internal DateTime _lastMTULaunch;

        internal bool _MTUReady => _lastMTULaunch.AddSeconds(11) < DateTime.UtcNow;

        internal DateTime _timeStarted;

        internal double _valueLooted;

        internal List<DirectItem> smallDronesInBay => _getDronesInBay(MarketGroup.LightScoutDrone).Concat(_getDronesInBayByTypeId(_smallMutatedDroneTypeId)).ToList();
        internal List<DirectItem> mediumDronesInBay => _getDronesInBay(MarketGroup.MediumScoutDrone).Concat(_getDronesInBayByTypeId(_mediumMutatedDroneTypeId)).ToList();
        internal List<DirectItem> largeDronesInBay => _getDronesInBay(MarketGroup.HeavyAttackDrone).Concat(_getDronesInBayByTypeId(_heavyMutatedDroneTypeId)).ToList();
        internal List<DirectEntity> smallDronesInSpace => _getDronesInSpace(MarketGroup.LightScoutDrone).Concat(_getDronesInSpaceByTypeId(_smallMutatedDroneTypeId)).ToList();
        internal List<DirectEntity> mediumDronesInSpace => _getDronesInSpace(MarketGroup.MediumScoutDrone).Concat(_getDronesInSpaceByTypeId(_mediumMutatedDroneTypeId)).ToList();
        internal List<DirectEntity> largeDronesInSpace => _getDronesInSpace(MarketGroup.HeavyAttackDrone).Concat(_getDronesInSpaceByTypeId(_heavyMutatedDroneTypeId)).ToList();
        internal List<DirectItem> alldronesInBay => smallDronesInBay.Concat(mediumDronesInBay).Concat(largeDronesInBay).ToList();
        internal List<DirectEntity> allDronesInSpace => smallDronesInSpace.Concat(mediumDronesInSpace).Concat(largeDronesInSpace).ToList();
        internal List<DirectEntity> largeMedDronesInSpace => mediumDronesInSpace.Concat(largeDronesInSpace).ToList();
        public double _secondsSinceLastSessionChange => (DateTime.UtcNow - DirectSession.LastSessionChange).TotalSeconds;

        public bool IsOurShipWithintheAbyssBounds(int offset = 0) => IsSpotWithinAbyssalBounds(ESCache.Instance.DirectEve.ActiveShip.Entity.DirectAbsolutePosition, offset);

        internal DateTime _lastMTUScoop;
        /// <summary>
        /// Ensure that the container is open before calling this.
        /// </summary>
        /// <param name="typeId"></param>
        /// <returns></returns>
        internal int GetAmountofTypeIdLeftInCargo(int typeId, bool isMutated = false)
        {
            var shipsCargo = ESCache.Instance.DirectEve.GetShipsCargo();
            return shipsCargo.Items.Where(i => (i.TypeId == typeId && !isMutated) || (isMutated && i.IsDynamicItem && i.OrignalDynamicItem.TypeId == typeId)).Sum(e => e.Stacksize);
        }

        /// <summary>
        /// Ensure that the container is open before calling this.
        /// </summary>
        /// <param name="typeId"></param>
        /// <returns></returns>
        internal int GetAmountofTypeIdLeftItemhangar(int typeId, bool isMutated = false)
        {
            var itemHangar = ESCache.Instance.DirectEve.GetItemHangar();
            return itemHangar.Items
                .Where(i => (i.TypeId == typeId && !isMutated) ||
                            (isMutated && i.IsDynamicItem && i.OrignalDynamicItem.TypeId == typeId))
                .Sum(e => e.Stacksize);
        }

        /// <summary>
        /// Ensure that the container is open before calling this.
        /// </summary>
        /// <param name="typeId"></param>
        /// <returns></returns>
        internal int GetAmountofTypeIdLeftItemhangarAndCargo(int typeId, bool isMutated = false)
        {
            return GetAmountofTypeIdLeftInCargo(typeId, isMutated) + GetAmountofTypeIdLeftItemhangar(typeId, isMutated);
        }

        /// <summary>
        /// Ensure that the container is open before calling this.
        /// </summary>
        /// <param name="typeId"></param>
        /// <returns></returns>
        internal int GetAmountofTypeIdLeftItemhangarAndDroneBay(int typeId, bool isMutated = false)
        {
            return GetAmountOfTypeIdLeftInDroneBay(typeId, isMutated) + GetAmountofTypeIdLeftItemhangar(typeId, isMutated);
        }

        /// <summary>
        /// Ensure that the container is open before calling this.
        /// </summary>
        /// <param name="typeId"></param>
        /// <returns></returns>
        internal int GetAmountOfTypeIdLeftInDroneBay(int typeId, bool isMutated = false)
        {
            if (!Framework.ActiveShip.HasDroneBay)
                return 0;

            var droneBay = ESCache.Instance.DirectEve.GetShipsDroneBay();
            return droneBay.Items.Where(i => (i.TypeId == typeId && !isMutated) || (isMutated && i.IsDynamicItem && i.OrignalDynamicItem.TypeId == typeId)).Sum(e => e.Stacksize);
        }

        public void LogNextGateState()
        {
            var isOpen = IsAbyssGateOpen;
            if (_prevIsAbyssGateOpen != isOpen)
            {
                Log($"IsAbyssgateOpen changed value. Current [{isOpen}]");
                _prevIsAbyssGateOpen = isOpen;
            }
        }

        public string GenerateGridDump(IEnumerable<DirectEntity> ents, bool includeEhp = true)
        {
            //var ents = DirectEve.Entities.Where(e => e.IsNPCByBracketType || e.GroupId == 2009);
            var distinctTypes = ents.DistinctBy(e => e.TypeId);
            var ret = string.Empty;
            foreach (var type in distinctTypes.OrderBy(e => e.AbyssalTargetPriority))
            {
                var count = ents.Count(e => e.TypeId == type.TypeId);
                if (includeEhp)
                    ret += $"{count}x {type.TypeName}[{Math.Round(type.ShieldPct, 2)} {Math.Round(type.ArmorPct, 2)} {Math.Round(type.StructurePct, 2)}],";
                else
                    ret += $"{count}x {type.TypeName},";
            }

            if (ret.Length > 1)
                ret = ret.Remove(ret.Length - 1);
            return ret;
        }

        public bool LaunchMTU()
        {

            var mtu = _getMTUInBay;

            if (mtu != null && DirectEve.Interval(2000, 3000))
            {
                Log($"Launching the MTU.");
                _lastMTULaunch = DateTime.UtcNow;
                mtu.LaunchForSelf();
                return true;
            }
            return false;
        }

        public bool ScoopMTU()
        {
            var mtu = _getMTUInSpace;
            if (mtu != null && mtu.Distance < 2500 && DirectEve.Interval(2000, 3000))
            {
                Log($"Scooping the MTU.");
                _lastMTUScoop = DateTime.UtcNow;
                mtu.Scoop();
                return true;
            }
            return false;

        }

        public AbyssalBaseController()
        {
            ESCache.Instance.InitInstances();
            Form = new AbyssalControllerForm(this);
            _timeStarted = DateTime.UtcNow;
            _valueLooted = 0;
            // if we start in abyss space, we assume we have to recover from a crash

            AreWeResumingFromACrash = false;
            //AreWeResumingFromACrash = ESCache.Instance.DirectEve.Me.IsInAbyssalSpace(); // need a workaround for that
        }

        internal void UpdateIskLabel(double value)
        {
            var frm = this.Form as AbyssalControllerForm;
            frm.IskPerHLabel.Invoke(new Action(() =>
            {
                frm.IskPerHLabel.Text = Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);
            }));
        }

        internal void UpdateStageEHPValues(double v1, double v2)
        {
            var frm = this.Form as AbyssalControllerForm;
            frm.TotalStageEhp.Invoke(new Action(() =>
            {
                frm.TotalStageEhp.Text = Math.Round(v1, 2).ToString(CultureInfo.InvariantCulture) + "/" + Math.Round(v2, 2).ToString(CultureInfo.InvariantCulture);
            }));
        }

        internal void UpdateStageKillEstimatedTime(double v1)
        {
            var frm = this.Form as AbyssalControllerForm;
            frm.EstimatedNpcKillTime.Invoke(new Action(() =>
            {
                frm.EstimatedNpcKillTime.Text = Math.Round(v1, 2).ToString(CultureInfo.InvariantCulture);
            }));
        }

        internal void UpdateIgnoreAbyssEntities(bool v1)
        {
            var frm = this.Form as AbyssalControllerForm;
            frm.IgnoreAbyssEntities.Invoke(new Action(() =>
            {
                frm.IgnoreAbyssEntities.Text = v1.ToString();
            }));
        }

        internal void UpdateStageLabel(AbyssalStage value)
        {
            var frm = this.Form as AbyssalControllerForm;
            frm.StageLabel.Invoke(new Action(() =>
            {
                frm.StageLabel.Text = value.ToString();
            }));
        }

        internal void UpdateStageRemainingSecondsLabel(int value)
        {
            var frm = this.Form as AbyssalControllerForm;
            frm.StageRemainingSeconds.Invoke(new Action(() =>
            {
                frm.StageRemainingSeconds.Text = value.ToString();
            }));
        }


        internal void UpdateTimeNeededToGetToTheGate(int value)
        {
            var frm = this.Form as AbyssalControllerForm;
            frm.TimeNeededToGetToTheGate.Invoke(new Action(() =>
            {
                frm.TimeNeededToGetToTheGate.Text = value.ToString();
            }));
        }



        internal void UpdateWreckLootTime(int value)
        {
            var frm = this.Form as AbyssalControllerForm;
            frm.WreckLootTime.Invoke(new Action(() =>
            {
                frm.WreckLootTime.Text = value.ToString();
            }));
        }


        internal void UpdateAbyssTotalTime(int value)
        {
            var frm = this.Form as AbyssalControllerForm;
            frm.AbyssTotalTime.Invoke(new Action(() =>
            {
                frm.AbyssTotalTime.Text = value.ToString();
            }));
        }

    }
}
