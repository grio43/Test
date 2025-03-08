//
// (c) duketwo 2022
//

// |-- TODO ------------------------------------------->
// 01. Missing statistics: [DroneWaitToReturnStage1 .. 3, WeaponMissesPerStage1 .. 3 DroneMissesPerStage1  .. 3 (read from game logs)], AssaultDamageControlActivated
// 02. -DONE- We may drop the MTU before if the time constraints require us to do so. (i.e EstimatedGridKillTime < RequiredTimeToTractorAllWrecks)
// 03. -DONE- Get the fuck away from neuts. Somehow?
// 04. -DONE- Abyss stage persistence (so we can crash/reconnect with any issue) [done... confirm working state]
// 05. -DONE- Further improve drone launch/scoop. Maybe add additional logic to loop (launch->attack->scoop) to prevent npc locks on the drones. Some enemies are oneshotting med/small drones. Especially in some clouds.
// 05. -DONE- Make use of yellow boxing, entity followId, we are just plain using the followId
// 06. -DONE- Enforce better cap usage. (i.e. perma shield boost only if cap is above > 50, else boost only if the shieldDamage >= shieldBoosterShieldAmount)
// 07. -SKIP- If we are about to die due neuts, spam the shield booster to possibly get one cycle. (Maybe: Boost if currentCap >= shieldBoosterRequiredCapAmount)
// 08. -DONE- Make use of Nanite repair paste
// 09. -NOPE- Some drones are webbed, and we are moving away from them faster than they can get to us. (Move towards them, while they are in armor?)
// 10. -DONE- Activate the shieldbooster while shutting down (for whatever reason) -- We should add some event if the server is shutting down and do something during the last 10 seconds
// 11. -DONE- Keep the Shield booster up as much as possible (in case of a crash)
// 12. -DONE- If there is a vorton projector (how do we know if there are voltron projectors?) on the grid, STAY more than 10KM away from the MTU. Voltron projectors have up to 10KM chaining range. [fixed by not deploying the MTU until there is no vorton projector left.
// 13. -DONE-
// 14. -TEST- Add code to leave the the MTU behind (if we can't scoop it for whatever reason) -- It never happened, but it would be a deadend case, so handle it.
// 15. -IN PROGRESS- Think about how to be more time efficient. What is the optimal pathing? How can we achieve that in code? (i.e. calculating the best spot for the loot, which would be the spot which is the nearest to all loot containers + gate)
// 16. -DONE- We can't go upwards (in case of neuts) through a speed bubble, this will bring us too far outside and kill us. Alternative: Enforce a speed limit while being outside of the bounds and have very high speed (in a speed cloud)! (Already slow down if we are close to the boundary?)
// 17. -DONE- Better weapon management (20k max range, pref closer targets but don't swap too frequently!)
// 18. -DONE- Orbit the gate properly, also orbit the MTU properly (don't turn of the afterburner if there are more than X enemies on the grid?)
// 19. -SEMI DONE- Get data about currentNPCDamageOnGrid (do we even need that?), NeutAmountOnGrid (in optimal will be enough for both calculations to compare different spawns and set thresholds)
// 20. -DONE- (Reworked moveOnGrid) There is a bug within the MoveOnGrid method which selects the gate as moveToTarget for whatever reason when it shouldn't. (It doesn't break anything)
// 21. -DONE- (By fitting) Another deadgecase: If we go down to 0 cap due neuts and manage to survive, the cap regen is not quick enough to stabilize again while the shield booster is being spammed. This will fail horribly while being outside of the abyss boundary where we need the 400-500 hp/s boosts.
// 22. -DONE- (We're not leaving bounds anymore with new fit) Need log about how far we are outside of the abyss bounds.
// 23. -DONE- The tesser blaster spawn still can wipe our drones. Any strategy for this spawn? We possibly solved this by delaying the drone spawn after a session change. Awaiting confirmation [..]
// 24. -SKIP- We need a logic to move to the MTU faster, need check if the looting has been finished. Calculate ahead of time, when looting will be finished and move to the MTU accordingly. This is automatically done by the "time needed to get to the gate" method.
// 25. -DONE- Often we are still moving to the gate after the spawn has finished. Any better logic to be at the gate when all the grid is cleared?
// 26. -DONE- Add a bool = "IgnoreAbyssEntitiesDuringAStarPathfinding"
// 27. -DONE- If there is a vorton projector entity on grind, focus the targets which are further than 10k away from us. Else we kill our drones and waste a lot of time via constant recalling. What if all vorton NPCs are within 10k?
// 28. -DONE- We should not move too far away from the MTU if we are following the current target. We fixed that by calculating the time needed to move to the MTU/GATE. Still needs more testing
// 29. -DONE- We died to this: 1x Triglavian Extraction SubNode,1x Triglavian Extraction Node,1x Triglavian Bioadaptive Cache,4x Tangling Kikimora,4x Anchoring Damavik,1x Ghosting Kikimora,3x Striking Kikimora,2x Striking Damavik,1x Renewing Rodiva
// 30. -DONE- If we go backwards by the override, we should determine if the majority of enemies are in their optimal range, and if so, to save time we should move to to the gate if we are close to the abyss boundary.
// 31. -DONE- Damper priority, Weapon disruption ewar enemies / Anti Tracking which are targeting our drones
// 32. -DONE- Implement functional to launch drones if the majority (> 80%) of enemies are targeting us (except remote repair), also add a wait timeout (10 seconds since last session change)
// 33. -DONE- Possible dead case: If we fight rougue drones (or any other high dps spawn) while ignoring abyss entities, we might end up in a 4x sig radius bubble. This can kill us most likely. What to do about this? [We now move backwards, which seems fine]
// 34. -IN PROGRESS- Add some variations to look more like a hooman! Randomize fixed/static numbers (ammo loading numbers ... fixed percentage speed decrease in speed clouds ... what else? Fixed overheat times i.e.)
// 35. -TEST- [Abandon impl missing] If we are in a speed bubble during a stage and returning drones, they might never come back to us. Any strategy? (Move to the drone which isn't returning since X seconds..? Abandon drones in the worst case? Move out of the speed cloud?)
// 36. -SEMI DONE- Prio higher damage targets ... Can we determine them dynamically? For now maybe just prio "striking"? 
// 37. -DONE- Fix Overheat. Propmod: Only overheat if we need to make distance to enemies, or if we can save time while moving to the gate. Hardener: We can overheat this one for a longer time
// 38. -MAYBE NOT REQUIRED- If we go backwards to the move to an override spot, check if we would path through a x4 sigradius cloud. If yes, we need to use different angles and find a backwards direction which is not passing through a sigcloud. It's maybe not required as the grid will be mostly cleared when the high DPS NPCs come close.
// 39. We need to know if we are in an abyss bubble and which type the bubble has. Also add some meaningful statistics to it.
// 40. -DONE- Any statistics for target/drone target swaps, also any better logic to reduce the amount of swaps
// 41. -CCP FIXED IT- Since the last eve patch were only consuming one booster (CCP BUG), enforce to always consume both if one was consumed.
// 42. -UNSURE- Should we also load T2 drones too and use them on certain spawns for more dmg? (Deepwatcher, Rogue Drone BS, Drifter BS, Ephi) -- Spawns with high HP, but lower amount of drone recalls if compared to avg drone recalls
// 43: -DONE- To make it more reliable we need to handle the following cases: a) -DONE- abandon drones impl missing] Track drone return duration timers and move outside of a speed cloud if necessary and possibly abandon drones if there is no other option
//                                                                            b) -DONE- Add code to leave the MTU behind if we can't scoop it for whatever reason, which is a very rare case, but we would be stuck at that point
//                                                                            c) -DONE- Add a logic to have the drones always at more than 10k away while fighting vorton projector NPCs
// 44. -DONE- Remove IsEntityWeWantToAvoidInAbyssals from Entity, it should be possible to set it on each call (default in the function header .. yeah it will be a bit of a mess, but opens the gates for finer grained avoidances)
// 45. We should ignore neuts if the neut GJ/s are below what our cap can handle. Neut NPCs usually do little damage, so most of the time better to spend the gained time elsewhere (i.e. killing targets which are focusing the drones or anything else)
// 46. -DONE- We got oneshot because we couldn't overheat. A repair cycle was active. Disable all repair if we need to overheat.
// 47. -SKIP- Add stats for how long we are waiting for the drones to return on each stage.
// 48. -DONE- Stop repairs if overheat.
// 49. -DONE- Ensure drones are alaways attacking. (There is some deadend state currently)
// 50. Add any logic to keep going when we reach the set threshold of items in the hangar. Currently it's set to 600. What can do we? Delete non faction blueprints? Or do we just leave the non faction BPCs behind (do not loot them -- which may be a downside in the detectability department)?
// 51. -DONE- Randomize the keep at range / orbit distances.
// 52. We still want some numbers on the hitrate of the drones and weapons. Total hits fired / total hits landed.
// 53. -DONE- Make the thresholds for the database survey dump configurable.
// 54. -SEMI DONE- Drones do not hit small targets properly while in a speed cloud. What can we do about it? We should move outside from speed clouds. Do we? If yes, we could recall drones if all entities that are in a speed cloud or focus the targets which are outside? Anything else? We need to lure them out of the cloud somehow while not losing much time.
// 55. -REQUIRES A BETTER IDEA- To not to abandon drones, we could order them to attack the gate maybe [edit: you can't attack the gate with drones, any other idea]? So they don't MWD. Then move towards them and scoop? Is this viable? What about return and orbit?
// 56. -DONE- If we are in a station and in a pod -> Disabled this instance
// 57. -DONE- Check if any module is offline before entering an abyss.
// 58. We need to also automatically sell items, at least the filaments to keep the market stable.
// 59. -DONE- If we are sitting on the safe spot near gate and moving in and out of a speed cloud, we constantly deactivate/activate/overheat the after burner. Any solution to this? Limit max prop mod overheats per stage? Do only disable the prop mod every (25,35) seconds (given enough cap avail?). Maybe we start to stop moving while on the safe spot near gate unless we drop in shields?
// 60. -DONE- Save the fucking pod somehow if we get ganked. This is very sloppy in the current state.
// 61. -DONE- If we are constantly recalling drones, we need to be closer to the targets to reduce dps downtime. If they are in a speed cloud we need to lure them away by recalling drones and waiting a bit for them to get out. For any other case it will probably be fine to move close to them? (Except high dps targets..)
// 62. -SEMI DONE- What can we do that drones are always focusing the 'correct target' while maintaining a low amount of drone commands? Constant swapping hurts the dps.
// 63. -WIP- We need to properly handle gankers on the abyss spot. If they are on the spot, we need to use the invuln timer to activate the mods and launch drones. Also overheat beforehand. It needs to ensure that the assault damage control is delayed as much as possible to get as much time as possible. Also the drones and the guns needs to target a single enemy to reduce dps as quickly as possible.
// 64. If we swap the state to the error state, always give a reason and present the reason as notification and while being in the error state.
// 65. -DONE- We should move the logging out from the BuildBuyList method, as we create false log entries if this is being called while we already switched to a transport ship
// 66. -DONE- We should focus the non elite cyabals over the elite cynabals, same damage but a lot less tank. This will reduce the dps of the spawn a lot faster.
// 67. Snapshot of positions, type ids at the beginning of the stage (maybe health?)
// 68. Heatpercent damage of modules via the stats every 30 seconds, add additional logging if heat damage perc is >= 99 for any module
// 69. -DONE- Approach does not work if >= 150k, fix that
// 70. -TEST- Ensure that we are not moving outside of the abyss while we are in a cloud (the pathfinding will always take the shortest way outside of a cloud, which might bring us outside of the abyss sphere -> BAD)
// 71. Log how far the player is outside of the abyss boundary AND if we are outside of the abyss, maybe even log the distance every 5 seconds if we are outside
// 72. -SEMI DONE- Mutated Drone stats are not available initally (only avail after hovering) -- It is now fine for drones in bay, but when you launch drones before trying to access the attributes, it's still unavail. 
// 73. -DONE- If entites have no shield, the have NaN EHP
// 74. -TEST_ Effective DPS calc is wrong
// 75. Use behaviorShieldBoosterAmount (+ equivalent for armor) to check if we shoud split drones, certain entities have high repair
// 76. We weant to get all the typeIds for a specific group. Especially for https://everef.net/groups/1982, with that we can display a datagrid with all their numbers. (EHP, Repairamount/s, Neut GJ/s, DPS for all 4 types, ...)
// 77. Track when the last wreck is being tractored and be there ready to loot
// 78. Better speed cloud handling / avoidance
// 79. Fix the drone return logic if they are within a speed cloud. This is wasting a lot of time currently.
// 80. We need to say away from devoted knights (or just create a general method to avoid high dps ships)
// 81. Some bubbles do not have their colliders added, so the code can't detect them. We need to add a workaround for this.
// 82. Check if we can use the entID for collider entitiy chaches, instead of using the combination of pos + id (string)
// 83. Check for offline modules before jumping into death - DONE
// 84. A* Path error might be related to non traversable colliders within abyss bubbles (maybe only within overlapping bubbles)
// |--------------------------------------------- END ->

