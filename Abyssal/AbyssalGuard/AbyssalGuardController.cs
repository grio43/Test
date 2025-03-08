//
// (c) duketwo 2023
//

extern alias SC;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using EVESharpCore.Cache;
using EVESharpCore.Controllers.Base;
using EVESharpCore.Controllers.Questor.Core.States;
using EVESharpCore.Framework;
using EVESharpCore.Framework.Events;
using EVESharpCore.Framework.Lookup;
using SC::SharedComponents.Extensions;
using SC::SharedComponents.IPC;
using SC::SharedComponents.EVE.ClientSettings.AbyssalGuard.Main;
using SC::SharedComponents.Events;
using SC::SharedComponents.Utility;
using ServiceStack;

namespace EVESharpCore.Controllers.Abyssal.AbyssalGuard
{
    public class AbyssalGuardController : BaseController
    {
        internal enum AbyssalGuardState
        {
            Start,
            PickRandomMoon,
            GotoAbyssalWarpinspot,
            CreateAbyssalWarpinspot,
            CloakAndWaitForTargets,
            WarpToAbyssalRunner,
            PVP,
            Error,
        }

        public AbyssalGuardController()
        {
            DirectSession.OnSessionReadyEvent += OnSessionReadyHandler;
            OnSessionReadyHandler(null, null);
        }

        internal string _homeStationBookmarkName =>
            ESCache.Instance.EveAccount.ClientSetting.AbyssalGuardMainSetting.AbyssalHomeBookmarkName ?? string.Empty;

        internal string _abyssBookmarkName =>
            ESCache.Instance.EveAccount.ClientSetting.AbyssalGuardMainSetting.AbyssalBookmarkName ?? string.Empty;

        internal string _abyssalRunnerCharName =>
            ESCache.Instance.EveAccount.ClientSetting.AbyssalGuardMainSetting.AbyssCharacterName;

        internal bool IsAnyOtherNonFleetPlayerOnGrid => ESCache.Instance.EntitiesNotSelf.Any(e =>
            e.IsPlayer && e.Distance < 1000001 && Framework.FleetMembers.All(x => x.CharacterId != e.OwnerID));

        List<DirectEntity> OtherNonFleetPlayersOnGrid => ESCache.Instance.EntitiesNotSelf
            .Where(e => e.IsPlayer && e.Distance < 1000001 &&
                        Framework.FleetMembers.All(x => x.CharacterId != e.OwnerID)).Select(e => e.DirectEntity)
            .ToList();

        internal int _orcaTypeId = 28606;
        private long? _selectedMoonId = null;

        internal AbyssalGuardState _prevState;
        internal AbyssalGuardState _state;
        private const double _minAbyssBookmarkDistance = 310_000;

        private const double
            _maxAbyssBookmarkDistance = 1_600_000; // Is 1.6kk still on grid nowadays? We need eyes on the trace

        private const double _errorDistanceOnLineBMs = 25_000;
        private const double _nextToBookmarkDistance = 75_000;
        private const double _closeToMoonDistance = 9_999_000;
        private DirectBookmark GetAbyssBookmark() => GetBookmarkByName(_abyssBookmarkName);
        private DirectBookmark GetHomeStationBookmark() => GetBookmarkByName(_homeStationBookmarkName);

        internal AbyssalGuardState State
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

        internal bool DockedInHomeBookmarkLocation()
        {
            var hbm = ESCache.Instance.DirectEve.Bookmarks.OrderByDescending(e => e.IsInCurrentSystem)
                .FirstOrDefault(b => b.Title == _homeStationBookmarkName);
            if (hbm != null)
            {
                return hbm.DockedAtBookmark();
            }

            return false;
        }

        internal DirectBookmark GetBookmarkByName(string name)
        {
            var hbm = ESCache.Instance.DirectEve.Bookmarks.OrderByDescending(e => e.IsInCurrentSystem)
                .FirstOrDefault(b => b.Title == name);
            if (hbm != null)
            {
                return hbm;
            }

            return null;
        }

        internal bool DoesBookmarkExist(string bmName)
        {
            return Framework.Bookmarks.Any(b => b.Title == bmName);
        }

        private bool IsPointOnLine(Vec3 p1, Vec3 p2, Vec3 randomPoint, double errorRate = 0.0)
        {
            // Calculate the vector between the two line endpoints
            double lineX = p2.X - p1.X;
            double lineY = p2.Y - p1.Y;
            double lineZ = p2.Z - p1.Z;

            // Calculate the vector between the first endpoint and the random point
            double pointX = randomPoint.X - p1.X;
            double pointY = randomPoint.Y - p1.Y;
            double pointZ = randomPoint.Z - p1.Z;

            // Calculate the cross product of the two vectors
            double crossProductX = lineY * pointZ - lineZ * pointY;
            double crossProductY = lineZ * pointX - lineX * pointZ;
            double crossProductZ = lineX * pointY - lineY * pointX;

            // Calculate the magnitude of the cross product
            double crossProductMagnitude = Math.Sqrt(crossProductX * crossProductX + crossProductY * crossProductY +
                                                     crossProductZ * crossProductZ);

            // Calculate the length of the line segment
            double lineLength = Math.Sqrt(lineX * lineX + lineY * lineY + lineZ * lineZ);

            // Calculate the distance between the random point and the line segment
            double distance = crossProductMagnitude / lineLength;

            // Check if the distance is within the error rate
            return distance <= errorRate;
        }

