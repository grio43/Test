//
// (c) duketwo 2022
//

extern alias SC;
using EVESharpCore.Cache;
using EVESharpCore.Framework;
using SC::SharedComponents.Utility;
using ServiceStack.OrmLite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using SC::SharedComponents.Extensions;
using ServiceStack.Templates;


namespace EVESharpCore.Controllers.Abyssal
{
    enum MoveDirection
    {
        None,
        TowardsEnemy,
        AwayFromEnemy,
        Gate
    }

    public partial class AbyssalController : AbyssalBaseController
    {
        private DirectWorldPosition _moveToOverride = null;
        private DateTime _lastMoveToOverride = DateTime.MinValue;

        private Vec3? _moveBackwardsDirection = null;

        private bool _enemiesWereInOptimal = false;
        private bool AreWeCloseToTheAbyssBoundary => !IsOurShipWithintheAbyssBounds(-15000);

        private MoveDirection _moveDirection;

        private int _keepAtRangeDistance = 1000;
        private int _gateMTUOrbitDistance = 2000;
        private int _enemyOrbitDistance = 7500;
        private int _speedCloudDistance = 8000;
        private int _wreckOrbitDistance = 500;

        private List<int> _keepAtRangeDistances = new List<int>() { 500, 1500, 2000 };
        private List<int> _gateMTUOrbitDistances = new List<int>() { 1000, 1500 };
        private List<int> _wreckOrbitDistances = new List<int>() { 500, 1000 };
        private List<int> _enemyOrbitDistances = new List<int>() { 7500, 10000 };

        private bool _alwaysMoveIntoWeaponRange => ESCache.Instance.EveAccount.CS.AbyssalMainSetting.AlwaysMoveIntoWeaponRange;

        private bool IsNextGateInASpeedCloud => _nextGate != null && _nextGate.IsInSpeedCloud;

        private List<DirectEntity> SpeedClouds => Framework.Entities.Where(e => e.IsTachCloud).ToList();

        private bool IsNextGateNearASpeedCloud => _nextGate != null && !SpeedClouds.Any(e => e.DirectAbsolutePosition.GetDistanceSquared(_nextGate.DirectAbsolutePosition) < (e.RadiusOverride + _speedCloudDistance) * (e.RadiusOverride + _speedCloudDistance));

