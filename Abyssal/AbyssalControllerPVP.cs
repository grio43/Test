//
// (c) duketwo 2022
//

extern alias SC;
using EVESharpCore.Cache;
using EVESharpCore.Controllers.ActionQueue.Actions.Base;
using EVESharpCore.Framework;
using EVESharpCore.Framework.Lookup;
using ServiceStack;
using System;
using System.Linq;

namespace EVESharpCore.Controllers.Abyssal
{
    public partial class AbyssalController : AbyssalBaseController
    {
        //// TODO: any better approach? Generating random directions and evaluating traversal velocity / orthogonality? We even want to move further away while maintaining traversal

        //public Vec3 CalculateAverageDirection(List<DirectEntity> enemies)
        //{
        //    Vec3 sumDirection = new Vec3(0, 0, 0);

        //    DirectEntity currentShip = Framework.ActiveShip.Entity;

        //    foreach (DirectEntity enemyShip in enemies)
        //    {
        //        // Calculate the vector connecting spaceship to enemy ship
        //        Vec3 direction = new Vec3(enemyShip.X - currentShip.X, enemyShip.Y - currentShip.Y, enemyShip.Z - currentShip.Z);

        //        // Add the direction to the sum
        //        sumDirection.X += direction.X;
        //        sumDirection.Y += direction.Y;
        //        sumDirection.Z += direction.Z;
        //    }

        //    // Calculate the average direction
        //    int numShips = enemies.Count;
        //    Vec3 averageDirection = new Vec3(sumDirection.X / numShips, sumDirection.Y / numShips, sumDirection.Z / numShips);

        //    return averageDirection;
        //}

        public void PVPHandleLaunchDrones()
        {
            var dronesIWant = GetWantedDronesInSpace();
            if (dronesIWant.Any())
            {
                LaunchDrones(dronesIWant);
            }
        }

        public void PVPHandleAttackTargets()
        {
            var targets = Framework.Entities.Where(e => e.IsPlayer && e.IsAttacking && e.Distance <= _maxRange && e.GroupId != (int)Group.Capsule).OrderBy(e => e.OwnerId).ThenBy(e => e.IsWarpScramblingOrDisruptingMe).ToList();
            var droneTarget = targets.FirstOrDefault();
            var weaponTarget = _currentLockedAndLockingTargets.Where(e => e.Distance <= _weaponMaxRange && e.IsPlayer && e.IsAttacking && e.GroupId != (int)Group.Capsule).FirstOrDefault();

            // Lock targets
            foreach (var target in targets)
            {
                if (_currentLockedAndLockingTargets.Count() < _maximumLockedTargets)
                {
                    if (!droneTarget.IsTarget)
                    {
                        if (droneTarget.IsTargeting)
                            continue;

                        Log($"Locking target. Id [{droneTarget.Id}] Name [{droneTarget.Name}] TypeName [{droneTarget.TypeName}] Distance [{droneTarget.Distance}]");
                        droneTarget.LockTarget();
                        break;
                    }
                }
                else
                {
                    if (DirectEve.Interval(2000))
                        Log($"Maximum target amount reached. Currently Locked Targets [{_currentLockedAndLockingTargets.Count()}] Max Targets [{_maximumLockedTargets}]");
                }
            }

            // Unlock targets
            if (droneTarget != null && !droneTarget.IsTarget && _currentLockedAndLockingTargets.Count() >= _maximumLockedTargets)
            {
                var unlockTarget = _currentLockedTargets.OrderBy(e => e.Distance).FirstOrDefault();
                if (unlockTarget != null)
                {

                    Log($"Unlocking target. Id [{unlockTarget.Id}] Name [{unlockTarget.Name}] TypeName [{unlockTarget.TypeName}] Distance [{unlockTarget.Distance}]");
                    unlockTarget.UnlockTarget();
                }
            }

            // Attack with drones drones
            if (droneTarget != null)
            {


                if (!droneTarget.IsTarget)
                {
                    if (DirectEve.Interval(2000))
                        Log($"Target is not yet targeted. Id [{droneTarget.Id}] Name [{droneTarget.Name}] TypeName [{droneTarget.TypeName}] Distance [{droneTarget.Distance}]");
                    return;
                }

                var nonAttackingDrones = allDronesInSpace.Where(d => d.DroneState != 4 && d.DroneState != 1 && d.FollowEntity != droneTarget);
                if (nonAttackingDrones.Any())
                {
                    Log($"Attacking Target Id [{droneTarget.Id}] TypeName [{droneTarget.TypeName}] with the following drones: {string.Join(", ", allDronesInSpace.Select(d => d.TypeName).ToArray())}");
                    droneTarget.EngageTargetWithDrones(nonAttackingDrones);
                }
            }
            // Attack with guns
            if (weaponTarget != null)
            {

                var shipsCargo = DirectEve.GetShipsCargo();
                if (shipsCargo == null)
                    return;

                var weapons = ESCache.Instance.DirectEve.Weapons;

                if (weapons.Any(w => w.IsInLimboState))
                    return;

                if (!weapons.Any(_ => _.IsMaster))
                    return;

                // reload weapon
                if (weapons.Any(w => w.CurrentCharges == 0 || w.CurrentCharges > w.MaxCharges))
                {
                    var weap = weapons.FirstOrDefault(w => w.CurrentCharges == 0 || w.CurrentCharges > w.MaxCharges);
                    if (shipsCargo.Items.Any(e => e.TypeId == _ammoTypeId) && shipsCargo.Items.Any(e => e.Stacksize > 100))
                    {
                        var ammo = shipsCargo.Items.Where(e => e.TypeId == _ammoTypeId).OrderByDescending(e => e.Stacksize)
                            .FirstOrDefault();
                        if (DirectEve.Interval(1800, 2500) && weapons.FirstOrDefault().ChangeAmmo(ammo))
                        {
                            Log($"Reloading weapon.");
                            return;
                        }
                    }
                    else
                    {
                        if (DirectEve.Interval(5000, 9000))
                            Log($"Not enough ammo left.");
                    }
                }

                if (weapons.Any(w => w.IsActive))
                {
                    return;
                }

                // here we attack the target
                var weapon = weapons.FirstOrDefault(w => w.CurrentCharges > 0);

                if (weapon == null)
                    return;

                if (DirectEve.Interval(1000, 1500))
                {
                    Log($"Attacking Target Id [{droneTarget.Id}] TypeName [{droneTarget.TypeName}] with the weapons.");
                    weapon.Activate(weaponTarget.Id);
                    return;
                }
            }
        }