        private List<DirectBookmark> GetAllBookmarksWithinRange(double min, double max, DirectBookmark bookmark)
        {
            var r = new List<DirectBookmark>();

            foreach (var bm in Framework.Bookmarks)
            {
                if (bm.DistanceTo(bookmark) >= min && bm.DistanceTo(bookmark) <= max)
                {
                    r.Add(bm);
                }
            }

            return r;
        }

        private bool IsSelectedRandomMoonValid =>
            _selectedMoonId != null && Framework.EntitiesById.ContainsKey(_selectedMoonId.Value);

        private bool CheckBookmarksExist()
        {
            var abyssBookmark = GetAbyssBookmark();
            if (abyssBookmark == null)
            {
                Log($"Abyss bookmark is null.");
                State = AbyssalGuardState.Error;
                return false;
            }

            var homeStationBookmark = GetHomeStationBookmark();
            if (homeStationBookmark == null)
            {
                Log($"Home station bookmark is null.");
                State = AbyssalGuardState.Error;
                return false;
            }

            return true;
        }

        private List<DirectBookmark> GetBookmarksOnLineBetweenEntityAndAbyssSpot(DirectEntity entity,
            double errorRate = _errorDistanceOnLineBMs, double minDistance = _minAbyssBookmarkDistance,
            double maxDistance = _maxAbyssBookmarkDistance)
        {
            var r = new List<DirectBookmark>();
            var abyssBookmark = GetAbyssBookmark();

            if (abyssBookmark == null || entity == null || !entity.IsValid)
                return r;

            return GetBookmarksOnlineBetweenPoints(abyssBookmark.Pos, entity.Position, errorRate,
                minDistance, maxDistance);
        }

        private List<DirectBookmark> GetBookmarksOnLineBetweenChosenMoonAndAbyssSpot(
            double minDistance = _minAbyssBookmarkDistance, double maxDistance = _maxAbyssBookmarkDistance)
        {
            var id = _selectedMoonId ?? 0;
            if (!Framework.EntitiesById.ContainsKey(id))
                return new List<DirectBookmark>();

            return GetBookmarksOnLineBetweenEntityAndAbyssSpot(Framework.EntitiesById[_selectedMoonId.Value],
                minDistance: minDistance, maxDistance: _maxAbyssBookmarkDistance);
        }

        private List<DirectBookmark> GetBookmarksOnlineBetweenPoints(Vec3 p1, Vec3 p2, double errorRate,
            double minDistance = 0, double maxDistance = _maxAbyssBookmarkDistance)
        {
            var r = new List<DirectBookmark>();

            foreach (var bm in Framework.Bookmarks)
            {
                if (IsPointOnLine(p1, p2, bm.Pos, errorRate))
                {
                    if (bm.DistanceTo(p1) < minDistance)
                        continue;

                    if (bm.DistanceTo(p1) > maxDistance)
                        continue;

                    r.Add(bm);
                }
            }

            return r;
        }

        public bool AreWeCloseToTheChosenMoon()
        {
            if (_selectedMoonId == null)
                return false;

            var moon = Framework.EntitiesById[_selectedMoonId ?? 0];

            if (moon == null || !moon.IsValid)
                return false;

            return moon.Distance <= _closeToMoonDistance;
        }

        private (bool, DirectBookmark) AreWeCloseToOneBookmarkWhichIsOnLineBetweenAbyssSpotAndChosenMoon()
        {
            var abyssBookmark = GetAbyssBookmark();
            if (abyssBookmark == null)
                return (false, null);

            var bmsOnline = GetBookmarksOnLineBetweenChosenMoonAndAbyssSpot();
            if (!bmsOnline.Any())
            {
                State = AbyssalGuardState.CreateAbyssalWarpinspot;
                return (false, null);
            }

            if (bmsOnline.Any(b => b.DistanceTo(Framework.ActiveShip.Entity) < _nextToBookmarkDistance))
            {
                var cloest = bmsOnline.Where(b =>
                        b.DistanceTo(Framework.ActiveShip.Entity) < _nextToBookmarkDistance)
                    .OrderBy(b => b.DistanceTo(Framework.ActiveShip.Entity)).FirstOrDefault();
                return (true, cloest);
            }

            return (false, null);
        }

        private DateTime _lastTransitionFromNotWarpingToWarpingCapture = DateTime.MinValue;
        private DateTime _nextMwdDisable = DateTime.MinValue;

