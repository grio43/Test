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
using EVESharpCore.Controllers.Questor;
using EVESharpCore.Controllers.Questor.Core.States;
using EVESharpCore.Framework;
using EVESharpCore.Framework.Lookup;
using EVESharpCore.Traveller;
using SC::SharedComponents.EVE.ClientSettings;
using SC::SharedComponents.EVE.DatabaseSchemas;
using SC::SharedComponents.Extensions;
using SC::SharedComponents.SQLite;
using SC::SharedComponents.Utility;
using ServiceStack.OrmLite;

namespace EVESharpCore.Controllers.Abyssal
{
    enum DataSurveyDumpState
    {
        Start,
        ActivateTransportShip,
        EmptyTransportShip,
        LoadSurveysToCargo,
        TravelToDumpStation,
        Dump,
    }


    public partial class AbyssalController : AbyssalBaseController
    {

        private DataSurveyDumpState _dataSurveyDumpState;
        private TravelerDestination _travelerDestination;
        private int errorCnt;
        private bool _sellPerformed;
        private DateTime _sellPerformedDateTime;

        internal bool NeedToDumpDatabaseSurveys()
        {

            if (!ESCache.Instance.InDockableLocation)
                return false;

            if (ESCache.Instance.DirectEve.GetShipHangar() == null)
            {
                Log("Shiphangar is null.");
                return false;
            }

            if (ESCache.Instance.DirectEve.GetItemHangar() == null)
            {
                Log("ItemHangar is null.");
                return false;
            }

            // check if we are at the homestation, else false
            if (!AreWeDockedInHomeSystem())
                return false;

            var transportship = ESCache.Instance.DirectEve.GetShipHangar().Items
                .Where(i => i.IsSingleton && (i.GroupId == (int)Group.BlockadeRunner || i.GivenName.Equals(ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.TransportShipName))).ToList();

            // check if we are docked and a transport ship is available in the ship hangar, else false
            if (!transportship.Any())
            {
                Log("No transport ship found.");
                return false;
            }

            // check if survey value is > (2b + 100m for each day of month) to add some randomness

            var day = DateTime.Now.Day;
            var surveyAmount = ESCache.Instance.DirectEve.GetItemHangar().Items.Where(e => e.TypeId == 48121).Sum(e => (long)e.Stacksize);
            var surveyIskValue = surveyAmount * (long)100000;

            Log("Survey amount: " + surveyAmount + " Survey ISK value: " + surveyIskValue);

            if (surveyIskValue > ESCache.Instance.EveAccount.CS.AbyssalMainSetting.SurveyDumpThreshold * 1_000_000L + ESCache.Instance.EveAccount.CS.AbyssalMainSetting.SurveyDumpDailyAdditionValue * 1_000_000L * (long)day)
            {
                Log($"Survey ISK value is greater than [{ESCache.Instance.EveAccount.CS.AbyssalMainSetting.SurveyDumpThreshold * 1_000_000 + ESCache.Instance.EveAccount.CS.AbyssalMainSetting.SurveyDumpDailyAdditionValue * 1_000_000 * (long)day}]. Dumping.");
                _dataSurveyDumpState = DataSurveyDumpState.Start;
                return true;
            }

            return false;
        }

        internal void DumpDatabaseSurveys()
        {
            switch (_dataSurveyDumpState)
            {
                case DataSurveyDumpState.Start:
                    _dataSurveyDumpState = DataSurveyDumpState.ActivateTransportShip;
                    break;
                case DataSurveyDumpState.ActivateTransportShip:
                    ActivateTransportShip();
                    break;
                case DataSurveyDumpState.EmptyTransportShip:
                    EmptyTransportShip();
                    break;
                case DataSurveyDumpState.LoadSurveysToCargo:
                    LoadSurveysToCargo();
                    break;
                case DataSurveyDumpState.TravelToDumpStation:
                    TravelToDumpStation();
                    break;
                case DataSurveyDumpState.Dump:
                    Dump();
                    break;
            }
        }