        /// <summary>
        /// This is an override for any movement position, if this function returns anything but null, we are forced to go there, no matter what. (except single room abyssals)
        /// </summary>
        internal DirectWorldPosition MoveToOverride
        {
            get
            {
                // Always ensure we are in range to deal damage
                var targetsOrderedByDistance = TargetsOnGrid.OrderBy(x => x.Distance);
                var sumInBayInSpaceDrones = alldronesInBay.Count + allDronesInSpace.Count;
                var range = sumInBayInSpaceDrones == 0 || _alwaysMoveIntoWeaponRange
                    ? (_weaponMaxRange - 1000 < 0 ? _weaponMaxRange : _weaponMaxRange - 1000)
                    : _maxRange;

                if ((allDronesInSpace.Any(e => e.DroneState != 1 || e.Distance >= _maxDroneRange) || sumInBayInSpaceDrones == 0 || _alwaysMoveIntoWeaponRange) &&
                    targetsOrderedByDistance.Any() && targetsOrderedByDistance.First().Distance > range)
                {
                    if (DirectEve.Interval(10000))
                        Log(
                            $"The closest entity is outside of our range, moving towards the closest enemy. Distance to closest enemy [{targetsOrderedByDistance.First().Distance}] Typename [{targetsOrderedByDistance.First().TypeName}]");

                    return targetsOrderedByDistance.First().DirectAbsolutePosition;
                }

                if (!IsOurShipWithintheAbyssBounds(3000))
                {
                    if (DirectEve.Interval(5000) && AbyssalCenter != null && DirectEve.ActiveShip.Entity != null)
                    {
                        try
                        {
                            Log(

                                $"Our ship is not within the abyss bounds. Dist to boundary [{Math.Round(AbyssalCenter.DirectAbsolutePosition.GetDistanceSquared(DirectEve.ActiveShip.Entity.DirectAbsolutePosition).Value, 2)}]");
                        }
                        catch (Exception exception)
                        {
                        }

                    }
                    return _nextGate?.DirectAbsolutePosition ?? SafeSpotNearGate ?? null;
                }

                if (_secondsNeededToReachTheGate >= CurrentStageRemainingSecondsWithoutPreviousStages && DoWeNeedToMoveToTheGate()
                    && ((IsActiveShipFrigateOrDestroyer && AnyEntityOnGridToBeKitedAsFrigSizedShip) || !IsActiveShipFrigateOrDestroyer)) // We need to delay that if there are entities we want to avoid as frig/destroyer
                {
                    if (DirectEve.Interval(5000))
                    {
                        Log($"We need to move to the gate.");
                    }
                    return null;
                }

                if (_lastMoveToOverride.AddMilliseconds(250) > DateTime.UtcNow && _moveToOverride != null)
                    return _moveToOverride;

                if (!_maxDroneRange.HasValue)
                {
                    Log($"Warning: MaxDroneRange has no value!");
                    _maxDroneRange = _maxRange;
                }

                _moveToOverride = null;

                if (_nextGate == null)
                    return null;
                if (!_moveBackwardsDirection.HasValue)
                {
                    var direction = _nextGate.DirectAbsolutePosition.GetDirectionalVectorTo(_activeShipPos)
                        .Normalize(); // go backwards!

                    var ship = ESCache.Instance.DirectEve.ActiveShip.Entity;
                    var res = DirectSceneManager.GenerateSafeDirectionVector(direction, ship.DirectAbsolutePosition.GetVector(), AbyssalCenter.DirectAbsolutePosition.GetVector(), 75_000, 45d, 1000, 25_000, true, false, true, true, true, true, true, false, true, true);
                    if (res.HasValue)
                    {
                        _moveBackwardsDirection = res.Value;
                        Log($"GenerateSafeDirectionVector return the following direction [{_moveBackwardsDirection.Value}]");
                    }
                    else
                    {
                        _moveBackwardsDirection = DirectSceneManager.RotateVector(direction, 5);
                        Log($"Using fallback value for the backwards direction [{_moveBackwardsDirection.Value}]");
                    }
                }

                if (!_enemiesWereInOptimal && AreTheMajorityOfNPCsInOptimalOnGrid())
                {
                    Log(
                        $"The majority of the enemies are in optimal, skipping overriding the move-to destination for this stage.");
                    _enemiesWereInOptimal = true;
                }

                // handle marshals
                if
                    (_marshalsOnGridCount > _marshalTankThreshold &&
                     _maxRange >
                     69660) // here we should dive into a deviata supressor (which kills missles?) or go straight backwards keep at range. missile range of marshals is: 2.70 * 6 * 4300 = 69660, we have 80k drone range.
                {
                    var nearestMarhsal = TargetsOnGrid.Where(e => e.IsAbyssalMarshal).OrderBy(e => e.Distance)
                        .FirstOrDefault(); // pick the nearest
                    _moveToOverride =
                        CalculateMoveToOverrideSpot(_moveBackwardsDirection, nearestMarhsal, 74000,
                            74900); // stay between 70k and 74.5 k (max range will be 75 after the next patch)
                }
                else if (_enemiesWereInOptimal)
                {
                    _moveToOverride = null;
                }

                else if (TargetsOnGrid.Count(e => e.TypeName.ToLower().Contains("kikimora")) > _kikimoraTankThreshold)
                {
                    var nearestKiki = TargetsOnGrid.Where(e => e.TypeName.ToLower().Contains("kikimora"))
                        .OrderBy(e => e.Distance).FirstOrDefault(); // pick the nearest kiki

                    _moveToOverride = CalculateMoveToOverrideSpot(_moveBackwardsDirection, nearestKiki, 30_000, 42_000);
                }

                else if (TargetsOnGrid.Count(e => e.TypeName.ToLower().Contains("damavik")) > _damavikTankThreshold)
                {
                    var nearestKiki = TargetsOnGrid.Where(e => e.TypeName.ToLower().Contains("damavik"))
                        .OrderBy(e => e.Distance).FirstOrDefault(); // pick the nearest kiki

                    _moveToOverride = CalculateMoveToOverrideSpot(_moveBackwardsDirection, nearestKiki, 30_000, 42_000);
                }

                else if (TargetsOnGrid.Count(e => e.GroupId == 1997 && e.IsNPCBattlecruiser) > _bcTankthreshold)
                {
                    var nearestRogueBc = TargetsOnGrid.Where(e => e.GroupId == 1997 && e.IsNPCBattlecruiser)
                        .OrderBy(e => e.Distance).FirstOrDefault(); // pick the nearest rogue drone bc
                    _moveToOverride =
                        CalculateMoveToOverrideSpot(_moveBackwardsDirection, nearestRogueBc, 30_000, 42_000);
                }

                else if (TargetsOnGrid.Sum(e => e.GigaJouleNeutedPerSecond) >= _maxGigaJoulePerSecondTank && TargetsOnGrid.Any(e => e.IsNeutingEntity))
                {
                    // if the neuts on grid neut cap with more than "_maxGigaJouleCapTank" per second
                    var nearestNeut = TargetsOnGrid.Where(e => e.IsNeutingEntity).OrderBy(e => e.Distance)
                        .FirstOrDefault(); // pick the nearest neut
                    _moveToOverride = CalculateMoveToOverrideSpot(_moveBackwardsDirection, nearestNeut, 63_500, 66_000);
                }

                // We probably want to limit this to a certain loot attempt amount
                if (_moveToOverride == null && ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.DoNotUseMTU && _notEmptyWrecks.Any())
                {
                    if (DirectEve.Interval(3000))
                    {
                        Log($"Setting moveToOverride to the nearest wreck.");
                    }
                    _moveToOverride = _notEmptyWrecks.OrderBy(e => e.Distance).FirstOrDefault().DirectAbsolutePosition;
                }

                if (_moveToOverride != null)
                    _lastMoveToOverride = DateTime.UtcNow;

                return _moveToOverride;
            }
        }

        /// <summary>
        /// Calculates a spot to stay away from enemies at a given range. This is essentially a 'KeepAtRange' method, except that we passively use ongrid pathfinding to move around obstacles
        /// </summary>
        /// <param name="direction">The direction (unit vector) we are using to move away from the entity. Null means we use the direction enemyLocation -> active ship</param>
        /// <param name="targetEntity">The target entity we want to avoid</param>
        /// <param name="minRange">The minRange we want to stay away from the enemy</param>
        /// <param name="maxRange">The maxRange we want to stay away from the enemy</param>
        /// <returns></returns>
        private DirectWorldPosition CalculateMoveToOverrideSpot(Vec3? direction, DirectEntity targetEntity,
            double minRange, double maxRange)
        {
            if (minRange >= maxRange)
            {
                Log($"Error: minRange >= maxRange");
                return null;
            }

            var shipsMaxRange = _maxRange;

            if (shipsMaxRange < minRange) // We should factor in enemy optimal somewhere
            {
                minRange = shipsMaxRange * 0.8;
                maxRange = shipsMaxRange;
            }

            int abyssalBoundsMax = -5500; // the maximum distance we want to move outside of the abyss bounds
            int unitVecMagnitude = 15000; // magnitude of scaled unit vectors

            var activeShipPos = ESCache.Instance.DirectEve.ActiveShip.Entity.DirectAbsolutePosition;
            var directionTargetEntityTowardsUs = targetEntity.DirectAbsolutePosition
                .GetDirectionalVectorTo(activeShipPos).Normalize()
                .Scale(unitVecMagnitude); // 5k of the unit direction vector enemy -> us

            DirectWorldPosition moveAwayPos = null;

            if (direction.HasValue)
            {
                var dirMagnitude = direction.Value.Scale(unitVecMagnitude);
                moveAwayPos = new DirectWorldPosition(activeShipPos.X + dirMagnitude.X,
                    activeShipPos.Y + dirMagnitude.Y, activeShipPos.Z + dirMagnitude.Z);
            }
            else
            {
                moveAwayPos = new DirectWorldPosition(activeShipPos.X + directionTargetEntityTowardsUs.X,
                    activeShipPos.Y + directionTargetEntityTowardsUs.Y,
                    activeShipPos.Z + directionTargetEntityTowardsUs.Z);
            }

            if (IsSpotWithinAbyssalBounds(activeShipPos, abyssalBoundsMax)) // if we are within the given boundary
            {
                if (activeShipPos.GetDistance(targetEntity.DirectAbsolutePosition) >=
                    minRange) // ok we're further away than minRange, let's check if we are also within maxRage
                {
                    if (activeShipPos.GetDistance(targetEntity.DirectAbsolutePosition) >=
                        maxRange) // we're further away than maxRange, move towards the enemy
                    {
                        _moveDirection = MoveDirection.TowardsEnemy;
                        return targetEntity.DirectAbsolutePosition;
                    }
                    else // we're between minRange and maxRage, this is fine, but we rather still move away
                    {
                        _moveDirection = MoveDirection.AwayFromEnemy;
                        return moveAwayPos;
                    }
                }
                else // here we are too close to the enemy, let's move away from it
                {
                    _moveDirection = MoveDirection.AwayFromEnemy;
                    return moveAwayPos;
                }
            }
            else // if we are outside of the bounds, move towards the enemy
            {
                _moveDirection = MoveDirection.TowardsEnemy;
                return targetEntity.DirectAbsolutePosition;
            }
        }