        private void HandleCloak()
        {
            if (!Framework.Session.IsInSpace)
                return;

            if (State == AbyssalGuardState.CloakAndWaitForTargets)
                return;

            // deactivate cloak
            var cloaks = Framework.Modules.Where(e => e.GroupId == (int)Group.CloakingDevice).ToList();
            foreach (var cloak in cloaks)
            {
                if (cloak.IsInLimboState)
                    continue;

                if (cloak.IsActive)
                {
                    Log($"Trying to de-activate module [{cloak.TypeName}].");
                    cloak.Click();
                }
            }
        }

        private enum WarpState
        {
            NotWarping,
            Warping,
        }

        private WarpState _warpState;

        private void TrackWarpsAndActivateMWDAccordingly()
        {
            if (!Framework.Session.IsInSpace)
                return;

            if (Framework.ActiveShip.Entity.IsWarping)
                return;

            switch (_warpState)
            {
                case WarpState.NotWarping:
                    if (Framework.Me.IsWarpingByMode)
                    {
                        _lastTransitionFromNotWarpingToWarpingCapture = DateTime.UtcNow;
                        _warpState = WarpState.Warping;
                    }
                    else
                    {
                        _warpState = WarpState.NotWarping;
                    }

                    break;
                case WarpState.Warping:
                    if (Framework.Me.IsWarpingByMode)
                    {
                        _warpState = WarpState.Warping;
                    }
                    else
                    {
                        _warpState = WarpState.NotWarping;
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            if (_lastTransitionFromNotWarpingToWarpingCapture.AddSeconds(8) > DateTime.UtcNow)
            {
                var mwd = Framework.Modules.FirstOrDefault(m => m.GroupId == (int)Group.Afterburner);
                if (mwd != null)
                {
                    if (mwd.IsInLimboState)
                        return;

                    if (mwd.IsActive)
                    {
                        if (_nextMwdDisable < DateTime.UtcNow)
                        {
                            Log($"Deactivating MWD.");
                            mwd.Click();
                            _lastTransitionFromNotWarpingToWarpingCapture = DateTime.MinValue;
                        }

                        return;
                    }

                    if (Framework.ActiveShip.Entity.Velocity > 10000)
                        return;

                    Log($"Activating MWD.");
                    _nextMwdDisable = DateTime.UtcNow.AddMilliseconds(GetRandom(2500, 3500));
                    mwd.Click();
                    return;
                }
            }
        }

        private void EnsureHangarAccess()
        {
            if (!DirectEve.Interval(3500, 7500))
                return;

            if (Framework.Session.IsInDockableLocation)
                return;

            if (ESCache.Instance.EveAccount.ClientSetting.AbyssalGuardMainSetting.GuardMode ==
                AbyssalGuardMode.Orca && Framework.ActiveShip.TypeId == _orcaTypeId)
            {
                if (Framework.ActiveShip.GetShipConfigOption(DirectActiveShip.ShipConfigOption
                        .FleetHangar_AllowCorpAccess) == false)
                {
                    if (Framework.ActiveShip.ToggleShipConfigOption(DirectActiveShip.ShipConfigOption
                            .FleetHangar_AllowCorpAccess))
                    {
                        Log($"Enabling Fleet Hangar access for corp.");
                        return;
                    }
                }

                if (Framework.ActiveShip.GetShipConfigOption(DirectActiveShip.ShipConfigOption
                        .FleetHangar_AllowFleetAccess) == false)
                {
                    if (Framework.ActiveShip.ToggleShipConfigOption(DirectActiveShip.ShipConfigOption
                            .FleetHangar_AllowFleetAccess))
                    {
                        Log($"Enabling Fleet Hangar access for fleet.");
                        return;
                    }
                }

                if (Framework.ActiveShip.GetShipConfigOption(DirectActiveShip.ShipConfigOption
                        .SMB_AllowCorpAccess) == false)
                {
                    if (Framework.ActiveShip.ToggleShipConfigOption(DirectActiveShip.ShipConfigOption
                            .SMB_AllowCorpAccess))
                    {
                        Log($"Enabling SMB access for corp.");
                        return;
                    }
                }

                if (Framework.ActiveShip.GetShipConfigOption(DirectActiveShip.ShipConfigOption
                        .SMB_AllowFleetAccess) == false)
                {
                    if (Framework.ActiveShip.ToggleShipConfigOption(DirectActiveShip.ShipConfigOption
                            .SMB_AllowFleetAccess))
                    {
                        Log($"Enabling SMB access for fleet.");
                        return;
                    }
                }
            }
        }

        private bool HandleOverheat()
        {

            var heatDamageMax = 80;
            var medRackHeatEnableThreshold = 0.50d;
            var medRackHeatDisableThreshold = 0.65d;
            var medRackHeatStatus = ESCache.Instance.ActiveShip.MedHeatRackState(); // medium rack heat state
            var hardeners = Framework.Modules.Where(e => e.GroupId == (int)Group.ShieldHardeners).ToList();
            var anyModuleAboveThreshold = Framework.Modules.Where(m => m.HeatDamagePercent > 89);

            if (Framework.Entities.Any(e => e.IsPlayer && e.IsAttacking) && !anyModuleAboveThreshold.Any() && medRackHeatStatus <= medRackHeatDisableThreshold)
            {
                // Enabled overheat
                foreach (var hardener in hardeners.Where(e => !e.IsOverloaded))
                {
                    if (medRackHeatStatus >= medRackHeatEnableThreshold)
                        break;

                    if (hardener.IsOverloadLimboState || hardener.IsPendingOverloading || hardener.IsInLimboState)
                        continue;

                    if (hardener.ToggleOverload())
                    {
                        Log($"Toggling overload state ON. TypeName [{hardener.TypeName}] Id [{hardener.ItemId}]");
                        return true;
                    }
                }
            }
            else
            {
                // Disable overheat
                foreach (var hardener in hardeners.Where(e => e.IsOverloaded))
                {

                    if (hardener.IsOverloadLimboState || hardener.IsPendingStopOverloading || hardener.IsInLimboState)
                        continue;

                    if (hardener.ToggleOverload())
                    {
                        Log($"Toggling overload state OFF. TypeName [{hardener.TypeName}] Id [{hardener.ItemId}]");
                        return true;
                    }
                }

            }
            return false;
        }

        internal bool IsInHomeSystem()
        {
            var hbm = ESCache.Instance.DirectEve.Bookmarks.OrderByDescending(e => e.IsInCurrentSystem)
                .FirstOrDefault(b => b.Title == _homeStationBookmarkName);
            if (hbm != null)
            {
                return hbm.IsInCurrentSystem;
            }

            return false;
        }

        internal bool DoesHomeStationBookmarkExist()
        {
            return ESCache.Instance.DirectEve.Bookmarks.OrderByDescending(e => e.IsInCurrentSystem)
                .FirstOrDefault(b => b.Title == _homeStationBookmarkName) != null;
        }

        public override void DoWork()
        {
            try
            {
                if (State != AbyssalGuardState.Error && ESCache.Instance.InSpace && (_selectedMoonId == null ||
                        !Framework.EntitiesById.ContainsKey(_selectedMoonId.Value)))
                {
                    State = AbyssalGuardState.PickRandomMoon;
                }

                if (!DoesHomeStationBookmarkExist())
                {
                    Log($"Home station bookmark does not exist. Please create a bookmark with the name {_homeStationBookmarkName}.");
                    State = AbyssalGuardState.Error;
                    return;
                }

                if (!DoesBookmarkExist(_abyssBookmarkName))
                {
                    Log($"Abyssal bookmark does not exist. Please create a bookmark with the name {_abyssBookmarkName}.");
                    State = AbyssalGuardState.Error;
                    return;
                }

                if (!IsInHomeSystem())
                {
                    Log($"Not in home system. Please move to the home system.");
                    State = AbyssalGuardState.Error;
                    return;
                }

                if (ESCache.Instance.EveAccount.ClientSetting.AbyssalGuardMainSetting.GuardMode ==
                    AbyssalGuardMode.ConcordSpawner)
                {
                    Log($"Concord spawner mode is not yet implemented.");
                    State = AbyssalGuardState.Error;
                    return;
                }


                TrackWarpsAndActivateMWDAccordingly();
                HandleCloak();
                EnsureHangarAccess();

                if (HandleOverheat())
                    return;

                if (DirectEve.Interval(30000))
                    DirectEventManager.NewEvent(new DirectEvent(DirectEvents.KEEP_ALIVE, "Keep alive."));

                switch (State)
                {
                    case AbyssalGuardState.Start:

                        // if (ESCache.Instance.EveAccount.ClientSetting.AbyssalGuardMainSetting.GuardMode ==
                        //     AbyssalGuardMode.Orca && Framework.ActiveShip.TypeId != _orcaTypeId)
                        // {
                        //     Log(
                        //         $"Guardmode is [{ESCache.Instance.EveAccount.ClientSetting.AbyssalGuardMainSetting.GuardMode}] but current ship is not an Orca!");
                        //     State = AbyssalGuardState.Error;
                        //     return;
                        // }

                        if (!CheckBookmarksExist())
                            return;

                        if (EnsureInSpace())
                            State = AbyssalGuardState.GotoAbyssalWarpinspot;
                        break;

                    case AbyssalGuardState.PickRandomMoon:

                        if (!EnsureInSpace())
                            return;

                        if (!CheckBookmarksExist())
                            return;

                        if (_selectedMoonId == null || !Framework.EntitiesById.ContainsKey(_selectedMoonId.Value))
                        {
                            var randomMoon = GetRandomMoon();
                            Log($"Picked random moon [{randomMoon?.Name}]");

                            if (randomMoon == null)
                            {
                                Log($"Error: There is moon in the system?");
                                this.State = AbyssalGuardState.Error;
                                return;
                            }

                            _selectedMoonId = randomMoon.Id;
                            State = AbyssalGuardState.Start;
                        }

                        break;

                    case AbyssalGuardState.GotoAbyssalWarpinspot:


                        // Pick a random celestial (moon?)
                        // Check if we have spot already which is in dist > 160_000 and < 5_000_000 from the abyss spot and on the line between the moon and the abyss spot
                        // If not change state to CreateAbyssalWarpinspot

                        if (!IsSelectedRandomMoonValid)
                        {
                            Log($"Selected moon is not valid.");
                            State = AbyssalGuardState.PickRandomMoon;
                            return;
                        }

                        var rndMoon = Framework.EntitiesById[_selectedMoonId.Value];
                        var abyssBookmark = GetAbyssBookmark();
                        var homeStationBookmark = GetHomeStationBookmark();
                        if (!CheckBookmarksExist())
                            return;

                        var bmsOnline = GetBookmarksOnLineBetweenChosenMoonAndAbyssSpot();
                        if (!bmsOnline.Any())
                        {
                            State = AbyssalGuardState.CreateAbyssalWarpinspot;
                            return;
                        }

                        var close = AreWeCloseToOneBookmarkWhichIsOnLineBetweenAbyssSpotAndChosenMoon();
                        if (close.Item1)
                        {
                            Log(
                                $"We are close to a bookmark which is on the line between the moon and the abyss spot. Name [{close.Item2.Title}] DistanceToAbyssSpot: [{close.Item2.DistanceTo(abyssBookmark)}]");
                            State = AbyssalGuardState.CloakAndWaitForTargets;
                            return;
                        }

                        var bm = bmsOnline.Random();

                        if (Framework.Me.IsWarpingByMode)
                            return;

                        if (bm.DistanceTo(Framework.ActiveShip.Entity) > 150_000)
                        {
                            List<float> warpRanges = new List<float>()
                            {
                                //10_000,
                                //20_000,
                                30_000,
                                50_000,
                                70_000,
                                //100_000,
                            };

                            var dist = warpRanges.Random();

                            if (dist < 0 || dist > 100_000)
                                dist = 0;

                            if (bm.WarpTo(dist))
                            {
                                Log($"Warping to bookmark [{bm.Title}] at range [{dist}]");
                                LocalPulse = DateTime.UtcNow.AddMilliseconds(GetRandom(2500, 3500));
                            }
                        }
                        else
                        {
                            rndMoon = Framework.EntitiesById[_selectedMoonId.Value];
                            if (rndMoon != null)
                            {
                                Log($"Warping to the chosen moon. Name [{rndMoon.Name}]");
                                rndMoon.WarpTo();
                                LocalPulse = DateTime.UtcNow.AddMilliseconds(GetRandom(2500, 3500));
                            }
                        }

                        break;
                    case AbyssalGuardState.CreateAbyssalWarpinspot:

                        if (!IsSelectedRandomMoonValid)
                        {
                            Log($"Selected moon is not valid.");
                            State = AbyssalGuardState.PickRandomMoon;
                            return;
                        }

                        abyssBookmark = GetAbyssBookmark();

                        // TODO: Create a function which is being called every frame, which checks if we just entered warp, and if so, just cycle the mwd once
                        if (Framework.Me.IsWarpingByMode)
                        {
                            if (DirectEve.Interval(6000))
                                Log($"We are in warp. Speed [{Math.Round(Framework.ActiveShip.Entity.Velocity, 2)}]");
                            return;
                        }

                        rndMoon = Framework.EntitiesById[_selectedMoonId.Value];

                        var minStepSize = 90_000d;
                        var maxStepSize = 110_000d;
                        var iterations = 5;
                        // Let's put that in a loop, so we can choose how far we want to be away from the abyss spot
                        for (int i = 1; i <= iterations; i++)
                        {
                            var minStepSizeIter = minStepSize * i;
                            var maxStepSizeIter = maxStepSize * i;
                            var bookmarksOnLine =
                                GetBookmarksOnLineBetweenChosenMoonAndAbyssSpot(minStepSizeIter, maxStepSizeIter);

                            Log(
                                $"Iteration [{i}] bookmarksOnLine.Count [{bookmarksOnLine.Count}] minStepSizeIter[{minStepSizeIter}] maxStepSizeIter[{maxStepSizeIter}]");

                            if (i == iterations && bookmarksOnLine.Any())
                            {
                                Log(
                                    $"There is a final bookmark existing between moon [{rndMoon.Name}] and the abyssal spot. Distance to the abyss spot [{Math.Round(bookmarksOnLine.FirstOrDefault().DistanceTo(abyssBookmark), 2)}] Changing state to go to the bookmark.");
                                State = AbyssalGuardState.GotoAbyssalWarpinspot;
                                return;
                            }

                            // If we are close to the moon, always warp to the closest on line bookmark and if there is none, warp to the abyss bookmark
                            if (AreWeCloseToTheChosenMoon())
                            {
                                bm = abyssBookmark;
                                if (bookmarksOnLine.Any())
                                    bm = bookmarksOnLine.OrderBy(b => b.DistanceTo(Framework.ActiveShip.Entity))
                                        .FirstOrDefault();

                                if (bm != null && bm.DistanceTo(Framework.ActiveShip.Entity) > 150_000)
                                {
                                    Log(
                                        $"Warping either to a bookmark or to the abyss spot. Distance to the abyss spot [{Math.Round(bookmarksOnLine.FirstOrDefault()?.DistanceTo(abyssBookmark) ?? 0, 2)}]");
                                    bm.WarpTo(100_000);
                                    LocalPulse = DateTime.UtcNow.AddMilliseconds(GetRandom(2500, 3500));
                                    return;
                                }
                            }
                            else
                            {
                                // If we are close to the abyss spot within the given min, max distance, check if we already have a bookmark, if not, create one
                                var activeShipDistanceToAbyssSpot =
                                    abyssBookmark.DistanceTo(Framework.ActiveShip.Entity);

                                Log(
                                    $"activeShipDistanceToAbyssSpot [{Math.Round(activeShipDistanceToAbyssSpot, 2)}]  minStepSizeIter [{minStepSizeIter}] maxStepSizeIter [{maxStepSizeIter}] bookmarksOnLine.Count [{bookmarksOnLine.Count}]");

                                if (!bookmarksOnLine.Any() && activeShipDistanceToAbyssSpot <= maxStepSizeIter &&
                                    activeShipDistanceToAbyssSpot >= minStepSizeIter)
                                {
                                    Log(
                                        $"We are close to the abyss spot. Distance [{Math.Round(activeShipDistanceToAbyssSpot, 2)}] Creating a bookmark.");
                                    Framework.BookmarkCurrentLocation(null);
                                    LocalPulse = DateTime.UtcNow.AddMilliseconds(GetRandom(2500, 3500));
                                    return;
                                }

                                if (bookmarksOnLine.Any())
                                    continue;

                                rndMoon = Framework.EntitiesById[_selectedMoonId.Value];
                                if (rndMoon != null)
                                {
                                    if (rndMoon.DistanceTo(Framework.ActiveShip.Entity) > 150_000)
                                    {
                                        Log($"Warping to the chosen moon. Name [{rndMoon.Name}]");
                                        rndMoon.WarpTo();
                                        LocalPulse = DateTime.UtcNow.AddMilliseconds(GetRandom(2500, 3500));
                                        return;
                                    }
                                }
                            }
                        }

                        break;
                    case
                        AbyssalGuardState.CloakAndWaitForTargets:

                        // If we are at the spot (verifiy here again), cloak up and wait until any player entity appears on the grid which is not the abyssal runner
                        // If that triggered, swap the state to WarpToAbyssalRunner

                        bm = GetAbyssBookmark();
                        if (bm == null)
                        {
                            Log($"Error: Abyss bookmark is null.");
                            State = AbyssalGuardState.Error;
                            return;
                        }

                        if (bm.DistanceTo(Framework.ActiveShip.Entity) < 150_000)
                        {
                            Log($"Apparently we moved too close to the abyss bookmark, restarting.");
                            State = AbyssalGuardState.Start;
                            return;
                        }

                        if (IsAnyOtherNonFleetPlayerOnGrid)
                        {
                            var players = OtherNonFleetPlayersOnGrid;
                            if (DirectEve.Interval(5000))
                                foreach (var p in players)
                                {
                                    Log(
                                        $"Name [{p.Name}] TypeName [{p.TypeName}] Owner [{DirectOwner.GetOwner(Framework, p.OwnerId)?.Name}] DistanceToAbyssBookmark [{Math.Round(bm.DistanceTo(p), 2)}]");
                                }

                            var abyssRunnerFleetMember =
                                Framework.FleetMembers.FirstOrDefault(m => m.Name == _abyssalRunnerCharName);
                            if (abyssRunnerFleetMember != null && abyssRunnerFleetMember.Entity != null)
                            {
                                Log($"The abyss runner appeared on grid, changing the state");
                                State = AbyssalGuardState.WarpToAbyssalRunner;
                                return;
                            }
                            else
                            {
                                if (DirectEve.Interval(5000))
                                    Log($"Waiting for the abyss runner to appear on grid.");
                            }
                        }

                        if (Framework.Me.IsWarpingByMode)
                            return;
                        {
                            // cloak up
                            var cloaks = Framework.Modules.Where(e => e.GroupId == (int)Group.CloakingDevice).ToList();
                            foreach (var cloak in cloaks)
                            {
                                if (cloak.IsInLimboState)
                                    continue;

                                if (!cloak.IsActive)
                                {
                                    Log($"Trying to activate module [{cloak.TypeName}].");
                                    cloak.Click();
                                }

                            }
                        }

                        break;
                    case AbyssalGuardState.WarpToAbyssalRunner:
                        {
                            // If cloaked, uncloak
                            // If not in warp, init warp and create am action queue action with random delay [50,150 ms] to activate the propmod after initializing the warp
                            // While in warp enable the highslot buffs (can we do that in warp?)
                            // Once we land, we do nothing and wait for the abyssal runner to put his ship into our belly
                            // If the abyssal runner is in the pod OR is not on grid anymore OR we are being aggressed, swap state to PVP

                            // disable cloak
                            var cloaks = Framework.Modules.Where(e => e.GroupId == (int)Group.CloakingDevice).ToList();
                            foreach (var cloak in cloaks)
                            {
                                if (cloak.IsInLimboState)
                                    continue;

                                if (cloak.IsActive)
                                {
                                    Log($"Trying to de-activate module [{cloak.TypeName}].");
                                    cloak.Click();
                                }
                            }

                            if (cloaks.Any(m => m.IsActive))
                            {
                                Log($"Cloak is still active, waiting for it to deactivate.");
                                return;
                            }

                            if (Framework.Me.IsWarpingByMode)
                            {
                                if (DirectEve.Interval(6000))
                                    Log($"We are in warp. Speed [{Math.Round(Framework.ActiveShip.Entity.Velocity, 2)}]");
                                return;
                            }

                            var abyssRunnerFleetMember =
                                Framework.FleetMembers.FirstOrDefault(m => m.Name == _abyssalRunnerCharName);
                            if (abyssRunnerFleetMember != null && abyssRunnerFleetMember.Entity != null)
                            {
                                if (abyssRunnerFleetMember.Entity.Distance > 150_00)
                                {
                                    Log(
                                        $"Current distance to the fleet member name [{abyssRunnerFleetMember.Name}] is [{abyssRunnerFleetMember.Entity.Distance}]. Warping to the fleet member.");
                                    abyssRunnerFleetMember.WarpToMember();
                                    LocalPulse = DateTime.UtcNow.AddMilliseconds(GetRandom(1000, 1500));
                                    return;
                                }
                                else
                                {
                                    if (abyssRunnerFleetMember.Entity.GroupId == (int)Group.Capsule)
                                    {
                                        Log($"It seems that the abyss runner stored the ship in our belly.");
                                        State = AbyssalGuardState.PVP;
                                        return;
                                    }
                                    else
                                    {
                                        if (DirectEve.Interval(5000))
                                            Log(
                                                $"The abyss runner is not in a pod, waiting for him to get into our belly.");
                                    }
                                }
                            }

                            if (abyssRunnerFleetMember == null || abyssRunnerFleetMember.Entity == null)
                            {
                                Log(
                                    $"It seems that the abyss runner is not on grid anymore or left the fleet. Changing the state to PVP.");
                                State = AbyssalGuardState.PVP;
                                return;
                            }

                            break;
                        }

                    case AbyssalGuardState.PVP:
                        {
                            if (DirectEve.Interval(15000))
                                DirectEventManager.NewEvent(new DirectEvent(DirectEvents.PANIC, "AbyssalGuardState.PVP"));

                            // If we are not yet attacked, we try to warp to the star at a random distance
                            // If we get bumped and have no timers, we could just logoff? How do we know that we are actively being bumped?
                            // Is there still something which guarantees the warp? (Wasn't there a fixed amount of time that will init the warp regardless of anything?) -> Yes ships will warp after 3 minutes of being bumped

                            // Update: Establishing Warp Vector
                            // After giving a warp command, the ship will establish a warp vector, aligning itself to it's destination. During that process, an indicator will show how close to the warp vector the ship is. Once the bar is filled, or after 3 minutes have passed without any other external Warp Disruption, the ship will warp to the selected destination.

                            // If we are being actively attacked, overheat!
                            // TODO: Maybe add drone usage to damage the fags and get on the kms

                            //Handle docked state


                            if (Framework.Entities.Any(e => e.IsPlayer && e.IsAttacking))
                            {

                                if (DirectEve.Interval(500, 1200))
                                {
                                    Log($"We are under attack by the following players:");
                                    foreach (var player in Framework.Entities.Where(e => e.IsPlayer && e.IsAttacking))
                                    {
                                        Log($"Player [{Framework.GetOwner(player.CharId)?.Name ?? "NULL"}] TypeName [{player.TypeName}] Distance [{player.Distance}]");
                                    }
                                    Log($"We are under attack by the following players -- END");
                                }

                                var highSlotBoosters = Framework.Modules.Where(e => e.GroupId == 1770).ToList(); // https://everef.net/groups/1770
                                foreach (var hsb in highSlotBoosters)
                                {
                                    if (hsb.IsActive || hsb.IsInLimboState)
                                        continue;

                                    if (hsb.CanBeReloaded && hsb.CurrentCharges < 1)
                                        continue;

                                    if (DirectEve.Interval(500, 1200))
                                    {
                                        Log($"Activating high slot booster module. TypeName [{hsb.TypeName}] Id [{hsb.ItemId}]");
                                        hsb.Click();
                                    }
                                }
                            }

                            if (Framework.ActiveShip.Entity.IsWarping)
                            {
                                Log($"We are in warp, waiting. Speed [{Framework.ActiveShip.Entity.Velocity}]");
                                return;
                            }

                            long _starDist = 500000000;
                            if (Framework.Session.IsInDockableLocation)
                            {
                                Log($"We are in a dockable location. Changing to error state.");
                                State = AbyssalGuardState.Error;
                                return;
                            }

                            var star = ESCache.Instance.Star;
                            if (ESCache.Instance.Star.Distance <= _starDist && !Framework.Entities.Any(e => e.IsAttacking && e.IsPlayer) && Framework.Me.CanIWarp())
                            {
                                Log($"Looks like we managed to get to the star and there are no other players in range. Changing the state.");
                                State = AbyssalGuardState.Error;
                                return;
                            }

                            if (!Framework.Me.IsWarpingByMode && Framework.Me.CanIWarp() && star.Distance > _starDist)
                            {
                                if (DirectEve.Interval(2000, 2000))
                                    Log($"Looks like we are not scrambled/disrupted currently, trying to warp to the star.");

                                if (DirectEve.Interval(1500, 2000))
                                {
                                    star.WarpToAtRandomRange();
                                    LocalPulse = DateTime.UtcNow.AddMilliseconds(GetRandom(1000, 1500));
                                }
                            }

                            //State = AbyssalGuardState.Error; // for now we just go into the error state
                            break;
                        }
                    case AbyssalGuardState.Error:
                        {

                            if (DirectEve.Interval(30000))
                                DirectEventManager.NewEvent(new DirectEvent(DirectEvents.KEEP_ALIVE, "Keep alive."));

                            // Here we go into the home station and call out for help
                            // TODO: we need a home station bookmark in the settings

                            if (DirectEve.Interval(480000))
                            {
                                try
                                {
                                    DirectEventManager.NewEvent(new DirectEvent(DirectEvents.ERROR,
                                        $"AbyssalGuard error state. Current ship typename: [{Framework.ActiveShip.TypeName}] Docked [{Framework.Session.IsInDockableLocation}]"));
                                }
                                catch
                                {
                                }
                            }

                            if (Framework.Session.IsInDockableLocation)
                                return;

                            if (ESCache.Instance.InSpace)
                            {
                                if (ESCache.Instance.State.CurrentTravelerState != TravelerState.AtDestination)
                                {
                                    ESCache.Instance.Traveler.TravelToBookmark(ESCache.Instance.DirectEve.Bookmarks
                                        .OrderByDescending(e => e.IsInCurrentSystem)
                                        .FirstOrDefault(b => b.Title == _homeStationBookmarkName));
                                }
                                else
                                {
                                    ESCache.Instance.Traveler.Destination = null;
                                    ESCache.Instance.State.CurrentTravelerState = TravelerState.Idle;
                                    Log($"Arrived at the home station.");
                                    State = AbyssalGuardState.Error;
                                }
                            }

                            break;
                        }
                }
            }
            catch (Exception e)
            {
                Log(e.ToString());
                Log($"----------- STACK TRACE -----------");
                Log(e.StackTrace.ToString());
            }
        }

        public override bool EvaluateDependencies(ControllerManager cm)
        {
            return true;
        }

        public bool EnsureInSpace()
        {
            if (Framework.Session.IsInDockableLocation && DirectEve.Interval(1500, 3500))
            {
                ESCache.Instance.DirectEve.ExecuteCommand(DirectCmd.CmdExitStation);
                Log($"Undocking from a dockable location.");
                return false;
            }

            return Framework.Session.IsInSpace;
        }

        public override void Dispose()
        {
            Log("-- Removed OnSessionReadyHandler");
            DirectSession.OnSessionReadyEvent -= OnSessionReadyHandler;
        }

        private void OnSessionReadyHandler(object sender, EventArgs e)
        {
            Log($"OnSessionReadyHandler proc.");
            _selectedMoonId = null;
        }

        public DirectEntity GetRandomMoon()
        {
            return Framework.Entities.Where(e => e.GroupId == (int)Group.Moon).Random();
        }

        public override void ReceiveBroadcastMessage(BroadcastMessage broadcastMessage)
        {
            // We need a command to know if the abyssal guard is avail and ready to go on the spot
            // We need a second command to know if the guard is on the spot and the spot is clear of other players
            // -> Receive message with command X -> create an action queue action which responds after it was executed by the onframe handler
        }
    }
}