        private void ActivateTransportShip()
        {

            if (!ESCache.Instance.InDockableLocation)
                return;

            if (ESCache.Instance.DirectEve.GetShipHangar() == null)
            {
                Log("Shiphangar is null.");
                return;
            }

            if (ESCache.Instance.DirectEve.GetItemHangar() == null)
            {
                Log("ItemHangar is null.");
                return;
            }

            var transportship = ESCache.Instance.DirectEve.GetShipHangar().Items
                .Where(i => i.IsSingleton && (i.GroupId == (int)Group.BlockadeRunner || i.GivenName.Equals(ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.TransportShipName))).ToList();
            
            if (ESCache.Instance.ActiveShip == null)
            {
                Log("Active ship is null.");
                return;
            }

            if (ESCache.Instance.ActiveShip.GroupId == (int)Group.BlockadeRunner || ESCache.Instance.ActiveShip.GivenName.Equals(ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.TransportShipName))
            {
                _dataSurveyDumpState = DataSurveyDumpState.EmptyTransportShip;
                Log("We are in a transport ship now.");
                return;
            }

            if (transportship.Any())
            {
                transportship.FirstOrDefault().ActivateShip();
                Log("Found a transport ship. Making it active.");
                LocalPulse = DateTime.UtcNow.AddSeconds(ESCache.Instance.Time.SwitchShipsDelay_seconds);
                return;
            }

        }
        private void EmptyTransportShip()
        {
            if (ESCache.Instance.ActiveShip == null)
            {
                Log("Active ship is null.");
                return;
            }

            if (ESCache.Instance.DirectEve.GetItemHangar() == null)
            {
                Log($"Itemhangar is null.");
                return;
            }

            if (ESCache.Instance.CurrentShipsCargo == null)
            {
                Log("Currentships cargo is null.");
                return;
            }

            if (ESCache.Instance.CurrentShipsCargo.Items.Any())
            {

                if (!ESCache.Instance.DirectEve.NoLockedItemsOrWaitAndClearLocks())
                    return;
                if (ESCache.Instance.DirectEve.GetItemHangar().Add(ESCache.Instance.CurrentShipsCargo.Items))
                {
                    Log($"Moving items into itemhangar.");
                    LocalPulse = UTCNowAddMilliseconds(3000, 3500);
                    return;
                }
            }
            else
            {
                _dataSurveyDumpState = DataSurveyDumpState.LoadSurveysToCargo;
            }

        }
        private void LoadSurveysToCargo()
        {

            if (ESCache.Instance.DirectEve.GetShipHangar() == null)
            {
                Log("Shiphangar is null.");
                return;
            }

            if (ESCache.Instance.DirectEve.GetItemHangar() == null)
            {
                Log("ItemHangar is null.");
                return;
            }

            if (ESCache.Instance.CurrentShipsCargo == null)
            {
                Log("Currentships cargo is null.");
                return;
            }

            var itemHangar = ESCache.Instance.DirectEve.GetItemHangar();

            if (itemHangar.Items.Any(e => e.TypeId == 48121))
            {

                if (!ESCache.Instance.DirectEve.NoLockedItemsOrWaitAndClearLocks())
                    return;
                if (ESCache.Instance.CurrentShipsCargo.Add(ESCache.Instance.DirectEve.GetItemHangar().Items.Where(e => e.TypeId == 48121)))
                {
                    Log($"Moving items into cargo.");
                    LocalPulse = UTCNowAddMilliseconds(3000, 3500);
                    return;
                }
            }
            else
            {
                var fbmx = ESCache.Instance.DirectEve.Bookmarks.OrderByDescending(e => e.IsInCurrentSystem)
                   .OrderByDescending(e => e.LocationId == _homeSystemId)
                   .FirstOrDefault(b => b.Title == _surveyDumpBookmarkName);

                if (fbmx == null)
                {
                    _state = AbyssalState.Error;
                    Log($"No bookmark found with name {_surveyDumpBookmarkName}.");
                    return;
                }

                _dataSurveyDumpState = DataSurveyDumpState.TravelToDumpStation;
                _travelerDestination = new BookmarkDestination(fbmx);
            }

        }