        private DirectWorldPosition _safeSpotNearGate = null;
        private bool _safeSpotNearGateChecked = false;
        private int _safeSpotNeargGateResets = 0;

        /// <summary>
        /// If the gate is within an entity we want to avoid we calculate a "safespot" to drop the MTU.
        /// </summary>
        internal DirectWorldPosition SafeSpotNearGate
        {
            get
            {

                if (_safeSpotNeargGateResets > 3)
                {
                    if (DirectEve.Interval(3000))
                    {
                        Log("_safeSpotNeargGateResets > 3, returning null.");
                    }
                    return null;
                }

                if (_safeSpotNearGate != null && AbyssalCenter?.DirectAbsolutePosition != null && DirectEve.Interval(3000))
                {
                    var dist = _safeSpotNearGate.GetDistanceSquared(AbyssalCenter?.DirectAbsolutePosition);
                    if (dist > DirectEntity.AbyssBoundarySizeSquared)
                    {
                        Log($"SafeSpotNearGate is outside of abyssal bounds, resetting. Current distance [{_safeSpotNearGate.GetDistance(AbyssalCenter?.DirectAbsolutePosition)}] km.");
                        _safeSpotNearGate = null;
                        _safeSpotNearGateChecked = false;
                        _safeSpotNeargGateResets++;
                    }
                }

                if (_safeSpotNearGate != null)
                    return _safeSpotNearGate;

                if (_safeSpotNearGateChecked)
                    return null;

                var nextGate = _nextGate;

                var intersectingEnts = DirectEntity.AnyIntersectionAtThisPosition(nextGate.DirectAbsolutePosition,
                    ignoreTrackingPolyons: true, ignoreAutomataPylon: true, ignoreWideAreaAutomataPylon: true);

                DirectWorldPosition backupPos = null;

                if (intersectingEnts.Any() || IsNextGateNearASpeedCloud)
                {
                    _safeSpotNearGate = null;
                    var activeShipPos = ESCache.Instance.DirectEve.ActiveShip.Entity.DirectAbsolutePosition;

                    Vec3 direction = nextGate.DirectAbsolutePosition.GetDirectionalVectorTo(activeShipPos).Normalize();

                    var bp = _nextGate.DirectAbsolutePosition;
                    List<DirectWorldPosition> positions = new List<DirectWorldPosition>();

                    var diagonalFactor = 1 / Math.Sqrt(3);

                    var speedClouds = Framework.Entities.Where(e => e.IsTachCloud).ToList();


                    for (int i = 500; i <= 90500; i += 5000)
                    {
                        var diagDist = diagonalFactor * i;
                        var dir = direction.Scale(i);

                        var directDWP = new DirectWorldPosition(bp.X + dir.X, bp.Y + dir.Y, bp.Z + dir.Z);
                        var xp = new DirectWorldPosition(bp.X + i, bp.Y, bp.Z);
                        var xn = new DirectWorldPosition(bp.X - i, bp.Y, bp.Z);
                        var yp = new DirectWorldPosition(bp.X, bp.Y + i, bp.Z);
                        var yn = new DirectWorldPosition(bp.X, bp.Y - i, bp.Z);
                        var zp = new DirectWorldPosition(bp.X, bp.Y, bp.Z + i);
                        var zn = new DirectWorldPosition(bp.X, bp.Y, bp.Z - i);

                        var d1 = new DirectWorldPosition(bp.X - diagDist, bp.Y - diagDist, bp.Z - diagDist);
                        var d2 = new DirectWorldPosition(bp.X - diagDist, bp.Y - diagDist, bp.Z + diagDist);
                        var d3 = new DirectWorldPosition(bp.X - diagDist, bp.Y + diagDist, bp.Z - diagDist);
                        var d4 = new DirectWorldPosition(bp.X - diagDist, bp.Y + diagDist, bp.Z + diagDist);
                        var d5 = new DirectWorldPosition(bp.X + diagDist, bp.Y - diagDist, bp.Z - diagDist);
                        var d6 = new DirectWorldPosition(bp.X + diagDist, bp.Y - diagDist, bp.Z + diagDist);
                        var d7 = new DirectWorldPosition(bp.X + diagDist, bp.Y + diagDist, bp.Z - diagDist);
                        var d8 = new DirectWorldPosition(bp.X + diagDist, bp.Y + diagDist, bp.Z + diagDist);

                        positions.Add(directDWP);
                        positions.Add(xp);
                        positions.Add(xn);
                        positions.Add(yp);
                        positions.Add(yn);
                        positions.Add(zp);
                        positions.Add(zn);

                        positions.Add(d1);
                        positions.Add(d2);
                        positions.Add(d3);
                        positions.Add(d4);
                        positions.Add(d5);
                        positions.Add(d6);
                        positions.Add(d7);
                        positions.Add(d8);

                        var pos = positions.Where(p =>
                                AbyssalCenter.DirectAbsolutePosition.GetDistanceSquared(p) <=
                                DirectEntity.AbyssBoundarySizeSquared
                                && !DirectEntity.AnyIntersectionAtThisPosition(p, ignoreTrackingPolyons: true,
                                    ignoreAutomataPylon: true, ignoreWideAreaAutomataPylon: true).Any())
                            .OrderBy(e => e.GetDistanceSquared(activeShipPos)).FirstOrDefault();
                        if (pos != null)
                        {
                            if (backupPos == null)
                                backupPos = pos;

                            var continueOuter = false;
                            // Ensure the spot is at least ****k away from a speed cloud
                            foreach (var speedCloud in speedClouds)
                            {
                                var rsq = (speedCloud.RadiusOverride + _speedCloudDistance) * (speedCloud.RadiusOverride + _speedCloudDistance);
                                if (speedCloud.DirectAbsolutePosition.GetDistanceSquared(pos) <= rsq)
                                {
                                    continueOuter = true;
                                    break;
                                }
                            }

                            if (continueOuter)
                                continue;

                            _safeSpotNearGate = pos;
                            break;
                        }
                    }
                }

                if (_safeSpotNearGate == null && backupPos != null)
                {
                    _safeSpotNearGate = backupPos;
                }

                _safeSpotNearGateChecked = true;

                return _safeSpotNearGate;
            }
        }