        private bool _skipWaitOrca = false;
        private bool _forceOverheatPVP = false;
        private long _starDist = 500000000;


        private bool IsItemValidInShipMaintenanceBay(DirectItem item)
        {
            if (item == null) return false;
            if (!item.PyItem.IsValid) return false;
            if (item.CategoryId == (int)CategoryID.Charge) return true;
            if (item.CategoryId == (int)CategoryID.Module) return true;
            //if (item.CategoryId == (int)CategoryID.Implant) return true;
            if (item.GroupId == (int)Group.Booster) return true;
            if (item.TypeId == 16273) return true; //  Liquid Ozone
            if (item.CategoryId == (int)CategoryID.Deployable) return true;
            if (item.GroupId == (int)Group.AbyssalDeadspaceFilament) return true;// Filament
            if (item.TypeId == 16275) return true; //  Strontium Clathrates

            //You cannot store a ship that contains cargo other than Charges, Boosters, Filaments, Modules, Rigs, Deployables, Liquid Ozone and Strontium Clathrates in this Bay.< br >< br > Please remove the invalid cargo from the ship and try again.
            return false;
        }

        private ActionQueueAction _pvpActionQueueAction = null;

        // We enter this state from: ""a)"" InvulnPhaseAfterAbyssExit and ""b)"" while being attack by a player (so we need to handle docking up again too, i.e if agressed outside of a station)
        public void PVPState()
        {

            try
            {
                //Handle docked state
                if (Framework.Session.IsInDockableLocation)
                {
                    Log($"We are in a dockable location. Changing to error state.");
                    State = AbyssalState.Error;
                    return;
                }

                var star = ESCache.Instance.Star;
                if (ESCache.Instance.Star.Distance <= _starDist && !Framework.Entities.Any(e => e.IsAttacking && e.IsPlayer) && Framework.Me.CanIWarp())
                {
                    Log($"Looks like we managed to get to the star and there are no other players in range. Changing the state.");
                    State = AbyssalState.Error;
                    return;
                }

                // What we do while being in a capsule
                if (Framework.ActiveShip.GroupId == (int)Group.Capsule)
                {

                    if (DirectEve.Interval(1000) && star != null)
                    {
                        Log($"Distance to the star [{star.Distance}]");
                    }

                    if (Framework.Me.CanIWarp() && !Framework.ActiveShip.Entity.IsWarpingByMode)
                    {

                        if (star != null)
                        {
                            if (ESCache.Instance.Star.Distance > _starDist)
                            {
                                Log($"We are in a capsule and warping to the star.");
                                star.WarpTo();
                            }
                            else
                            {
                                Log($"It looks like we are at the star, changing the state which will bring us home");
                                State = AbyssalState.Error;

                            }
                        }
                    }
                    return;
                }

                // Handling while invulnerable
                if (ESCache.Instance.DirectEve.Me.IsInvuln && AreWeAtTheFilamentSpot && IsAnyOtherNonFleetPlayerOnGrid)
                {

                    if (DirectEve.Interval(2000))
                    {
                        Log($"We are at at the filament spot, are invulnerable and there are other players on grid. Seconds of invuln left: [{Math.Round(ESCache.Instance.DirectEve.Me.InvulnRemainingSeconds(60), 2)}] seconds.");
                        Log($"Non Fleet player on grid ---- [{string.Join(",", OtherNonFleetPlayersOnGrid.Select(e => e.DirectEntity.Name + " -- " + e.DirectEntity.TypeName))}]");
                    }

                    if (Framework.FleetMembers.Count > 1 && !_skipWaitOrca)
                    {
                        if (DirectEve.Interval(3000))
                            Log($"We are in a fleet that has at least two members, we assume that an orca will warp to us soon.");

                        if (ESCache.Instance.DirectEve.Me.InvulnRemainingSeconds(60) > 7)
                        {
                            // Orca handling
                            // Is there any orca which has the owner of any party member?
                            var friendlyOrca = Framework.Entities.FirstOrDefault(e =>
                                Framework.FleetMembers.Any(f =>
                                    f.CharacterId == e.OwnerId
                                    && f.CharacterId != Framework.Session.CharacterId
                                    && Framework.ActiveShip.OwnerId != f.CharacterId)
                            );
                            if (friendlyOrca != null)
                            {
                                // Open the fleet hangar if the ship is on grid
                                var fleetHangar = friendlyOrca.GetFleetHangarContainer();
                                if (fleetHangar == null || !fleetHangar.IsValid || !fleetHangar.IsReady)
                                {
                                    Log($"FleetHangar is null/invalid/not ready.");
                                    return;
                                }

                                if (DirectEve.Interval(2000))
                                    Log($"There is a friendly orca on grid from CharacterId [{friendlyOrca.OwnerId}] Distance [{friendlyOrca.Distance}] Mode [{friendlyOrca.Mode}]");

                                if (friendlyOrca.Distance < 2500)
                                {
                                    // TODO: Clarify if we can we drop it into the orca while the orca is still in warp mode?
                                    if (DirectEve.Interval(2000))
                                        Log($"The orca is in range to store the ship in the maintenance bay. Distance [{friendlyOrca.Distance}]. Trying to store the ship in the bay.");

                                    if (DirectEve.Interval(200, 250))
                                    {
                                        // TODO: You cannot store a ship that contains cargo other than Charges, Boosters, Filaments, Modules, Rigs, Deployables, Liquid Ozone and Strontium Clathrates in this Bay.<br><br>Please remove the invalid cargo from the ship and try again.
                                        // TODO: We need to sore all others items (all others are prefered) (or just all) in the fleet hanger before we try to store the ship.

                                        var shipsCargo = Framework.GetShipsCargo();

                                        if (shipsCargo == null || !shipsCargo.IsValid || !shipsCargo.IsReady)
                                        {
                                            Log($"ShipsCargo is null/invalid/not ready.");
                                            return;
                                        }

                                        if (shipsCargo.Items.Any())
                                        {
                                            fleetHangar.Add(shipsCargo.Items);
                                            Log($"Moving all items into the fleethangar");
                                            return;
                                        }

                                        Log($"Trying to store the ship in the orca.");
                                        // We don't need any additional error/amount checks here, as we only try to store while invulnerable
                                        friendlyOrca.StoreCurrentShipInShipMaintenanceBay();
                                    }
                                }
                            }
                            return;
                        }

                        // Here are only 7 seconds left until we drop invuln, skip waiting for the orca
                        Log($"Set _skipWaitOrca to true.");
                        _skipWaitOrca = true;
                        return;
                    }

                    // Non orca handling

                    // Overheat while we are still invuln
                    _forceOverheatPVP = true;
                    var medRackHeatStatus = ESCache.Instance.ActiveShip.MedHeatRackState(); // medium rack heat state
                    var medRackExceeded = medRackHeatStatus >= _heatDamageMaximum;
                    var anyModuleBurnedOut = DirectEve.Modules.Any(m => m.HeatDamagePercent >= _heatDamageMaximum);
                    var boosterOverheat = ShieldBoosters.All(m => m.IsPendingOverloading || m.IsOverloaded) || !ShieldBoosters.Any() || anyModuleBurnedOut || medRackExceeded;
                    var hardenerOverheat = ShieldHardeners.All(m => m.IsPendingOverloading || m.IsOverloaded) || !ShieldHardeners.Any() || anyModuleBurnedOut || medRackExceeded;
                    var afterBurnerOverheat = PropMods.All(m => m.IsPendingOverloading || m.IsOverloaded) || !PropMods.Any() || anyModuleBurnedOut || medRackExceeded;

                    if (DirectEve.Interval(1000))
                    {
                        Log($"anyModuleBurnedOut {anyModuleBurnedOut} boosterOverheat[{boosterOverheat}] hardenerOverheat [{hardenerOverheat}] afterBurnerOverheat [{afterBurnerOverheat}]");
                    }

                    if (!boosterOverheat || !hardenerOverheat || !afterBurnerOverheat)
                        return;

                    if (DirectEve.Interval(1000))
                        Log($"Modules are all pending to overload. We are going to drop the invuln now. While moving the module handling code part will activate the modules.");


                    // Start an action queue action to do the warp trick with a MWD/AB 
                    // Only do when we have an AB/MWD fit
                    if (_pvpActionQueueAction == null && PropMods.Any())
                    {
                        DateTime timeout = DateTime.UtcNow.AddSeconds(25);
                        bool propModEnabledOnce = false;
                        bool propModDisabledOnce = false;
                        bool error = false;
                        DateTime disablePropModAfter = DateTime.MaxValue;
                        DateTime nextWarpAttempt = DateTime.MinValue;
                        DateTime activatedPropMod = DateTime.MinValue;
                        var msOffset = 0;
                        // This runs on every frame
                        _pvpActionQueueAction = ActionQueueAction.Run(() =>
                        {
                            if (DateTime.UtcNow > timeout || !ESCache.Instance.DirectEve.Me.IsInvuln || error)
                            {
                                Log($"_pvpActionQueueAction timeout hit or not invulnerable anymore or error, stopping execution. Timeout [{DateTime.UtcNow > timeout}] Invuln [{!ESCache.Instance.DirectEve.Me.IsInvuln}] Error [{error}]");
                                _pvpActionQueueAction.RemoveAction();
                                _pvpActionQueueAction = null;
                            }

                            // Enable the after burner once
                            if (!propModEnabledOnce && PropMods.Any() && !Framework.Me.IsWarpingByMode)
                            {
                                foreach (var m in PropMods)
                                {
                                    if (m.HeatDamagePercent <= 99 && m.IsActivatable && !m.IsActive && !m.IsInLimboState)
                                    {
                                        if (DirectEve.Interval(950, 1750))
                                        {
                                            Log($"Activating module. Typename [{m.TypeName}] Id [{m.ItemId}]");
                                            m.Click();
                                            propModEnabledOnce = true;
                                            disablePropModAfter = DateTime.UtcNow.AddMilliseconds(Rnd.Next(1500, 2500));
                                            activatedPropMod = DateTime.UtcNow;
                                        }
                                    }
                                }
                            }
                            // Once we have enabled the afterburner, disable it after a random time
                            if (!propModDisabledOnce && propModEnabledOnce && disablePropModAfter <= DateTime.UtcNow)
                            {
                                foreach (var m in PropMods)
                                {
                                    if (m.IsActivatable && m.IsActive && !m.IsInLimboState)
                                    {
                                        if (DirectEve.Interval(950, 1750))
                                        {
                                            Log($"Disabling module. Typename [{m.TypeName}] Id [{m.ItemId}]");
                                            m.Click();
                                            propModDisabledOnce = true;
                                        }
                                    }
                                }
                            }

                            var propmod = PropMods.FirstOrDefault();
                            //if (DirectEve.Interval(500))
                            //    Log($"PropMod Isactive [{propmod.IsActive}] propEff.IsValid [{(propEff?.Item1 ?? false)}] propModMillisecondsUntilNextCycle [{propModMillisecondsUntilNextCycle}]");

                            if (propModDisabledOnce && propmod.IsActive && propmod.MillisecondsUntilNextCycle.HasValue)
                            {
                                var propModMillisecondsUntilNextCycle = propmod.MillisecondsUntilNextCycle.Value;
                                var propModMillisecondsUntilNextCycle2 = propmod.EffectDurationMilliseconds - (DateTime.UtcNow - activatedPropMod).TotalMilliseconds;
                                var topSpeedWithoutPropMod = Framework.ActiveShip.GetMaxVelocityBase();
                                var topSpeedWithPropMod = Framework.ActiveShip.GetMaxVelocityWithPropMod();
                                var speedRequiredToEnterWarpWithoutPropMod = topSpeedWithoutPropMod * 0.75;
                                var percSpeedNeeded = speedRequiredToEnterWarpWithoutPropMod / topSpeedWithPropMod;
                                var millisecondsNeededToReachThatSpeed = Framework.ActiveShip.GetSecondsToWarpWithPropMod(percSpeedNeeded) * 1000;
                                if (msOffset == 0)
                                    msOffset = Rnd.Next(650, 750);
                                //if (DirectEve.Interval(500))
                                //{
                                //    Log($"topSpeedWithoutPropMod [{topSpeedWithoutPropMod}] topSpeedWithPropMod [{topSpeedWithPropMod}] speedRequiredToEnterWarpWithoutPropMod [{speedRequiredToEnterWarpWithoutPropMod}] percSpeedNeeded [{percSpeedNeeded}] millisecondsNeededToReachThatSpeed [{millisecondsNeededToReachThatSpeed}] propModMillisecondsUntilNextCycle [{propModMillisecondsUntilNextCycle}]");
                                //}

                                if (propModMillisecondsUntilNextCycle >= 0 && propModMillisecondsUntilNextCycle <= millisecondsNeededToReachThatSpeed + msOffset)
                                {
                                    if (DirectEve.Interval(500))
                                        Log($"Proc! propModMillisecondsUntilNextCycle [{propModMillisecondsUntilNextCycle}] propModMillisecondsUntilNextCycle2 [{propModMillisecondsUntilNextCycle2}]");

                                    var star = ESCache.Instance.Star;
                                    if (DirectEve.Interval(150, 250) && !Framework.Me.IsWarpingByMode && Framework.Me.CanIWarp() && star.Distance > _starDist)
                                    {
                                        if (star.WarpToAtRandomRange())
                                        {
                                            Log($"Trying to warp to the star at a random range.");
                                            SendBroadcastMessage("*", nameof(InstalockController), "WARP_START", string.Empty);
                                            var innerTimeout = DateTime.UtcNow.AddSeconds(10);
                                            var timeStarted = DateTime.UtcNow;
                                            var waitedForWarpByMode = false;
                                            ActionQueueAction innerAction = null;

                                            Log($"Adding inner action queue action.");
                                            innerAction = ActionQueueAction.Run(() =>
                                            {
                                                if (innerTimeout <= DateTime.UtcNow)
                                                {
                                                    Log($"InnerAction timeout.");
                                                    innerAction.RemoveAction();
                                                }

                                                if (!waitedForWarpByMode && Framework.Me.IsWarpingByMode)
                                                {
                                                    Log($"We are warping by mode, waiting for warp to finish.");
                                                    waitedForWarpByMode = true;
                                                    timeStarted = DateTime.UtcNow;
                                                    return;
                                                }

                                                if (Framework.ActiveShip.Entity.IsWarping)
                                                {
                                                    var secondsElapsed = DateTime.UtcNow.Subtract(timeStarted).TotalSeconds;
                                                    Log($"It took [{Math.Round(secondsElapsed, 2)}] seconds to initialize the warp.");
                                                    innerAction.RemoveAction();
                                                }

                                            }, true, true);
                                        }
                                    }
                                }
                            }
                        }, true, true);
                    }

                    // Drop the invuln now when we don't have an AB fit
                    if (!PropMods.Any() && DirectEve.Interval(1500, 4000))
                    {
                        //var dir = ESCache.Instance.ActiveShip.MoveToRandomDirection();
                        //Log($"Moving into a random direction to break the abyss invuln. Seconds of invuln left: [{Math.Round(ESCache.Instance.DirectEve.Me.InvulnRemainingSeconds(60), 2)}] seconds.");
                        star.AlignTo();
                        Log($"Moving to the star to break the abyss invuln. Seconds of invuln left: [{Math.Round(ESCache.Instance.DirectEve.Me.InvulnRemainingSeconds(60), 2)}] seconds.");
                        LocalPulse = UTCNowAddMilliseconds(1000, 2500);
                        return;
                    }

                    return; // During abyss invuln we can launch drones and activate / overheat modules, wait here until we have the modules activated and the drones launched?
                }

                // Here we dropped the invuln state
                // Handle pvp here, this is also the entry point if we were engaged on other spots in space

                //Handle if we are in a docking range to a station or a gate
                var closestStation = ESCache.Instance.Stations.OrderBy(e => e.Distance).FirstOrDefault();
                if (closestStation != null && closestStation.Distance <= 0 && !Framework.Me.WeaponsTimerExists)
                {

                    if (DirectEve.Interval(500, 1000) && Framework.Me.IsWarpingByMode)
                    {
                        Log($"Aborting the warp.");
                        ESCache.Instance.DirectEve.ExecuteCommand(EVESharpCore.Framework.DirectCmd.CmdStopShip);
                        return;
                    }

                    if (DirectEve.Interval(1000))
                        Log($"Looks like that we are in docking range to the station [{closestStation.Name}] Distance [{closestStation.Distance}]. Trying to dock.");

                    if (DirectEve.Interval(1000, 2500) && closestStation.Dock())
                    {
                        Log($"Docking.");
                    }
                    return;
                }

                var closestStargate = ESCache.Instance.Stargates.OrderBy(e => e.Distance).FirstNonDefault();
                if (closestStargate != null && closestStargate.Distance <= 0 && !Framework.Me.WeaponsTimerExists)
                {

                    if (DirectEve.Interval(500, 1000) && Framework.Me.IsWarpingByMode)
                    {
                        Log($"Aborting the warp.");
                        ESCache.Instance.DirectEve.ExecuteCommand(EVESharpCore.Framework.DirectCmd.CmdStopShip);
                        return;
                    }

                    if (DirectEve.Interval(1000))
                        Log($"Looks like that we are in jump range to a stargate [{closestStargate.Name}] Distance [{closestStargate.Distance}]. Trying to jump.");

                    if (DirectEve.Interval(1000, 2500) && closestStargate.Jump())
                    {
                        Log($"Jumping.");
                    }
                    return;
                }

                // Launch drones only if we are being attacked or if there are 3 or more players next to us
                if ((IsAnyPlayerAttacking || Framework.Entities.Count(e => e.IsPlayer && e.Distance < 25_000) > 3) && !Framework.Me.IsWarpingByMode)
                {
                    if (DirectEve.Interval(2000, 2000))
                        Log($"Drone handling / target handling proc, due we are being attacked or there are at least 3 enemies close to us.");

                    PVPHandleLaunchDrones();
                    PVPHandleAttackTargets();
                }

                // Try to warp as soon as we can. Yes -- at that point we gonna lose the drones if we launched them before.
                if (!Framework.Me.IsWarpingByMode && Framework.Me.CanIWarp() && star.Distance > _starDist)
                {
                    if (DirectEve.Interval(2000, 2000))
                        Log($"Looks like we are not scrambled/disrupted currently, trying to warp to the star.");

                    if (DirectEve.Interval(1500, 2000))
                    {
                        star.WarpToAtRandomRange();
                    }
                }

            }
            catch (Exception ex)
            {
                Log($"Exception: {ex}");
            }
            finally
            {
                if (DirectEve.Interval(1000))
                {
                    // Log pvp state every second to know what the fuck is actually happening
                    // Which modules are active, which are overheated
                    // Enemies and their distances, which are agressing
                    // Drones and their targets
                    // Log invuln state and remaining seconds left
                    // Log fleet member size
                }
            }
        }
    }
}