        private void TravelToDumpStation()
        {

            if (ESCache.Instance.DirectEve.Session.IsInSpace && ESCache.Instance.ActiveShip.Entity != null && ESCache.Instance.ActiveShip.Entity.IsWarpingByMode)
                return;

            if (ESCache.Instance.Traveler.Destination != _travelerDestination)
                ESCache.Instance.Traveler.Destination = _travelerDestination;

            ESCache.Instance.Traveler.ProcessState();

            if (ESCache.Instance.State.CurrentTravelerState == TravelerState.AtDestination)
            {
                Log($"Arrived at {_surveyDumpBookmarkName}. Starting to dump the surveys.");
                _dataSurveyDumpState = DataSurveyDumpState.Dump;

                ESCache.Instance.Traveler.Destination = null;
                return;
            }

            if (ESCache.Instance.State.CurrentTravelerState == TravelerState.Error)
            {
                if (ESCache.Instance.Traveler.Destination != null)
                    Log("Stopped traveling, traveller threw an error.");

                ESCache.Instance.Traveler.Destination = null;

                _state = AbyssalState.Error;
                _travelerDestination = null;
                return;
            }

        }
        private void Dump()
        {
            if (ESCache.Instance.DirectEve.GetItemHangar() == null)
            {
                Log("ItemHangar is null.");
                return;
            }


            if (ESCache.Instance.CurrentShipsCargo == null || ESCache.Instance.CurrentShipsCargo.Capacity == 0)
            {
                Log("Currentships cargo is null.");
                return;
            }

            var shipsCargo = ESCache.Instance.CurrentShipsCargo;

            if (shipsCargo.Items.Any(e => e.TypeId == 48121))
            {
                if (!ESCache.Instance.DirectEve.NoLockedItemsOrWaitAndClearLocks())
                    return;
                if (ESCache.Instance.DirectEve.GetItemHangar().Add(shipsCargo.Items.Where(e => e.TypeId == 48121).ToList()))
                {
                    Log($"Moving surveys into itemhangar.");
                    LocalPulse = UTCNowAddMilliseconds(2000, 3500);
                    return;
                }
                return;
            }

            var loot2dump = ESCache.Instance.UnloadLoot.LootItemsInItemHangar().Where(i => !i.IsSingleton && i.TypeId == 48121).ToList();

            if (loot2dump.Any())
            {

                var anyMultiSellWnd = ESCache.Instance.DirectEve.Windows.OfType<DirectMultiSellWindow>().Any();
                if (ESCache.Instance.SellError && anyMultiSellWnd)
                {
                    Log("Sell error, closing window and trying again.");
                    var sellWnd = ESCache.Instance.DirectEve.Windows.OfType<DirectMultiSellWindow>().FirstOrDefault();
                    sellWnd.Cancel();
                    errorCnt++;
                    LocalPulse = UTCNowAddMilliseconds(1500, 2000);
                    ESCache.Instance.SellError = false;

                    if (errorCnt > 20)
                    {
                        Log($"Too many errors while dumping loot, error.");
                        _state = AbyssalState.Error;
                        return;
                    }

                    return;
                }

                if (!anyMultiSellWnd)
                {
                    Log($"Opening MultiSellWindow with {loot2dump.Count} items.");
                    ESCache.Instance.DirectEve.MultiSell(loot2dump);
                    LocalPulse = UTCNowAddMilliseconds(1500, 2000);
                    _sellPerformed = false;
                    return;
                }
                else
                {
                    var sellWnd = ESCache.Instance.DirectEve.Windows.OfType<DirectMultiSellWindow>().FirstOrDefault();
                    if (sellWnd.AddingItemsThreadRunning)
                    {
                        Log($"Waiting for items to be added.");
                        LocalPulse = UTCNowAddMilliseconds(1500, 2000);
                        return;
                    }
                    else
                    {

                        if (sellWnd.GetDurationComboValue() != DurationComboValue.IMMEDIATE)
                        {
                            Log($"Setting duration combo value to {DurationComboValue.IMMEDIATE}.");
                            //Log($"Currently not working correctly, you need to select IMMEDIATE manually.");
                            sellWnd.SetDurationCombovalue(DurationComboValue.IMMEDIATE);
                            LocalPulse = UTCNowAddMilliseconds(3000, 4000);
                            return;
                        }

                        if (sellWnd.GetSellItems().All(i => !i.HasBid))
                        {
                            Log($"Only items without a bid are left. Done. " +
                                $"Changing to next state ({nameof(DumpLootControllerState.TravelToHomeStation)}).");
                            sellWnd.Cancel();
                            LocalPulse = UTCNowAddMilliseconds(1500, 2000);
                            _state = AbyssalState.TravelToHomeLocation;
                            return;
                        }

                        if (_sellPerformed)
                        {
                            var secondsSince =
                                Math.Abs((DateTime.UtcNow - _sellPerformedDateTime).TotalSeconds);
                            Log($"We just performed a sell [{secondsSince}] seconds ago. Waiting for timeout.");
                            LocalPulse = UTCNowAddMilliseconds(1000, 2000);

                            if (secondsSince <= 16) return;

                            Log($"Timeout reached. Canceling the trade and changing to next state.");
                            sellWnd.Cancel();
                            LocalPulse = UTCNowAddMilliseconds(1500, 2000);
                            _state = AbyssalState.TravelToHomeLocation;
                            return;
                        }


                        Log($"Items added. Performing trade.");
                        sellWnd.PerformTrade();
                        _sellPerformed = true;
                        _sellPerformedDateTime = DateTime.UtcNow;
                        LocalPulse = UTCNowAddMilliseconds(2000, 4000);
                        return;

                    }
                }
            }
            else
            {
                Log($"Sold all items. Changing state back to {AbyssalState.TravelToHomeLocation}.");
                _state = AbyssalState.TravelToHomeLocation;
                return;
            }

        }

    }
}