        // approx time we need to get to the gate (we also need to factor in the distance of (nextgate;mtu) in here (if we plan to place the mtu further away from the gate (which we do now))
        internal double _secondsNeededToReachTheGate
        {
            get
            {
                var offset = 5d; // add 5 seconds
                if (_getMTUInSpace != null)
                {
                    // dist: ship -> mtu -> gate
                    var dist = _getMTUInSpace.DirectAbsolutePosition.GetDistance(_nextGate.DirectAbsolutePosition) +
                               _getMTUInSpace.Distance;
                    return (dist.Value / _maxVelocity.Value) + offset;
                }

                // dist: ship -> nextgate
                return (_nextGate.Distance / _maxVelocity.Value) + offset;
            }
        }

        internal bool
            AnyEntityOnGridWeWantToBeCloseWith // We ignore abyss clouds here, that means here should be no spawn which can kill us due cloud downsides
        {
            get
            {
                //var rogueDrones = TargetsOnGrid.Where(e => e.GroupId == 1997 && (e.IsNPCBattlecruiser || e.IsNPCCruiser || e.IsNPCFrigate)).Any();
                //var kikimora = _targetsOnGrid.Any(e => e.TypeName.ToLower().Contains("kikimora"));
                //var cynabalDramiel = TargetsOnGrid.Any(e => e.TypeName.ToLower().Contains("cynabal")) || TargetsOnGrid.Any(e => e.TypeName.ToLower().Contains("dramiel"));
                //var ephialtes = TargetsOnGrid.Any(e => e.TypeName.ToLower().Contains("ephialtes") || e.TypeId == 56214); // drifter bs + ephi spawn
                //var karen = _targetsOnGrid.Any(e => e.TypeId == 56214); // drifter bs - karybdis

                //if (cynabalDramiel)
                //return true;

                return false;
            }
        }

        internal bool IgnoreAbyssEntities
        {
            get
            {
                if (IsAbyssGateOpen)
                    return true;

                if (!IsOurShipWithintheAbyssBounds())
                    return true;

                if (AreWeInASpeedCloud && TargetsOnGridWithoutLootTargets.Any() && !AreWeCloseToTheAbyssBoundary)
                    return false;

                if (AnyEntityOnGridWeWantToBeCloseWith)
                    return true;

                if (TargetsOnGrid.Count <= 4 && !IsActiveShipFrigateOrDestroyer)
                    return true;

                if (TargetsOnGrid.Count <= 1 && IsActiveShipFrigateOrDestroyer)
                    return true;

                //if (DoWeNeedToMoveToTheGate())
                //    return true;

                return false;
            }
        }

        internal bool DoWeNeedToMoveToTheGate()
        {
            var timeNeededToGate = _secondsNeededToReachTheGate + _secondsNeededToRetrieveWrecks + 40;
            var timeNeededToClearGrid = GetEstimatedStageRemainingTimeToClearGrid();

            if (timeNeededToClearGrid != null)
            {
                if (timeNeededToGate > timeNeededToClearGrid)
                    return true;
            }

            if (CurrentStageRemainingSecondsWithoutPreviousStages < timeNeededToGate)
                return true;

            return false;
        }

        private DateTime _lastMoveOnGrid = DateTime.MinValue;

        private DateTime _lastHandleDrones = DateTime.MinValue;

        private DateTime _lastHandleTarget = DateTime.MinValue;