// Price lookups:
// - T6 filaments: https://evepraisal.com/a/157hmt
// - T5 filaments: https://evepraisal.com/a/157hnw

/// [ - How the usual cycle of the bot looks like
///   01. Start in homestation 
///   02. Arm: if out of items, buy with the cloak hauler
///   03. ...
///   04. ...
/// ]
/// 

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
        private double _propModMinCapPerc = 35;
        private double _moduleMinCapPerc = 2;
        private double _shieldBoosterMinCapPerc = 5;
        private double _shieldBoosterEnableShieldPerc = 101;
        private double _drugsBoostersMinShieldPerc = 98;
        private DateTime _nextStack;
        private bool PlayNotificationSounds => ESCache.Instance.EveAccount.CS.AbyssalMainSetting.SoundNotifications;
        public bool DroneDebugState { get; set; } = false;

        private static Dictionary<int, double> _droneRecoverShieldPerc = new Dictionary<int, double>()
        {
            [50] = 35,
            [25] = 35,
            [10] = 40,
            [5] = 50,
            [0] = 50,
        };

        private static Dictionary<int, double> _droneLaunchShieldPerc = new Dictionary<int, double>()
        {
            [50] = 60,
            [25] = 60,
            [10] = 65,
            [5] = 70,
            [0] = 70,
        };

        private double?
            _maxDroneRange =
                82000;

        private double? _maxVelocity = 625; // ...
        private double? _maxGigaJoulePerSecondTank = 37;
        private DateTime _lastLoot;
        private static int _ammoTypeId = 21906; // Republic Fleet Fusion S

        private static int _naniteRepairPasteTypeId = 28668; // Nanite Repair Paste

        /// <summary>
        /// </summary>
        private List<(int, int)> _shipsCargoBayList = null;

        /// <summary>
        /// tuple(typeid, amount)
        /// </summary>
        private List<(int, int)>
            _boosterList = new List<(int, int)>
                { (46002, 2), (9950, 2) }; // 9950 = Standard Blue Pill Booster, 46002 = Agency 'Hardshell' TB5 Dose II

        private int _shieldBoosterOverheatDuration = 10;
        private int _shieldHardenerOverheatDuration = 20;
        private int _propmodOverheatDuration = 15;

        private int _overheatRandMaxOffset = 6;

        private int _homeSystemId = -1;

        private int _shopStationID= 60003760; // 60003760 Jita 4-4 Caldari Navy Assembly Plant

        private int _weaponMaxRange = 20000;

        private int _trashItemAttempts = 0;

        private int _itemHangarTrashItemsThreshold = 600;

        /// <summary>
        /// Item1: typeId <br />
        /// Item2: amount <br />
        /// Item3: size <br />
        /// Item4: mutated <br />
        /// </summary>
        private static List<(int, int, DroneSize, bool)> _droneBayItemList = new List<(int, int, DroneSize, bool)> { };

        private static int _mtuTypeId = -1; // 'Magpie' Mobile Tractor Unit
        private int _shipTypeId = -1;

        private string
            _homeStationBookmarkName =
                "station"; // this is the station we are starting with the abyss ship <-> this also means, that the safespot of the abyss starting point should be in that system to prevent gate jumps

        private string
            _repairLocationBookmarkName =
                "repair"; // repair station bm, this can be the same as the homestation if it has a repair facility

        private string _filamentSpotBookmarkName = "abyss"; // the spot where we open the filament

        private string _surveyDumpBookmarkName = "surveyDump"; // bookmark of the station where we are dumping the survey data


        private DirectWorldPosition _activeShipPos =>
            ESCache.Instance.DirectEve.ActiveShip.Entity.DirectAbsolutePosition;

        private DateTime? _startedToRecallDronesWhileNoTargetsLeft = null;

        private static int _filamentTypeId = 47764; // gamma t1

        private bool _isInHomeSystem => Framework.Session.SolarSystemId == _homeSystemId;

        private bool _mtuAlreadyDroppedDuringThisStage;

        private DateTime _abyssalControllerStarted = DateTime.MinValue;

        private int _kikimoraTankThreshold;

        private int _damavikTankThreshold;

        private int _marshalTankThreshold;

        private int _bcTankthreshold;

        public AbyssalController()
        {
            _abyssalControllerStarted = DateTime.UtcNow;
            DirectSession.OnSessionReadyEvent += OnSessionReadyHandler;

            _keepAtRangeDistance = _keepAtRangeDistances[Rnd.Next(_keepAtRangeDistances.Count)];
            _enemyOrbitDistance = _enemyOrbitDistances[Rnd.Next(_enemyOrbitDistances.Count)];
            _gateMTUOrbitDistance = _gateMTUOrbitDistances[Rnd.Next(_gateMTUOrbitDistances.Count)];
            _wreckOrbitDistance = _wreckOrbitDistances[Rnd.Next(_wreckOrbitDistances.Count)];
            _itemHangarTrashItemsThreshold = Rnd.Next(550, 700);

            Log($"AbyssalController started at {_abyssalControllerStarted}. Selected the following random values for this session:");
            Log($"_keepAtRangeDistance: [{_keepAtRangeDistance}] _enemyOrbitDistance: [{_enemyOrbitDistance}] _gateMTUOrbitDistance: [{_gateMTUOrbitDistance}]");
            OnSessionReadyHandler(null, null);
        }

        internal bool AreAllDronesAttacking => allDronesInSpace.All(d => d.DroneState == 1) && allDronesInSpace.Any();

        internal bool AreWeDockedInHomeSystem()
        {
            var hbm = ESCache.Instance.DirectEve.Bookmarks.OrderByDescending(e => e.IsInCurrentSystem).OrderByDescending(e => e.LocationId == _homeSystemId).FirstOrDefault(b => b.Title == _homeStationBookmarkName);
            if (hbm != null)
            {
                return hbm.DockedAtBookmark();
            }
            return false;
        }

        private bool AreAllTargetsInASpeedCloud => TargetsOnGrid.Where(e => e.GroupId != 2009).Any() && TargetsOnGrid.Where(e => e.GroupId != 2009).ToList().All(e => e.IsInSpeedCloud);

        private bool AreAllFrigatesInASpeedCloud => TargetsOnGrid.Where(e => e.GroupId != 2009 && e.IsNPCFrigate).Any() && TargetsOnGrid.Where(e => e.GroupId != 2009 && e.IsNPCFrigate).ToList().All(e => e.IsInSpeedCloud);

        private bool AreWeCurrentlyAttackingAFrigate => (_groupTarget1OrSingleTarget?.IsNPCFrigate ?? false) || (_groupTarget2?.IsNPCFrigate ?? false);
        /// <summary>
        /// Tachyon Cloud (white): +300% Velocity (x4.0 velocity), -50% Inertia Modifier. 
        /// </summary>
        //private bool AreWeInASpeedCloud => Framework.Me.IsHudStatusEffectActive(HudStatusEffect.aoeCausticCloud);
        private bool AreWeInASpeedCloud => DirectEntity.AnyIntersectionAtThisPosition(ESCache.Instance.DirectEve.ActiveShip.Entity.DirectAbsolutePosition, false, true, true, true, true, true, false).Any(e => e.IsTachCloud);

        /// <summary>
        /// Bioluminescence Cloud (light blue): +300% Signature Radius (4.0x signature radius multiplier)
        /// </summary>
        //private bool AreWeInABioCloud => Framework.Me.IsHudStatusEffectActive(HudStatusEffect.aoeBioluminescenceCloud);
        private bool AreWeInABioCloud => DirectEntity.AnyIntersectionAtThisPosition(ESCache.Instance.DirectEve.ActiveShip.Entity.DirectAbsolutePosition, false, true, true, true, true, false, true).Any(e => e.IsBioCloud);
        /// <summary>
        /// //Filament Cloud(orange): Penalty to Shield Booster boosting(-40%)
        /// </summary>
        //private bool AreWeInAFilamentCloud => Framework.Me.IsHudStatusEffectActive(HudStatusEffect.aoeFilamentCloud);
        private bool AreWeInAFilamentCloud => DirectEntity.AnyIntersectionAtThisPosition(ESCache.Instance.DirectEve.ActiveShip.Entity.DirectAbsolutePosition, false, true, true, true, false, true, true).Any(e => e.IsFilaCould);

        private bool AreWeInsideOfAnyCloud => AreWeInABioCloud || AreWeInAFilamentCloud || AreWeInASpeedCloud;

        private int _filaStackSize => _activeShip.IsFrigate ? 3 : _activeShip.IsDestroyer ? 2 : 1;

        private bool IsActiveShipFrigateOrDestroyer => _activeShip.IsFrigate || _activeShip.IsDestroyer;

        private DirectActiveShip _activeShip => Framework.ActiveShip;

        public override void Dispose()
        {
            DirectEve.Log("-- Removed OnSessionReadyHandler");
            DirectSession.OnSessionReadyEvent -= OnSessionReadyHandler;
        }

        internal double GetSecondsToKillWithActiveDrones()
        {
            Dictionary<DirectDamageType, float> dict = new Dictionary<DirectDamageType, float>();
            foreach (var drone in allDronesInSpace.Select(d => new AbyssalDrone(d)).ToList())
            {
                foreach (var kv in drone.GetInvType?.GetDroneDPS())
                {
                    if (dict.ContainsKey(kv.Key))
                    {
                        dict[kv.Key] += kv.Value;
                    }
                    else
                    {
                        dict.Add(kv.Key, kv.Value);
                    }
                }
            }
            var effDPs = 0d;
            var secondsToKillActiveDrones = TargetsOnGridWithoutLootTargets.Sum(e => e.GetSecondsToKill(dict, out effDPs));
            return secondsToKillActiveDrones;
        }

        private void LoadSettings()
        {
            int GetFilamentTypeId()
            {
                switch (ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.FilamentType)
                {
                    case AbyssFilamentType.GammaT0:
                        return 56136;
                    case AbyssFilamentType.GammaT1:
                        return 47764;
                    case AbyssFilamentType.GammaT2:
                        return 47900;
                    case AbyssFilamentType.GammaT3:
                        return 47901;
                    case AbyssFilamentType.GammaT4:
                        return 47902;
                    case AbyssFilamentType.GammaT5:
                        return 47903;
                    case AbyssFilamentType.GammaT6:
                        return 56143;
                    case AbyssFilamentType.DarkT0:
                        return 56132;
                    case AbyssFilamentType.DarkT1:
                        return 47762;
                    case AbyssFilamentType.DarkT2:
                        return 47892;
                    case AbyssFilamentType.DarkT3:
                        return 47893;
                    case AbyssFilamentType.DarkT4:
                        return 47894;
                    case AbyssFilamentType.DarkT5:
                        return 47895;
                    case AbyssFilamentType.DarkT6:
                        return 56140;
                    case AbyssFilamentType.FirestormT0:
                        return 56134;
                    case AbyssFilamentType.FirestormT1:
                        return 47763;
                    case AbyssFilamentType.FirestormT2:
                        return 47896;
                    case AbyssFilamentType.FirestormT3:
                        return 47897;
                    case AbyssFilamentType.FirestormT4:
                        return 47898;
                    case AbyssFilamentType.FirestormT5:
                        return 47899;
                    case AbyssFilamentType.FirestormT6:
                        return 56142;
                    case AbyssFilamentType.ExoticT0:
                        return 56133;
                    case AbyssFilamentType.ExoticT1:
                        return 47761;
                    case AbyssFilamentType.ExoticT2:
                        return 47888;
                    case AbyssFilamentType.ExoticT3:
                        return 47889;
                    case AbyssFilamentType.ExoticT4:
                        return 47890;
                    case AbyssFilamentType.ExoticT5:
                        return 47891;
                    case AbyssFilamentType.ExoticT6:
                        return 56141;
                    case AbyssFilamentType.ElectricalT0:
                        return 56131;
                    case AbyssFilamentType.ElectricalT1:
                        return 47765;
                    case AbyssFilamentType.ElectricalT2:
                        return 47904;
                    case AbyssFilamentType.ElectricalT3:
                        return 47905;
                    case AbyssFilamentType.ElectricalT4:
                        return 47906;
                    case AbyssFilamentType.ElectricalT5:
                        return 47907;
                    case AbyssFilamentType.ElectricalT6:
                        return 56139;
                }
                return 0;
            }

            int GetMTUTypeId()
            {
                switch (ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.MTUType)
                {
                    case MTUType.Standard:
                        return 33475;
                    case MTUType.Packrat:
                        return 33700;
                    case MTUType.Magpie:
                        return 33702;
                }

                return 0;
            }

            _filamentTypeId = GetFilamentTypeId();
            _shipTypeId = ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.ShipType == AbyssShipType.Ishtar ? 12005
                : ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.ShipType == AbyssShipType.Vexor ? 626
                : ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.ShipType == AbyssShipType.Gila ? 17715
                : ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.ShipType == AbyssShipType.Algos ? 32872
                : ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.ShipType == AbyssShipType.Worm ? 17930
                : ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.ShipType == AbyssShipType.Tristan ? 593
                : ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.ShipType == AbyssShipType.MaulusN ? 37456
                : ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.ShipType == AbyssShipType.Cerberus ? 11993
                : -1;

            _homeSystemId = ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.HomeSystemId;

            _maxVelocity = ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.MaxSpeedWithPropMod;
            _maxDroneRange = ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.MaxDroneRange;
            _maxGigaJoulePerSecondTank = ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.GigajoulePerSecExcess;
            _ammoTypeId = ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.AmmoTypeId;
            _mtuTypeId = GetMTUTypeId();
            _weaponMaxRange = ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.WeaponMaxRange;
            _shopStationID = ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.shopStationID;
            _homeStationBookmarkName = ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.HomeStationBookmarkName;
            _repairLocationBookmarkName = ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.RepairStationBookmarkName;
            _filamentSpotBookmarkName = ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.AbyssalBookmarkName;
            _surveyDumpBookmarkName = ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.SurveyDumpStationBookmarkName;
            _bcTankthreshold = ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.BCTankthreshold;
            _kikimoraTankThreshold = ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.KikimoraTankThreshold;
            _damavikTankThreshold = ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.DamavikTankThreshold;
            _marshalTankThreshold = ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.MarshalTankThreshold;


            _boosterList = new List<(int, int)>();
            foreach (var booster in ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.Boosters)
            {
                _boosterList.Add((booster.TypeId, booster.Amount));
            }

            _droneBayItemList = new List<(int, int, DroneSize, bool)>();
            foreach (var drone in ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.DroneBayItems)
            {
                _droneBayItemList.Add((drone.TypeId, drone.Amount, drone.Type, drone.Mutated));
            }

            var attr =
                $"_filementTypeId {_filamentTypeId} _shipTypeId {_shipTypeId} _homeSystemId {_homeSystemId} _maxVelocity {_maxVelocity} _maxDroneRange {_maxDroneRange} _maxGigaJoulePerSecondTank {_maxGigaJoulePerSecondTank} _ammoTypeId{_ammoTypeId} _mtuTypeId {_mtuTypeId} _weaponMaxRange {_weaponMaxRange}";

            var droneLog = String.Join(" - ", _droneBayItemList.Select(a => $"[{a.Item1}, {a.Item2}, {a.Item3}, {a.Item4}], "));
            var boosterLog = String.Join(" - ", _boosterList.Select(a => $"[{a.Item1}, {a.Item2}]"));
            Log($"DroneLog [{droneLog}]");
            Log($"BoosterLog [{boosterLog}]");
            Log($"Settings [{attr}]");
        }

        private void OnSessionReadyHandler(object source, EventArgs args)
        {
            LoadSettings();

            DirectEve.Log("OnSessionReadyHandler proc.");
            _mtuAlreadyDroppedDuringThisStage = false;
            _startedToRecallDronesWhileNoTargetsLeft = null;
            _currentStageMaximumEhp = 0;
            _currentStageCurrentEhp = 0;
            _droneEngageCount = 0;
            _safeSpotNearGate = null;
            _safeSpotNearGateChecked = false;
            _safeSpotNeargGateResets = 0;
            _moveToOverride = null;
            _droneRecallsStage = 0;
            _mtuScoopAttempts = 0;
            _sessionChangeIdleCheck = false;
            _lastDroneInOptimal = null;
            _dronesInOptimalStage = 0;
            _attemptsToJumpFrigateDestroyerAbyss = 0;
            _moveDirection = MoveDirection.None;
            _enemiesWereInOptimal = false;
            _droneRecallTimers = new Dictionary<long, DateTime>();
            _moveBackwardsDirection = null;
            _printDroneEstimatedKillTime = false;
            _trashItemAttempts = 0;

            _shipsCargoBayList = new List<(int, int)>
            {
                (_naniteRepairPasteTypeId, Rnd.Next(250, 265)),
                //(_mtuTypeId, 1),
                (_filamentTypeId, Rnd.Next(5 * _filaStackSize, 8 * _filaStackSize)),
                (_ammoTypeId, IsActiveShipFrigateOrDestroyer? Rnd.Next(5999, 6999)/2 : Rnd.Next(5999, 6999))
            }.Concat(_boosterList).ToList();

            if (!ESCache.Instance.EveAccount.ClientSetting.AMS.DoNotUseMTU)
            {
                _shipsCargoBayList.Add((_mtuTypeId, 1));
            }

            if (ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.DisableOverheat)
            {
                _shipsCargoBayList.RemoveAll(a => a.Item1 == _naniteRepairPasteTypeId);
            }

            _limboDeployingAbyssalDrones = new List<AbyssalDrone>();
            _recentlyDeployedDronesTimer = new Dictionary<long, DateTime>();
            _limboDeployingDronesTimer = new Dictionary<long, DateTime>();
            _alreadyLootedItemIds = new HashSet<long>();
            _alreadyLootedItems = new List<string>();
            _activationErrorTickCount = 0;
            _droneRecallsDueEnemiesBeingInASpeedCloud = 0;
            _nextDroneRecallDueEnemiesBeingInASpeedCloud = DateTime.UtcNow.AddSeconds(Rnd.Next(35, 45));
            _skipWaitOrca = false;
            _forceOverheatPVP = false;
            _leftInvulnAfterAbyssState = false;
            _minSecondsToLaunchDronesAfterSessionChange = Rnd.Next(9, 10);
            _abandoningDrones = false;
            _droneDPSUpdate = false;
        }

        private int _minSecondsToLaunchDronesAfterSessionChange = 8;

        private int _neutsOnGridCount => TargetsOnGrid.Count(e => e.IsNeutingEntity);

        private int _marshalsOnGridCount => TargetsOnGrid.Count(e => e.IsAbyssalMarshal);

        /// <summary>
        /// Min (_maxDroneRange, _maxTargetRange)
        /// </summary>
        private double _maxRange => Math.Min(_maxDroneRange ?? _maxTargetRange, _maxTargetRange);


        private List<DirectEntity> _targetsOngrid = null;

        internal List<DirectEntity> TargetsOnGrid
        {
            get
            {
                if (!DirectEve.HasFrameChanged() && _targetsOngrid != null)
                    return _targetsOngrid;

                _targetsOngrid = DirectEve.Entities
                    .Where(e => e.IsNPCByBracketType && e.BracketType != BracketType.NPC_Drone ||
                                IsEntityWeWantToLoot(e)).OrderBy(e => e.AbyssalTargetPriority).ToList();
                return _targetsOngrid;
            }
        }


        internal bool AnyRemoteRepairNonFrigOnGrid => TargetsOnGrid.Any(e => e.IsRemoteRepairEntity && !e.IsNPCFrigate);

        internal bool AnyRemoteRepairOngrid => TargetsOnGrid.Any(e => e.IsRemoteRepairEntity);
        internal bool IsCloseToSafeSpotNearGate => SafeSpotNearGate != null && SafeSpotNearGate.GetDistance(_activeShipPos) < 6500;

        internal bool IsInLastStage => CurrentAbyssalStage == AbyssalStage.Stage3;

        internal List<DirectEntity> TargetsOnGridWithoutLootTargets =>
            TargetsOnGrid.Where(e => e.GroupId != 2009).ToList();

        internal bool _printDroneEstimatedKillTime;


        private List<string> _kitedEntitiesAsFrig = new List<string>() { "kikimora", "cynabal", "dramiel", "tessera", "devoted knight", "devoted hunter" };

        private bool AnyEntityOnGridToBeKitedAsFrigSizedShip
        {
            get
            {
                if (!IsActiveShipFrigateOrDestroyer)
                    return false;

                foreach (var name in _kitedEntitiesAsFrig)
                {
                    if (TargetsOnGrid.Any(e => e.TypeName.ToLower().Contains(name)))
                        return true;
                }
                return false;
            }
        }

        private List<DirectEntity> EntitiesToBeKitedAsFrig =>
            TargetsOnGrid.Where(e => _kitedEntitiesAsFrig.Any(x => e.TypeName.ToLower() == x)).ToList();

        private int MTUSpeed
        {
            get
            {
                switch (ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.MTUType)
                {
                    case MTUType.Standard:
                        return 1000;
                    case MTUType.Packrat:
                        return 1250;
                    case MTUType.Magpie:
                        return 1500;
                }
                return 1250;
            }
        }

        internal double _secondsNeededToRetrieveWrecks =>
            (TargetsOnGrid.Where(e => IsEntityWeWantToLoot(e)).Concat(_wrecks.Where(e => !e.IsEmpty))
                .Sum(e => e.Distance) / MTUSpeed) + TargetsOnGrid.Where(e => IsEntityWeWantToLoot(e))
                .Concat(_wrecks.Where(e => !e.IsEmpty)).Count() * 8;

        /// <summary>
        /// Targets on grid count without loot targets
        /// </summary>
        internal int _targetOnGridCount => TargetsOnGridWithoutLootTargets.Count();

        internal bool _majorityOnNPCsAreAgressingCurrentShip => TargetsOnGridWithoutLootTargets.Any() &&
            TargetsOnGridWithoutLootTargets.Count(e => e.IsTargetedBy && !e.IsRemoteRepairEntity) >
            TargetsOnGridWithoutLootTargets.Where(e => !e.IsRemoteRepairEntity).Count() * 0.51;

        internal int DronesInOptimalCount()
        {
            var drones = allDronesInSpace;
            var r = 0;
            foreach (var drone in drones)
            {
                if (drone.FollowId <= 0)
                    continue;

                var targetId = drone.FollowId;

                var target = TargetsOnGrid.FirstOrDefault(e => e.Id == targetId);

                if (target == null)
                    continue;

                if (drone.IsInOptimalRangeTo(target))
                    r++;
            }

            return r;
        }

        internal void PrintDroneEstimatedKillTimePerStage()
        {
            if (_printDroneEstimatedKillTime)
                return;

            if (!allDronesInSpace.Any())
                return;

            if (_abyssStatEntry == null)
                return;

            switch (CurrentAbyssalStage)
            {
                case AbyssalStage.Stage1:

                    if (String.IsNullOrEmpty(_abyssStatEntry.Room1Dump))
                        return;
                    _printDroneEstimatedKillTime = true;
                    break;
                case AbyssalStage.Stage2:

                    if (String.IsNullOrEmpty(_abyssStatEntry.Room1Dump))
                        return;
                    _printDroneEstimatedKillTime = true;
                    break;
                case AbyssalStage.Stage3:

                    if (String.IsNullOrEmpty(_abyssStatEntry.Room1Dump))
                        return;
                    _printDroneEstimatedKillTime = true;
                    break;
            }

            if (_printDroneEstimatedKillTime)
            {
                Dictionary<DirectDamageType, float> dict = new Dictionary<DirectDamageType, float>();

                foreach (var drone in allDronesInSpace)
                {
                    foreach (var kv in drone.GetDroneDPS())
                    {
                        if (dict.ContainsKey(kv.Key))
                        {
                            dict[kv.Key] += kv.Value;
                        }
                        else
                        {
                            dict.Add(kv.Key, kv.Value);
                        }
                    }
                }

                Log($"Estimated time to kill all targets in this room (with drones) [{DirectEntity.GetSecondsToKill(dict, TargetsOnGridWithoutLootTargets, out _)}] seconds.");
            }
        }

        private bool _doOnce;
        private bool _droneDPSUpdate = false;

        internal void DoOnceOnStartup()
        {

            if (!_doOnce)
            {

                _doOnce = true;
                if (DirectEve.Me.IsInAbyssalSpace())
                {
                    //_mtuAlreadyDroppedDuringThisStage = true;
                    Log(
                        $"Retrieving current stage from the eve account due a crash/restart. Stage [{ESCache.Instance.EveAccount.AbyssStage}]");
                    // if we start in abyss space we retrieve the stage id from the corresponding eve acc
                    switch (ESCache.Instance.EveAccount.AbyssStage)
                    {
                        case 2:
                            _attemptsToJumpMidgate++; // this is the only case we need
                            break;
                        default:
                            break;
                    }
                }
            }
        }

        private (bool, bool, bool) _overheatStates = (false, false, false);

        /// <summary>
        /// return (overheatPropmod, overheatHardener, overheatBooster);
        /// </summary>
        /// <returns></returns>
        /// 
        private (bool, bool, bool) _getOverHeatStates()
        {
            if (IsAnyPlayerAttacking || _forceOverheatPVP)
                return (true, true, true);

            if (ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.DisableOverheat)
                return (false, false, false);

            if (DirectEve.HasFrameChanged())
            {
                var analBurner = DirectEve.Modules.Where(e => e.GroupId == (int)Group.Afterburner);
                var shieldHardener = DirectEve.Modules.Where(e => e.GroupId == (int)Group.ShieldHardeners);
                var shieldBooster = DirectEve.Modules.Where(e => e.GroupId == (int)Group.ShieldBoosters);
                var modules = analBurner.Concat(shieldHardener).Concat(shieldBooster);
                Func<bool> overHeatPropMod = () =>
                {
                    var activeShip = ESCache.Instance.DirectEve.ActiveShip;

                    // not if we aren't in the abyss space
                    if (!ESCache.Instance.DirectEve.Me.IsInAbyssalSpace())
                        return false;

                    // not if we are close to the gate / mtu
                    if (
                        activeShip.Entity.DirectAbsolutePosition.GetDistance(_nextGate.DirectAbsolutePosition) < 10000
                        || _getMTUInSpace != null &&
                        activeShip.Entity.DirectAbsolutePosition.GetDistance(_getMTUInSpace.DirectAbsolutePosition) <
                        10000
                    )
                        return false;

                    // not if we are webbed
                    if (TargetsOnGrid.Any(e => e.IsWebbingMe))
                        return false;

                    // not if any other module is being repaired
                    if (modules.Any(e => e.IsBeingRepaired))
                        return false;

                    // Do not overheat while near the safe spot near gate
                    if (IsCloseToSafeSpotNearGate)
                        return false;

                    // if moving to the gate would take longer than killing
                    if (_moveDirection == MoveDirection.Gate && _secondsNeededToReachTheGate >= GetEstimatedStageRemainingTimeToClearGrid())
                        return true;

                    // if there are more than 2 neuts
                    if (_neutsOnGridCount > 2 && _moveDirection == MoveDirection.AwayFromEnemy)
                        return true;

                    // if there is more than 1 marshal
                    if (_marshalsOnGridCount > 1 && _moveDirection == MoveDirection.AwayFromEnemy)
                        return true;

                    return false;
                };

                var playerAttacking = IsAnyPlayerAttacking;

                bool overheatPropmod = overHeatPropMod();
                bool overheatHardener =
                    DirectActiveShip.PastTwentyPulsesShieldArmorStrucValues.Count(d => d.Item1 < 0.35) >= 2 ||
                    playerAttacking; // if we caputed at least 3 occasions of being below 35% shield in the past 20 pulses (10 seconds)
                bool overheatBooster =
                    DirectActiveShip.PastTwentyPulsesShieldArmorStrucValues.Count(d => d.Item1 < 0.35) >= 3 ||
                    playerAttacking; // same as above with 4 occasions

                _overheatStates = (overheatPropmod, overheatHardener, overheatBooster);
            }

            return _overheatStates;
        }

        private bool _anyOverheat =>
            _getOverHeatStates().Item1 || _getOverHeatStates().Item2 || _getOverHeatStates().Item3;

        private int _heatDamageMaximum = 75; // percent
        private double _medRackHeatEnableThreshold = 0.50d;
        private double _medRackHeatDisableThreshold = 0.60d;
        private int _passiveModuleDamagePercentMax = 75;
        private int _passiveModuleStartRepairPercent = 10;

        internal bool ManageOverheat()
        {
            if (!ESCache.Instance.InSpace)
                return false;

            var analBurner = DirectEve.Modules.Where(e => e.GroupId == (int)Group.Afterburner);
            var shieldHardener = DirectEve.Modules.Where(e => e.GroupId == (int)Group.ShieldHardeners);
            var shieldBooster = DirectEve.Modules.Where(e => e.GroupId == (int)Group.ShieldBoosters);
            var passiveModules = DirectEve.Modules.Where(e => e.IsPassiveModule)
                .OrderByDescending(m => m.HeatDamagePercent); // passive modules
            var modules = analBurner.Concat(shieldHardener).Concat(shieldBooster);
            var passiveModuleHighestDamagePercent =
                passiveModules.Any() ? passiveModules.OrderByDescending(m => m.HeatDamagePercent).FirstOrDefault().HeatDamagePercent : 0;
            var passiveModuleThresholdExceeded = passiveModuleHighestDamagePercent > _passiveModuleDamagePercentMax;


            var overheatStates = _getOverHeatStates();
            bool overheatPropmod = overheatStates.Item1;
            bool overheatHardener = overheatStates.Item2;
            bool overheatBooster = overheatStates.Item3;
            bool anyOverHeat = _anyOverheat;

            if (passiveModuleHighestDamagePercent >= _passiveModuleStartRepairPercent && passiveModules.Any() &&
                !anyOverHeat)
            {
                if (DirectEve.Interval(3500, 4500))
                {
                    foreach (var mod in passiveModules)
                    {
                        //if (mod.GroupId != 61) // atm only repair cap battery passive modules
                        //    continue;

                        if (!mod.IsBeingRepaired && mod.HeatDamagePercent >= _passiveModuleStartRepairPercent)
                        {
                            // Do not repair in the last stage
                            if (IsInLastStage)
                                continue;

                            if (anyOverHeat || IsAnyOtherNonFleetPlayerOnGrid)
                                continue;

                            if (mod.Repair())
                            {
                                Log(
                                    $"Starting to repair a passive module. TypeName [{mod.TypeName}] Id [{mod.ItemId}]");
                                return true;
                            }
                        }
                    }
                }
            }

            if (anyOverHeat && passiveModules.Any(e => e.IsBeingRepaired))
            {
                foreach (var item in passiveModules.Where(e => e.IsBeingRepaired))
                {
                    if (item.CancelRepair())
                    {
                        Log($"Canceling to repair a passive module. TypeName [{item.TypeName}] Id [{item.ItemId}]");
                        item.CancelRepair();
                        return true;
                    }
                }
            }

            foreach (var item in modules)
            {
                if ((item.HeatDamagePercent > _heatDamageMaximum || passiveModuleThresholdExceeded || modules.Any(e => e.HeatDamagePercent > _heatDamageMaximum)) &&
                    item.IsOverloaded && item.ToggleOverload())
                {
                    Log(
                        $"Disabling overload on module [{item.TypeName}] because overload threshold was hit. Threshold [{_heatDamageMaximum}] Current [{item.HeatDamagePercent}]");
                    return true;
                }
            }

            // if a passive module is damaged too much, prevent any overheat operation
            if (passiveModuleThresholdExceeded)
            {
                if (DirectEve.Interval(30000))
                    Log($"Passive module damage threshold exceeded! Can't overheat anything.");
                return false;
            }

            var medRackHeatStatus = ESCache.Instance.ActiveShip.MedHeatRackState(); // medium rack heat state


            foreach (var item in modules)
            {
                if (item.IsOverloaded && item.HeatDamagePercent >= _heatDamageMaximum && item.ToggleOverload())
                {
                    Log(
                        $"Disabling overload on module [{item.TypeName}] because heatdamage is too high! HeatDamagePercent [{item.HeatDamagePercent}]");
                    return true;
                }
            }

            if (medRackHeatStatus <= _medRackHeatEnableThreshold)
            {
                // Only overheat if all modules are below 70% heat dmg perc
                if (modules.All(e => e.HeatDamagePercent <= _heatDamageMaximum))
                {
                    if (overheatPropmod)
                        foreach (var item in analBurner)
                        {
                            if (!item.IsOverloaded && item.HeatDamagePercent < _heatDamageMaximum && item.ToggleOverload())
                            {
                                Log($"Overheating module [{item.TypeName}] Heatdamage [{item.HeatDamagePercent}]");
                                _nextOverheatDisablePropMod = DateTime.UtcNow.AddSeconds(Rnd.Next(_propmodOverheatDuration,
                                    _propmodOverheatDuration + _overheatRandMaxOffset));
                                return true;
                            }
                        }

                    if (overheatHardener)
                        foreach (var item in shieldHardener)
                        {
                            if (!item.IsOverloaded && item.HeatDamagePercent < _heatDamageMaximum && item.ToggleOverload())
                            {
                                Log($"Overheating module [{item.TypeName}] Heatdamage [{item.HeatDamagePercent}]");
                                _nextOverheatDisableShieldHardner = DateTime.UtcNow.AddSeconds(
                                    Rnd.Next(_shieldHardenerOverheatDuration,
                                        _shieldHardenerOverheatDuration + _overheatRandMaxOffset));
                                return true;
                            }
                        }

                    if (overheatBooster)
                        foreach (var item in shieldBooster)
                        {
                            if (!item.IsOverloaded && item.HeatDamagePercent < _heatDamageMaximum && item.ToggleOverload())
                            {
                                _nextOverheatDisableShieldBooster = DateTime.UtcNow.AddSeconds(
                                    Rnd.Next(_shieldBoosterOverheatDuration,
                                        _shieldBoosterOverheatDuration + _overheatRandMaxOffset));
                                Log($"Overheating module [{item.TypeName}] Heatdamage [{item.HeatDamagePercent}]");
                                return true;
                            }
                        }
                }


                // disable overheat
                if (!overheatPropmod)
                    foreach (var item in analBurner)
                    {
                        // add date time checks
                        if (item.IsOverloaded && _nextOverheatDisablePropMod < DateTime.UtcNow &&
                            item.ToggleOverload())
                        {
                            Log($"Disabling overload on module [{item.TypeName}] because overloading case is false.");
                            return true;
                        }
                    }

                if (!overheatHardener)
                    foreach (var item in shieldHardener)
                    {
                        if (item.IsOverloaded && _nextOverheatDisableShieldHardner < DateTime.UtcNow &&
                            item.ToggleOverload())
                        {
                            Log($"Disabling overload on module [{item.TypeName}] because overloading case is false.");
                            return true;
                        }
                    }

                if (!overheatBooster)
                    foreach (var item in shieldBooster)
                    {
                        if (item.IsOverloaded && _nextOverheatDisableShieldBooster < DateTime.UtcNow && item.ToggleOverload())
                        {
                            Log($"Disabling overload on module [{item.TypeName}] because overloading case is false.");
                            return true;
                        }
                    }
            }
            else if (medRackHeatStatus >= _medRackHeatDisableThreshold)
            {
                // here we disable the overheat state
                foreach (var item in modules)
                {
                    if (item.IsOverloaded && item.ToggleOverload())
                    {
                        Log(
                            $"Disabling overload on module [{item.TypeName}] because rack overload threshold was hit. Threshold [{_medRackHeatDisableThreshold}] MedRackHeat [{medRackHeatStatus}]");
                        return true;
                    }
                }
            }

            return false;
        }

        private Dictionary<int, List<DateTime>> _boosterHistory = new Dictionary<int, List<DateTime>>();
        private bool _boosterFailedState = false;

        internal bool ManageDrugs()
        {
            if (ESCache.Instance.InSpace &&
                ESCache.Instance.ActiveShip.Entity.ShieldPct < _drugsBoostersMinShieldPerc / 100)
            {
                var shipsCargo = ESCache.Instance.DirectEve.GetShipsCargo();
                if (shipsCargo == null)
                    return false;

                foreach (var typeId in _boosterList.Select(b => b.Item1))
                {

                    // check if booster is already being used
                    if (ESCache.Instance.DirectEve.Me.Boosters.Any(b => b.TypeID == typeId))
                    {
                        if (DirectEve.Interval(10000, 12000))
                            Log($"Skipping to load boaster, already loaded. TypeId [{typeId}]");
                        continue;
                    }

                    var boosterItem = shipsCargo.Items.FirstOrDefault(i => i.TypeId == typeId);

                    if (boosterItem != null && boosterItem.GetBoosterConsumbableUntil() <= DateTime.UtcNow)
                    {
                        if (DirectEve.Interval(10000, 12000))
                            Log($"Skipping to load boaster, booster cant be consumed anymore. GetBoosterConsumbableUntil [{boosterItem.GetBoosterConsumbableUntil()}] TypeId [{typeId}] TypeName [{boosterItem.TypeName}]");
                        continue;
                    }

                    if (boosterItem != null && DirectEve.Interval(1500, 2500))
                    {
                        if (_boosterHistory.TryGetValue(typeId, out var b) && b.Count(e => e > DateTime.UtcNow.AddMinutes(-60)) >= 3)
                        {
                            Log($"Prevented to consume booster [{boosterItem.TypeName}] We already tried to consume it 3 times the past 60 minutes.");
                            _boosterFailedState = true;
                            return false;
                        }

                        _lastDrugUsage = DateTime.UtcNow;
                        boosterItem.ConsumeBooster();
                        Log($"Consuming booster [{boosterItem.TypeName}]");

                        if (_boosterHistory.TryGetValue(typeId, out var bo))
                        {
                            bo.Add(DateTime.UtcNow);
                        }
                        else
                        {
                            _boosterHistory[typeId] = new List<DateTime> { DateTime.UtcNow };
                        }

                        return true;
                    }
                }
            }

            return false;
        }

        internal bool ManagePropMod()
        {
            var capPerc = DirectEve.ActiveShip.CapacitorPercentage;
            if (DirectEve.Me.IsInAbyssalSpace())
            {
                foreach (var mod in DirectEve.Modules.Where(e => e.GroupId == (int)Group.Afterburner))
                {
                    var disableToRepair =
                        ((_getMTUInSpace != null && _getMTUInSpace.Distance <= 8000) ||
                         _nextGate != null && _nextGate.Distance <= 8000 || IsCloseToSafeSpotNearGate) && mod.HeatDamagePercent > 10 &&
                        TargetsOnGridWithoutLootTargets.Count < 4 && TargetsOnGridWithoutLootTargets.Count != 0 && !_getOverHeatStates().Item1;

                    if (mod.IsActive)
                    {
                        if (
                            ((capPerc < _propModMinCapPerc || disableToRepair) && !IsActiveShipFrigateOrDestroyer))
                        {
                            if (mod.IsInLimboState)
                                continue;

                            if (disableToRepair && !IsActiveShipFrigateOrDestroyer)
                                Log(
                                    $"Disabling propmod TypeName [{mod.TypeName}] to be able to repair the mod while being near the MTU.");

                            if (capPerc < _propModMinCapPerc)
                                Log(
                                    $"Disabling propmod TypeName [{mod.TypeName}] due cap too low. Perc [{capPerc}] Min required cap Perc [{_propModMinCapPerc}]");

                            if (DirectEve.Interval(800, 1500))
                            {
                                Log($"Disabling propmod TypeName [{mod.TypeName}].");
                                mod.Click();
                                return true;
                            }
                        }

                        continue;
                    }
                    // below means mod == inactive

                    if (mod.IsBeingRepaired && (!disableToRepair || _anyOverheat))
                    {
                        // cancel repair
                        if (mod.CancelRepair())
                        {
                            Log($"Canceling repair TypeName[{mod.TypeName}].");
                            return true;
                        }
                    }

                    if (mod.IsInLimboState)
                        continue;

                    if (!mod.IsBeingRepaired && disableToRepair && !_anyOverheat)
                    {
                        var shipsCargo = ESCache.Instance.DirectEve.GetShipsCargo();

                        if (shipsCargo == null)
                            continue;

                        if (!shipsCargo.Items.Any(i => i.TypeId == _naniteRepairPasteTypeId))
                        {
                            DirectEve.IntervalLog(4000, 4000, "No nanite repair paste found in cargo, can't repair.");
                            continue;
                        }

                        // Do not repair in the last stage
                        if (IsInLastStage)
                            continue;

                        // repair
                        if (mod.Repair())
                        {
                            Log($"Repairing TypeName[{mod.TypeName}].");
                            return true;
                        }
                    }

                    //var scrambled = DirectEve.Entities.Any(e => e.IsWarpScramblingMe);
                    //if (capPerc >= _propModMinCapPerc && DirectEve.Interval(800, 1500) && _nextGate.Distance > distToNextGate)
                    if (capPerc >= _propModMinCapPerc && DirectEve.Interval(800, 1500))
                    {
                        Log($"Activating Typename [{mod.TypeName}]");
                        mod.Click();
                        return true;
                    }
                }
            }

            return false;
        }

        internal bool NeedRepair(bool excludeModules = false)
        {

            if (ESCache.Instance.InDockableLocation)
                return false;

            var dronesNeedRepair = largeDronesInBay.Any() &&
                                   !largeDronesInBay.All(d =>
                                       d.GetDroneInBayDamageState().Value.Y >= 0.6f); // only on sisi
            //var dronesNeedRepair = !alldronesInBay.All(d => d.GetDroneInBayDamageState().Value.Y >= 1.0f); // enable on tranq
            var modulesNeedRepair = !excludeModules
                ? !ESCache.Instance.DirectEve.Modules.All(m => m.HeatDamagePercent == 0)
                : false;
            var shipNeedsRepair = ESCache.Instance.ActiveShip.ArmorPercentage < 100 ||
                                  ESCache.Instance.ActiveShip.StructurePercentage < 100;

            if (DirectEve.Interval(5000))
                Log($"Drones need repair [{dronesNeedRepair}] Modules need repair [{modulesNeedRepair}] Ship needs repair [{shipNeedsRepair}]");
            return dronesNeedRepair || modulesNeedRepair || shipNeedsRepair;
        }

        internal bool AreTheMajorityOfNPCsInOptimalOnGrid()
        {
            var targetsWithoutCaches = TargetsOnGrid.Where(e => e.GroupId != 2009).ToList();

            if (targetsWithoutCaches.Any())
            {
                var totalTargetsCount = targetsWithoutCaches.Count();
                var targetsWhichAreInOptimalCount = targetsWithoutCaches.Count(e => e.IsInOptimalRange || e.Distance < 28_000);
                if (DirectEve.Interval(3000, 5000))
                {
                    Log(
                        $"TargetsOnGridCount [{totalTargetsCount}] TargetsOnGridInOptimal [{targetsWhichAreInOptimalCount}]");
                }

                if (targetsWhichAreInOptimalCount >= totalTargetsCount * 0.51)
                {
                    return true;
                }
            }

            return false;
        }

        internal bool ManageTargetLocks()
        {

            if (!DirectEve.HasFrameChanged())
                return false;

            _lastHandleTarget = DateTime.UtcNow;

            if (TargetsOnGrid.Any())
            {
                if (ESCache.Instance.MaxLockedTargets == 0)
                {
                    Log($"We are jammed, targeting the jamming entities.");
                    var jammers = ESCache.Instance.Combat.TargetedBy.Where(t => t.IsJammingMe).ToList();
                    foreach (var jammer in jammers)
                    {
                        if (!jammer.IsTargeting && !jammer.IsTarget && jammer.Distance <= _maxRange &&
                            DirectEve.Interval(3500, 4500))
                        {
                            Log($"Targeting jammer [{jammer.Id}] TypeName [{jammer.TypeName}].");
                            jammer.LockTarget("");
                            return true;
                        }
                    }
                }

                if (DirectEve.Interval(15000, 20000))
                    Log($"We have targets to target.");


                // target npcs
                if (_currentLockedAndLockingTargets.Count() < _maximumLockedTargets)
                {
                    foreach (var target in TargetsOnGrid
                                 .Where(e => !e.IsTarget && !e.IsTargeting && e.Distance <= _maxRange)
                                 .OrderBy(e => e.AbyssalTargetPriority))
                    {
                        if (DirectEve.Interval(1))
                        {
                            Log(
                                $"Targeting Id [{target.Id}] TypeName [{target.TypeName}] TargetPriority [{target.AbyssalTargetPriority}]");
                            target.LockTarget();
                            return true;
                        }
                    }
                }


                // unlock targets being out of range
                if (_currentLockedTargets.Any())
                {
                    if (_currentLockedTargets.Any(e => e.Distance >= _maxRange))
                    {
                        var targetToUnlock =
                            _currentLockedTargets.FirstOrDefault(e => e.Distance > _maxRange);
                        if (DirectEve.Interval(2500, 3200))
                        {
                            Log(
                                $"Unlocking target due being out of range [{targetToUnlock.Id}] TypeName [{targetToUnlock.TypeName}]");
                            targetToUnlock.UnlockTarget();
                            return true;
                        }
                    }
                }

                // check if higher prio is present
                var highestTargeted = _currentLockedAndLockingTargets.OrderByDescending(e => e.AbyssalTargetPriority)
                    .FirstOrDefault();
                if (highestTargeted != null)
                {
                    // get the lowest on grid within _maxRange
                    var lowestOnGrid = TargetsOnGrid.Where(e => !e.IsTarget && !e.IsTargeting && e.Distance < _maxRange)
                        .OrderBy(e => e.AbyssalTargetPriority).FirstOrDefault();
                    if (lowestOnGrid != null)
                    {
                        // if lowest on grid < highest targeted, then unlock highest targeted
                        if (lowestOnGrid.AbyssalTargetPriority < highestTargeted.AbyssalTargetPriority)
                        {
                            if (DirectEve.Interval(800, 1200))
                            {
                                Log(
                                    $"Unlocking Id [{highestTargeted.Id}] TypeName [{highestTargeted.TypeName}] due higher priority is present. Priority [{highestTargeted.AbyssalTargetPriority}] will be replaced with [{lowestOnGrid.AbyssalTargetPriority}]");
                                highestTargeted.UnlockTarget();
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private double _lastArmorPerc = 100;
        private double _lastStructurePerc = 100;

        internal bool ManageModules()
        {
            if (DirectEve.Session.IsInDockableLocation)
                return false;

            if (ESCache.Instance.DirectEve.Me.IsInvuln)
            {
                if (DirectEve.Interval(2000))
                    Log($"Not managing modules during invul phases.");
                return false;
            }

            if (!DirectEve.Me.IsInAbyssalSpace() && ESCache.Instance.InSpace && NeedRepair() && !IsAnyOtherNonFleetPlayerOnGrid)
            {

                if (AreWeAtTheFilamentSpot)
                {
                    // we don't want to manage modules on the abyss spot if we need repair and no other player is near
                    if (DirectEve.Interval(3000))
                        Log($"We are at the filament spot and no other player is near, not managing modules.");
                    return false;
                }
            }

            var capPerc = DirectEve.ActiveShip.CapacitorPercentage;
            var currentShieldPct = DirectEve.ActiveShip.ShieldPercentage;
            var currentArmorPct = DirectEve.ActiveShip.ArmorPercentage;
            var currentStructurePct = DirectEve.ActiveShip.StructurePercentage;

            // Check if we received armor or structure damage since last capture
            var armorOrStrucDecreased = currentArmorPct < _lastArmorPerc || currentStructurePct < _lastStructurePerc;

            _lastArmorPerc = currentArmorPct;
            _lastStructurePerc = currentStructurePct;


            var anyTargets = DirectEve.Entities.Any(e =>
                e.IsNPCByBracketType && e.BracketType != BracketType.NPC_Drone && e.GroupId != 2009) || IsAnyOtherNonFleetPlayerOnGrid;

            var inAbyssSpace = ESCache.Instance.DirectEve.Me.IsInAbyssalSpace();

            // shield booster
            foreach (var mod in DirectEve.Modules.Where(e => e.GroupId == (int)Group.ShieldBoosters))
            {
                if (mod.IsActive)
                {
                    if (capPerc < 40 && currentShieldPct > 80 && !IsAnyOtherNonFleetPlayerOnGrid)
                    {
                        if (mod.IsInLimboState)
                            continue;

                        Log($"Disabling mod TypeName [{mod.TypeName}] due low cap perc and high shield perc.");
                        if (DirectEve.Interval(800, 1500))
                        {
                            mod.Click();
                            return true;
                        }
                    }

                    if (capPerc < _shieldBoosterMinCapPerc || !anyTargets && inAbyssSpace)
                    {
                        if (mod.IsInLimboState)
                            continue;

                        if (capPerc < _shieldBoosterMinCapPerc)
                            Log(
                                $"Disabling mod TypeName [{mod.TypeName}] due cap too low. Perc [{capPerc}] Min required cap Perc [{_moduleMinCapPerc}]");

                        if (DirectEve.Interval(800, 1500))
                        {
                            mod.Click();
                            return true;
                        }
                    }

                    continue;
                }

                if (DirectEve.Me.IsInAbyssalSpace() && anyTargets || _anyOverheat || !IsOurShipWithintheAbyssBounds())
                {
                    if (mod.IsBeingRepaired)
                    {
                        // cancel repair
                        if (mod.CancelRepair())
                        {
                            Log($"Canceling repair on TypeName[{mod.TypeName}] due enemies are on grid.");
                            return true;
                        }
                    }
                }

                if (DirectEve.Me.IsInAbyssalSpace() && !anyTargets && IsOurShipWithintheAbyssBounds() && !_anyOverheat)
                {
                    if (!mod.IsBeingRepaired && mod.HeatDamagePercent > 5)
                    {
                        var shipsCargo = ESCache.Instance.DirectEve.GetShipsCargo();

                        if (shipsCargo == null)
                            continue;

                        if (!shipsCargo.Items.Any(i => i.TypeId == _naniteRepairPasteTypeId))
                        {
                            DirectEve.IntervalLog(4000, 4000, "No nanite repair paste found in cargo, can't repair.");
                            continue;
                        }

                        // Do not repair in the last stage
                        if (IsInLastStage)
                            continue;

                        // repair
                        if (mod.Repair())
                        {
                            Log($"Repairing TypeName[{mod.TypeName}].");
                            return true;
                        }
                    }

                    continue;
                }

                if (mod.IsInLimboState)
                    continue;

                if (capPerc >= _shieldBoosterMinCapPerc && currentShieldPct <= _shieldBoosterEnableShieldPerc
                    && (anyTargets || (!anyTargets && currentShieldPct <= 99)) && DirectEve.Interval(800, 1500))
                {
                    Log($"Activating Typename [{mod.TypeName}]");
                    mod.Click();
                    return true;
                }
            }

            // multispectral shield hardener
            List<int> groups = new List<int>() // priority, cap levels etc (custom type?)
            {
                (int)Group.ShieldHardeners,
            };

            foreach (var groupId in groups)
            {
                foreach (var mod in DirectEve.Modules.Where(e => e.GroupId == (int)Group.ShieldHardeners))
                {
                    if (mod.IsActive)
                    {
                        if (capPerc < _moduleMinCapPerc || !anyTargets && inAbyssSpace)
                        {
                            if (mod.IsInLimboState)
                                continue;

                            if (capPerc < _moduleMinCapPerc)
                                Log(
                                    $"Disabling mod TypeName [{mod.TypeName}] due cap too low. Perc [{capPerc}] Min required cap Perc [{_moduleMinCapPerc}]");

                            if (DirectEve.Interval(800, 1500))
                            {
                                mod.Click();
                                return true;
                            }
                        }

                        continue;
                    }

                    if (DirectEve.Me.IsInAbyssalSpace() &&
                        (anyTargets || _anyOverheat || !IsOurShipWithintheAbyssBounds()))
                    {
                        if (mod.IsBeingRepaired)
                        {
                            // cancel repair
                            if (mod.CancelRepair())
                            {
                                Log($"Canceling repair on TypeName[{mod.TypeName}] due enemies are on grid.");
                                return true;
                            }
                        }
                    }

                    if (DirectEve.Me.IsInAbyssalSpace() && !anyTargets && IsOurShipWithintheAbyssBounds() &&
                        !_anyOverheat)
                    {
                        if (!mod.IsBeingRepaired && mod.HeatDamagePercent > 5)
                        {
                            var shipsCargo = ESCache.Instance.DirectEve.GetShipsCargo();

                            if (shipsCargo == null)
                                continue;

                            if (!shipsCargo.Items.Any(i => i.TypeId == _naniteRepairPasteTypeId))
                            {
                                DirectEve.IntervalLog(4000, 4000,
                                    "No nanite repair paste found in cargo, can't repair.");
                                continue;
                            }

                            // Do not repair in the last stage
                            if (IsInLastStage)
                                continue;

                            // repair
                            if (mod.Repair())
                            {
                                Log($"Repairing TypeName[{mod.TypeName}].");
                                return true;
                            }
                        }

                        continue;
                    }

                    if (mod.IsInLimboState)
                        continue;
                    if (capPerc >= _moduleMinCapPerc
                                                     && (anyTargets || (!anyTargets && currentShieldPct <= 99)) && DirectEve.Interval(800, 1500))
                    {
                        Log($"Activating Typename [{mod.TypeName}]");
                        mod.Click();
                        return true;
                    }
                }
            }

            if (ESCache.Instance.Modules.Any(e =>
                    e.GroupId == 60 && e.EffectId == 7012 && e.IsActivatable)) // assault damage control effect
            {
                var lowDefenseCondition = (capPerc < 15 && currentShieldPct < 30 && currentArmorPct < 50 || currentShieldPct < 30 &&
                                          currentArmorPct < 20) && armorOrStrucDecreased;
                if (ESCache.Instance.Entities.Any(e => e.IsPlayer && e.IsAttacking && currentArmorPct < 95) || lowDefenseCondition)
                {
                    var module =
                        ESCache.Instance.Modules.FirstOrDefault(e =>
                            e.GroupId == 60 && e.EffectId == 7012 && e.IsActivatable);

                    if (module.IsInLimboState)
                        return false;

                    if (module.IsDeactivating)
                        return false;

                    if (module.IsActive)
                        return false;

                    Log(
                        $"We are being attacked by a player or are really low at our defenses. Activating assault damage control.");

                    if (DirectEve.Interval(5000, 6000))
                    {
                        module.Click();
                        return true;
                    }
                }
            }

            return false;
        }

        internal bool ManageWeapons()
        {
            // we assume the weapon is grouped (we auto group during travel) -- so code below works only if we have 1 weapon!
            if (!DirectEve.Me.IsInAbyssalSpace())
                return false;

            if (!TargetsOnGrid.Any())
                return false;

            var weapons = ESCache.Instance.DirectEve.Weapons;

            if (weapons.Any(w => w.IsInLimboState))
                return false;

            if (!weapons.Any(_ => _.IsMaster))
                return false;


            var currentDroneTargets = allDronesInSpace.Where(e => e.FollowEntity != null).Select(e => e.FollowEntity)
                .Where(e => e.Distance < _weaponMaxRange && e.IsTarget && e.GroupId != 2009).OrderBy(e => e.Distance)
                .ToList();

            // if we have a current target
            if (weapons.Any(w => w.IsActive))
            {
                var targetId = weapons.FirstOrDefault().TargetId;
                if (_currentLockedTargets.Any(t => t.Id == targetId))
                {
                    var currentTarget = _currentLockedTargets.FirstOrDefault(t => t.Id == targetId);
                    // if target is not within distance anymore -> stop
                    if (currentTarget != null && (currentTarget.Distance > _weaponMaxRange ||
                                                  (currentDroneTargets.Any(e => e.Distance <= _weaponMaxRange) && // if we have any drone targets on grid below weapon range
                                                   !currentDroneTargets.Any(e => e.Id == currentTarget.Id)))) // if the current target is not a drone target
                    {
                        if (DirectEve.Interval(1000, 1500) && weapons.FirstOrDefault().Click())
                        {
                            Log($"Deactivating weapons due current target being out of range or a drone target went in weapon range.");
                            return true;
                        }
                    }
                }
            }

            if (weapons.Any(w => w.IsActive))
            {
                return false;
            }

            var shipsCargo = DirectEve.GetShipsCargo();
            if (shipsCargo == null)
                return false;

            // reload weapon
            if (weapons.Any(w => w.CurrentCharges == 0 || w.CurrentCharges > w.MaxCharges))
            {
                var weap = weapons.FirstOrDefault(w => w.CurrentCharges == 0 || w.CurrentCharges > w.MaxCharges);
                Log($"CurrentCharges [{weap.CurrentCharges}]");
                if (shipsCargo.Items.Any(e => e.TypeId == _ammoTypeId) && shipsCargo.Items.Any(e => e.Stacksize > 100))
                {
                    var ammo = shipsCargo.Items.Where(e => e.TypeId == _ammoTypeId).OrderByDescending(e => e.Stacksize)
                        .FirstOrDefault();
                    if (DirectEve.Interval(1800, 2500) && weapons.FirstOrDefault().ChangeAmmo(ammo))
                    {
                        Log($"Reloading weapon.");
                        return true;
                    }
                }
                else
                {
                    if (DirectEve.Interval(5000, 9000))
                        Log($"Not enough ammo left.");
                }
            }

            // here we attack the target
            var weapon = weapons.FirstOrDefault(w => w.CurrentCharges > 0);

            if (weapon == null)
                return false;

            // focus targets which are attacked by drones and are within weapon range

            if (currentDroneTargets.Any())
            {
                if (DirectEve.Interval(1000, 1500))
                {
                    weapon.Activate(currentDroneTargets.FirstOrDefault().Id);
                    return true;
                }
            }

            // if our current target is below maxRange we shoot that regardless
            // else we choose based on distance
            if (_currentLockedTargets.Any()) // this seems redundant
            {
                var target = GetSortedTargetList(_currentLockedTargets.Where(e => e.Distance < _weaponMaxRange))
                    .FirstOrDefault();
                if (target != null && target.Distance <= _weaponMaxRange)
                {
                    if (DirectEve.Interval(1000, 1500))
                    {
                        weapon.Activate(target.Id);
                        return true;
                    }
                }
                else
                {
                    // pick the closest one
                    var closestTarget = GetSortedTargetList(_currentLockedTargets).OrderBy(e => e.Distance)
                        .FirstOrDefault();
                    if (closestTarget != null && closestTarget.Distance <= _weaponMaxRange)
                    {
                        if (DirectEve.Interval(1000, 1500))
                        {
                            weapon.Activate(closestTarget.Id);
                            return true;
                        }
                    }
                }
            }

            return false;
        }


        public override bool EvaluateDependencies(ControllerManager cm)
        {
            return true;
        }
    }
}