        internal void MoveOnGrid()
        {
            if (!DirectEve.HasFrameChanged())
                return;

            if (DirectSceneManager.LastRedrawSceneColliders.AddSeconds(15) < DateTime.UtcNow)
                ESCache.Instance.DirectEve.SceneManager.RedrawSceneColliders(ignoreAbyssEntities: IgnoreAbyssEntities,
                    ignoreTrackingPolyons: true, ignoreAutomataPylon: true, ignoreWideAreaAutomataPylon: true);

            _lastMoveOnGrid = DateTime.UtcNow;
            // manage speed if outside of the boundary
            if (!IsOurShipWithintheAbyssBounds())
            {
                var inSpeedCloud = AreWeInASpeedCloud;
                if (inSpeedCloud)
                {
                    float speed = ((float)_maxVelocity.Value * 0.3f) /
                                  (float)Rnd.Next((int)_maxVelocity.Value + 1, (int)_maxVelocity.Value + 6); // variance
                    if (ESCache.Instance.DirectEve.ActiveShip.SetSpeedFraction(speed))
                    {
                        Log(
                            $"Our ship is outside of the abyss bounds and we are in a speed cloud. Limit speed to 30%"); // as the value is transmitted directly, maybe add some variance to it
                        Log($"Speedvalue [{speed}]");
                    }
                }
                else
                {
                    if (Math.Abs(1 - ESCache.Instance.DirectEve.ActiveShip.Entity.SpeedFraction) > 0.1d)
                    {
                        if (ESCache.Instance.DirectEve.ActiveShip.SetSpeedFraction(1.0f))
                        {
                            Log($"Setting max speed fraction back to 1.0");
                        }
                    }
                }
            }
            else
            {
                if (Math.Abs(1 - ESCache.Instance.DirectEve.ActiveShip.Entity.SpeedFraction) > 0.1d)
                {
                    if (ESCache.Instance.DirectEve.ActiveShip.SetSpeedFraction(1.0f))
                    {
                        Log($"Setting max speed fraction back to 1.0");
                    }
                }
            }

            // move to override, we always move there if it exists (except in single room abyssals)
            if (!_singleRoomAbyssal && MoveToOverride != null)
            {
                if (DirectEve.Interval(5000))
                    Log($"MoveToOverride exists. Moving there.");
                if (DirectEntity.MoveToViaAStar(3000, forceRecreatePath: forceRecreatePath, dest: MoveToOverride,
                        ignoreAbyssEntities: true))
                {
                    var wrecks = ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.DoNotUseMTU ? _notEmptyWrecks.Where(w => w.DirectAbsolutePosition.GetDistance(MoveToOverride) < 169).OrderBy(e => e.Distance).ToList() : new List<DirectEntity>();
                    if (wrecks.Any())
                    {
                        var wreck = wrecks.FirstOrDefault();
                        if (!wreck.IsOrbitedByActiveShip)
                        {
                            Log($"Orbiting wreck [{wreck.Name}] at [{_gateMTUOrbitDistance}]");
                            wreck.Orbit(_wreckOrbitDistance);
                        }
                    }
                    else
                    {
                        if (DirectEve.Interval(1200, 1700))
                            ESCache.Instance.DirectEve.ActiveShip.MoveTo(MoveToOverride);
                    }

                }

                return;
            }

            var moveSafespotDronesNotReturningAfterGridCleared = allDronesInSpace.Any()
                                                                 && IsAbyssGateOpen // only if abyss gate open
                                                                 && _startedToRecallDronesWhileNoTargetsLeft !=
                                                                 null // only while we are recalling
                                                                 && (DateTime.UtcNow -
                                                                     _startedToRecallDronesWhileNoTargetsLeft.Value)
                                                                 .TotalSeconds >
                                                                 15 // if  we are recalling drones for longer than 15 seconds
                                                                 && CurrentStageRemainingSecondsWithoutPreviousStages >
                                                                 15; // if the current stage has at least 25 seconds left

            var moveSafespotDronesNotReturningWhileGridNotClearedAndWeAreInASpeedCloud =
                TargetsOnGridWithoutLootTargets.Any() && AreWeInASpeedCloud &&
                allDronesInSpace.Any(e =>
                    DroneReturningSinceSeconds(e.Id) >
                    25); // drone control range is around 80k and slowest drones (gecko) is around 2.8k

            var speedCloudOnGateCondition = !_abandoningDrones && _getMTUInSpace == null && (IsNextGateInASpeedCloud || IsNextGateNearASpeedCloud) && allDronesInSpace.Any() && (TargetsOnGridWithoutLootTargets.Any(e => e.Distance < _maxRange) || !TargetsOnGridWithoutLootTargets.Any());

            var dronesReturningForLongerThan10Sec = allDronesInSpace.Where(e => DroneReturningSinceSeconds(e.Id) > 10);

            if (dronesReturningForLongerThan10Sec.Any() && DirectEve.Interval(5000))
            {
                foreach (var drone in dronesReturningForLongerThan10Sec)
                {
                    Log($"--- Drone [{drone.TypeName}] Id {drone.Id} is being recalled for longer than 10 seconds.");
                }
            }

            if (SafeSpotNearGate == null && DirectEve.Interval(5000))
            {
                Log($"Warn: SafeSpotNearGate is null.");
            }

            // Move to safe spot near gate 
            if ((!_singleRoomAbyssal
                && SafeSpotNearGate != null
                && (
                    TargetsOnGridWithoutLootTargets.Any(e => e.Distance < _maxRange) // any target on  grid within range
                    || _currentLockedTargets.Any(e => e.GroupId != 2009)  // any target locked which is not a cache
                    || (AreAllDronesAttacking && !allDronesInSpace.Any(d => d.FollowEntity != null && d.FollowEntity.GroupId == 1981)) // all attacking and no drone is attacking a pylon
                    || speedCloudOnGateCondition
                )
                && (
                    (!AnyEntityOnGridWeWantToBeCloseWith && _getMTUInSpace == null && _MTUAvailable && TargetsOnGrid.Count() >= 6)
                    || moveSafespotDronesNotReturningAfterGridCleared
                    || moveSafespotDronesNotReturningWhileGridNotClearedAndWeAreInASpeedCloud
                    || speedCloudOnGateCondition
                    || (AreWeInASpeedCloud && TargetsOnGridWithoutLootTargets.Any() && _secondsNeededToReachTheGate < CurrentStageRemainingSecondsWithoutPreviousStages
                        && (TargetsOnGridWithoutLootTargets.All(t => t.Distance < _maxRange) || AreAllDronesAttacking)
                       )
                    )
               ))
            {
                // okay if we didn't drop a mtu yet, but we do have calculated a safespot. we move there and drop the mtu
                if (DirectEve.Interval(5000))
                {
                    if (moveSafespotDronesNotReturningAfterGridCleared)
                        Log(
                            $"We are moving to the next best calculated s afespot near the gate because drones are not returning after the grid has been cleared.");
                    else if (moveSafespotDronesNotReturningWhileGridNotClearedAndWeAreInASpeedCloud)
                        Log(
                            $"We are moving to the next best calculated safespot near the gate because drones are not returning while the grid is being cleared and we are in a speed cloud.");
                    else
                        Log($"We are moving to the next best calculated safespot near the gate.");
                }

                if (DirectEntity.MoveToViaAStar(3000, forceRecreatePath: forceRecreatePath, dest: SafeSpotNearGate,
                        ignoreAbyssEntities: IgnoreAbyssEntities, ignoreTrackingPolyons: true,
                        ignoreAutomataPylon: true, ignoreWideAreaAutomataPylon: true))
                {
                    // here we move back and forward, maybe add some custom circular movement around a position in space? - DONE
                    if (SafeSpotNearGate.GetDistance(_nextGate.DirectAbsolutePosition) > 8000)
                    {
                        if (!SafeSpotNearGate.Orbit(7000))
                            ESCache.Instance.DirectEve.ActiveShip.MoveTo(SafeSpotNearGate);
                    }
                    else
                    {
                        if (!_nextGate.IsOrbitedByActiveShip)
                        {
                            Log($"Orbiting the next gate at [{_gateMTUOrbitDistance}].");
                            _nextGate.Orbit(_gateMTUOrbitDistance);
                        }
                    }
                }
                return;
            }

            DirectEntity moveToTarget = _getMTUInSpace ?? _nextGate; // the default

            DirectEntity higherPrioTarget = null;
            var highestTargeted = _currentLockedAndLockingTargets.OrderByDescending(e => e.AbyssalTargetPriority)
                .FirstOrDefault();
            if (highestTargeted != null)
            {
                // get the lowest on grid
                var lowestOnGrid = TargetsOnGrid.Where(e => !e.IsTarget && !e.IsTargeting)
                    .OrderBy(e => e.AbyssalTargetPriority).FirstOrDefault();
                if (lowestOnGrid != null)
                {
                    if (lowestOnGrid.AbyssalTargetPriority < highestTargeted.AbyssalTargetPriority)
                    {
                        if (DirectEve.Interval(2000, 3000))
                        {
                            Log(
                                $"Higher priority present on grid. Moving towards TypeName [{lowestOnGrid.TypeName}] TypeId [{lowestOnGrid.TypeId}] Distance [{lowestOnGrid.Distance}]");
                        }

                        higherPrioTarget = lowestOnGrid;
                    }
                }
            }

            var targetsOrderedByDistance = TargetsOnGrid.OrderBy(x => x.Distance);
            var sumInBayInSpaceDrones = alldronesInBay.Count + allDronesInSpace.Count;

            var range = sumInBayInSpaceDrones == 0 || _alwaysMoveIntoWeaponRange
                ? (_weaponMaxRange - 1000 < 0 ? _weaponMaxRange : _weaponMaxRange - 1000)
                : _maxRange;


            //var noTargetLockedExceptCaches = !_currentLockedTargets.Any(e => e.GroupId != 2009);
            var allTargetsOutOfRange = TargetsOnGridWithoutLootTargets.Any() && TargetsOnGridWithoutLootTargets.All(e => e.Distance >= range);

            if (_singleRoomAbyssal)
            {
                Log($"This is a single room abyss, moving to the next gate.");
                moveToTarget = _nextGate;
            }
            else if (higherPrioTarget != null)
            {
                moveToTarget = higherPrioTarget;
                if (_wrecks != null && _wrecks.All(w => w.IsEmpty) &&
                    _getMTUInSpace != null) // ignore higher priority if we need to scoop the mtu
                {
                    moveToTarget = _getMTUInSpace;
                }
            }
            // If we have no targets locked or if all targets are out of range, move to the closest target on grid
            else if (
                (allTargetsOutOfRange)
                && (allDronesInSpace.Any(e => e.DroneState != 1 || e.Distance >= _maxDroneRange) || sumInBayInSpaceDrones == 0 || _alwaysMoveIntoWeaponRange)) // if we dont have anything locked, move to the closest target on grid
            {
                moveToTarget = TargetsOnGridWithoutLootTargets.OrderBy(e => e.Distance).FirstOrDefault();
                if (moveToTarget == null)
                {
                    Log($"moveToTarget == null");
                }
                else
                {
                    Log(
                        $"We are moving towards the closest entity on grid [{moveToTarget.Name}] TypeId [{moveToTarget.TypeId}] Distance [{moveToTarget.Distance}]");
                }
            }

            // move to the gate/mtu if we need to due time restrictions
            else if (
                     (DoWeNeedToMoveToTheGate() || TargetsOnGrid.Count() <= 4) &&
                     (TargetsOnGridWithoutLootTargets.Any(e => e.Distance < _maxRange) || _currentLockedTargets.Any(e => e.GroupId != 2009) || allDronesInSpace.All(e => e.DroneState == 1 && e.Distance <= _maxDroneRange))
                    )
            {
                if (DirectEve.Interval(5000, 7000))
                {
                    var mtuDistane = _getMTUInSpace != null ? _getMTUInSpace.Distance : -1;
                    var mtuGateDistance = _getMTUInSpace != null
                        ? _getMTUInSpace.DirectAbsolutePosition.GetDistance(_nextGate.DirectAbsolutePosition)
                        : -1;
                    Log(
                        $"We need to move to the gate/MTU, not enough time left. CurrentSpeed [{Math.Round(ESCache.Instance.ActiveShip.Entity.Velocity, 2)}] CurrentStageRemainingSecondsWithoutPreviousStages [{CurrentStageRemainingSecondsWithoutPreviousStages}] CurrentStageRemainingSeconds [{CurrentStageRemainingSeconds}] Stage [{CurrentAbyssalStage}] SecondsNeededToReachTheGate [{_secondsNeededToReachTheGate}] DistanceToGate {_nextGate.Distance} MTUDistance {mtuDistane} MTUGateDist [{mtuGateDistance}]");
                }
                moveToTarget = _getMTUInSpace ?? _nextGate;
            }

            // move to the mtu
            else if (TargetsOnGrid.Count() < 3 && _getMTUInSpace != null ||
                     (_wrecks != null && _wrecks.All(w => w.IsEmpty) && _getMTUInSpace != null))
            {
                moveToTarget = _getMTUInSpace;
            }

            else if
                (AnyEntityOnGridWeWantToBeCloseWith &&
                 allDronesInSpace
                     .Any()) // if there is any entity on grid we want to be close with just go to one of the drones
            {
                moveToTarget =
                    allDronesInSpace.OrderBy(e => e.Id)
                        .FirstOrDefault(); // order by id to pick the same, unless it has been scooped
            }

            // move to target
            if (moveToTarget == null)
                moveToTarget = _getMTUInSpace ?? _nextGate;

            if (moveToTarget == _nextGate)
                _moveDirection = MoveDirection.Gate;

            if (DirectEntity.MoveToViaAStar(stepSize: 3000, distanceToTarget: 12500,
                    forceRecreatePath: forceRecreatePath, dest: moveToTarget.DirectAbsolutePosition,
                    destinationEntity: moveToTarget, ignoreAbyssEntities: IgnoreAbyssEntities,
                    ignoreTrackingPolyons: true, ignoreAutomataPylon: true, ignoreWideAreaAutomataPylon: true))
            {
                if (
                    moveToTarget != null
                    && (!TargetsOnGrid.Any(e => e.IsNPCBattleship) && _nextGate == moveToTarget ||
                        moveToTarget != _nextGate ||
                        moveToTarget == _nextGate &&
                        _singleRoomAbyssal) // don't keep at range when there is a bs on grid and move to target is the gate
                    && ((_getMTUInSpace == moveToTarget || _nextGate == moveToTarget) // mtu or gate
                        && (TargetsOnGrid.Count < 2 || (_wrecks != null && _wrecks.All(w => w.IsEmpty) &&
                                                        _getMTUInSpace != null &&
                                                        !_trigItemCaches.Any(e => e.Distance < _maxDroneRange)))
                        || (CurrentStageRemainingSecondsWithoutPreviousStages - _secondsNeededToReachTheGate - 15 < 0 &&
                            TargetsOnGrid.Count < 4)
                        || _singleRoomAbyssal // single room abyss
                    )
                )
                {
                    if (!moveToTarget.IsApproachedOrKeptAtRangeByActiveShip)
                    {
                        Log($"Keep at range [{moveToTarget.Id}] TypeName [{moveToTarget.TypeName}]");
                        moveToTarget.KeepAtRange(_keepAtRangeDistance);
                    }
                }
                else if (moveToTarget != null && !moveToTarget.IsOrbitedByActiveShip)
                {
                    var orbitDist = moveToTarget == _getMTUInSpace || moveToTarget == _nextGate || moveToTarget == _wrecks.FirstOrDefault()
                        ? _gateMTUOrbitDistance
                        : _enemyOrbitDistance;
                    Log($"Orbiting [{moveToTarget.Id}] TypeName [{moveToTarget.TypeName}] OrbitDist [{orbitDist}]");
                    moveToTarget.Orbit(orbitDist);
                }
            }
        }

        public static List<DirectEntity> GetSortedTargetList(IEnumerable<DirectEntity> list,
            IEnumerable<DirectEntity> averageDistEntities = null)
        {
            return list.OrderByDescending(e => e.Id ^ (long.MaxValue - 1337))
                .OrderBy(e => e.AbyssalTargetPriority)
                .ThenByDescending(e => e.Id == _group1OrSingleTargetId || e.Id == _group2TargetId)
                .ThenBy(e =>
                averageDistEntities == null || !averageDistEntities.Any()
                    ? (int)(e.Distance / 5000)
                    : (int)(e.DistanceTo(averageDistEntities) / 5000)).ToList();
        }

        private int _mtuScoopAttempts = 0;
        private DateTime _lastMTUOpenTime = DateTime.MinValue;

        private HashSet<long> _alreadyLootedItemIds = new HashSet<long>();

        private List<string> _alreadyLootedItems = new List<string>();


        internal bool LootItemsFromContainer(DirectContainer cont)
        {
            if (cont == null)
                return false;

            if (!cont.Items.Any())
                return false;

            if (cont.Window == null)
                return false;

            if (!cont.Window.IsReady)
                return false;

            if (cont.Window.CurrInvIdItem != cont.ItemId)
            {
                Log($"Selecting inv tree item with id [{cont.ItemId}]");
                cont.Window.SelectTreeEntryByID(cont.ItemId);
                return true;
            }

            if (DirectEve.Interval(15000, 20000))
                Log($"Container not empty, looting.");

            var additionalCargoCap = cont.TypeId == _mtuTypeId ? 101 : 0;

            var totalVolume =
                cont.Items.Sum(i =>
                    i.TotalVolume);
            var freeCargo = DirectEve.GetShipsCargo().Capacity -
                            DirectEve.GetShipsCargo().UsedCapacity -
                            additionalCargoCap; // 100 is coming from the MTU itself, which we will scoop after

            if (freeCargo < totalVolume)
            {
                // for now just scoop the mtu to keep going
                Log($"There is not enough free cargo left. Scooping the mtu to keep going.");
                ScoopMTU();
                return true;
            }

            if (DirectEve.Interval(2500, 3500) && cont.Window.LootAll())
            {
                _lastLoot = DateTime.UtcNow;
                float currentLootedValue = 0;
                Log($"Moving loot to current ships cargo.");
                foreach (var item in cont.Items)
                {
                    if (_alreadyLootedItemIds.Contains(item.ItemId))
                        continue;

                    var value = item.AveragePrice() * item.Quantity;
                    Log(
                        $"Item Typename[{item.TypeName}] Amount [{item.Quantity}] Value [{value}]");
                    _valueLooted += value;
                    currentLootedValue += (float)value;
                    _alreadyLootedItemIds.Add(item.ItemId);
                    _alreadyLootedItems.Add($"[{item.TypeName},{item.Quantity}]");
                }

                var totalMinutes = (DateTime.UtcNow - _timeStarted).TotalMinutes;
                if (totalMinutes != 0 && _valueLooted != 0)
                {
                    var millionIskPerHour = (((_valueLooted / totalMinutes) * 60) / 1000000);
                    UpdateIskLabel(millionIskPerHour);
                }

                if (_abyssStatEntry != null)
                {
                    switch (CurrentAbyssalStage)
                    {
                        case AbyssalStage.Stage1:
                            _abyssStatEntry.LootValueRoom1 = Math.Max((float)Math.Round(currentLootedValue / 1000000, 2), _abyssStatEntry.LootValueRoom1);
                            break;

                        case AbyssalStage.Stage2:
                            _abyssStatEntry.LootValueRoom2 = Math.Max((float)Math.Round(currentLootedValue / 1000000, 2), _abyssStatEntry.LootValueRoom2);
                            break;

                        case AbyssalStage.Stage3:
                            _abyssStatEntry.LootValueRoom3 = Math.Max((float)Math.Round(currentLootedValue / 1000000, 2), _abyssStatEntry.LootValueRoom3);
                            break;
                    }
                }
                return true;
            }
            return false;
        }

        internal bool HandleLooting()
        {
            if (_getMTUInBay != null)
            {
                _mtuScoopAttempts = 0;
            }

            if (_getMTUInBay == null && _getMTUInSpace == null)
            {
                if (ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.DoNotUseMTU && _notEmptyWrecks.Any())
                {
                    var nearbyWrecks = _notEmptyWrecks.Where(w => w.Distance < 2500).ToList();
                    if (nearbyWrecks.Any())
                    {
                        var nearbyWreck = nearbyWrecks.FirstOrDefault();
                        var cont = ESCache.Instance.DirectEve.GetContainer(nearbyWreck.Id);
                        if (cont == null)
                        {
                            Log($"Error: Cont == null!");
                            return false;
                        }

                        if (cont.Window == null)
                        {
                            if (DirectEve.Interval(1500, 2500) && nearbyWreck.OpenCargo())
                            {
                                Log($"Opening cargo of wreck [{nearbyWreck.Id}]");
                            }

                            return true;
                        }

                        if (!cont.Window.IsReady)
                        {
                            Log($"Container window not ready yet.");
                            return false;
                        }

                        if (cont.Items.Any())
                        {
                            if (LootItemsFromContainer(cont))
                                return true;
                        }
                    }
                }
                return false;
            }

            var currentStageAddedSeconds =
                CurrentAbyssalStage == AbyssalStage.Stage3
                    ? 20
                    : 0; // remove 20 seconds before starting to empty the mtu in stage 3 (to potentially prevent dying while waiting for the loot)
            // loot and scoop the mtu if all wrecks are empty or if time is running out
            if (
                _wrecks != null && _wrecks.All(w => w.IsEmpty) &&
                !_trigItemCaches.Any(e =>
                    e.Distance <= _maxDroneRange) // Note to myself: && has higher precedence than ||
                || CurrentStageRemainingSecondsWithoutPreviousStages -
                (currentStageAddedSeconds + _secondsNeededToReachTheGate) < 0
            )
            {
                if (_getMTUInSpace != null && _getMTUInSpace.Distance < 2500 &&
                    _lastMTULaunch.AddSeconds(10) < DateTime.UtcNow)
                {
                    var cont = ESCache.Instance.DirectEve.GetContainer(_getMTUInSpace.Id);
                    if (cont == null)
                    {
                        Log($"Error: Cont == null!");
                        return false;
                    }

                    if (cont.Window == null)
                    {
                        if (DirectEve.Interval(2500, 3000) && _getMTUInSpace.OpenCargo())
                        {
                            _lastMTUOpenTime = DateTime.UtcNow;
                            Log($"Opening container cargo.");
                        }

                        return true;
                    }

                    if (!cont.Window.IsReady)
                    {
                        Log($"Container window not ready yet.");
                        return false;
                    }

                    if (cont.Items.Any())
                    {
                        if (LootItemsFromContainer(cont))
                            return true;
                    }
                    else
                    {
                        // if there are no items, scoop it
                        if (_lastLoot.AddMilliseconds(Rnd.Next(1900, 2400)) < DateTime.UtcNow &&
                            _lastMTULaunch.AddSeconds(10) < DateTime.UtcNow && ScoopMTU() && _mtuScoopAttempts < 11)
                        {
                            _mtuScoopAttempts++;
                            Log($"Scooping the MTU. MTUScoop attempts: [{_mtuScoopAttempts}]");
                            return true;
                        }
                    }
                }
            }


            var nearGate = _nextGate != null && _nextGate.Distance <= 11000;
            var nearWrecks = _wrecks != null && _wrecks.Any(w => !w.IsEmpty) && _wrecks.Average(e => e.Distance) < 16000;
            var nearMTUDropSpot = SafeSpotNearGate != null &&
                                  SafeSpotNearGate.GetDistance(DirectEve.ActiveShip.Entity.DirectAbsolutePosition) <=
                                  7000;
            var anyVortonProjectorsOnGrid = TargetsOnGrid.Any(e => e.NPCHasVortonProjectorGuns);

            if (DirectEve.Interval(5000))
                Log($"nearGate: [{nearGate}] nearWrecks: [{nearWrecks}] nearMTUDropSpot: [{nearMTUDropSpot}] anyVortonProjectorsOnGrid: [{anyVortonProjectorsOnGrid}]");

            if (DirectEve.Interval(2500, 5000) && anyVortonProjectorsOnGrid)
            {
                Log(
                    $"------- Vorton projector entity found on grind. Not launching the MTU yet. Vorton entity amount [{TargetsOnGrid.Count(e => e.NPCHasVortonProjectorGuns)}]");
            }

            if (!AreWeInASpeedCloud && (
                    (_wrecks != null && _wrecks.Any(w => !w.IsEmpty))
                    && (nearGate || nearMTUDropSpot || nearWrecks || nearWrecks)
                    && _getMTUInSpace == null
                    && _getMTUInBay != null
                    && !_singleRoomAbyssal
                    && !_mtuAlreadyDroppedDuringThisStage
                    && !anyVortonProjectorsOnGrid
                    && IsOurShipWithintheAbyssBounds()
                )
            )
            {
                if (_lastMTUScoop.AddSeconds(10) < DateTime.UtcNow && _lastMTULaunch.AddSeconds(10) < DateTime.UtcNow &&
                    DirectEve.Interval(1500, 2500))
                {
                    if (Framework.Me.IsDownTimeComingSoon() ||
                        LaunchMTU()) // this is and should be the only spot where we launch the MTU.
                    {
                        _mtuAlreadyDroppedDuringThisStage = true;
                    }

                    return true;
                }
            }

            return false;
        }
    }
}