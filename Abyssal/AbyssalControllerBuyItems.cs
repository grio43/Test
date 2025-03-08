//
// (c) duketwo 2022
//

extern alias SC;
using EVESharpCore.Cache;
using EVESharpCore.Framework;
using ServiceStack.OrmLite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EVESharpCore.Controllers.Questor.Core.States;
using EVESharpCore.Framework.Lookup;
using EVESharpCore.Traveller;


namespace EVESharpCore.Controllers.Abyssal
{

    enum BuyItemsState
    {
        CreateBuyList,
        ActivateTransportShip,
        EmptyTransportShip,
        TravelToJita,
        BuyItems,
        MoveItemsToCargo,
        TravelToHomeSystem,
        UnloadLoot,
    }

    public partial class AbyssalController : AbyssalBaseController
    {
        private BuyItemsState _buyItemsState;
        private Dictionary<int, int> _buyList = new Dictionary<int, int>();
        private Dictionary<int, int> _moveToCargoList = new Dictionary<int, int>();
        private int _maxAvgPriceMultiplier = 4;
        private int _maxBasePriceMultiplier = 16;
        private int _orderIterations = 0;

        internal void BuyItems()
        {
            switch (_buyItemsState)
            {
                case BuyItemsState.CreateBuyList:
                    CreateBuyList();
                    break;
                case BuyItemsState.ActivateTransportShip:
                    ActivateTransport();
                    break;
                case BuyItemsState.EmptyTransportShip:
                    EmptyTransport();
                    break;
                case BuyItemsState.TravelToJita:
                    TravelToJita();
                    break;
                case BuyItemsState.BuyItems:
                    BuyItemsInJita();
                    break;
                case BuyItemsState.MoveItemsToCargo:
                    MoveItemsToCargo();
                    break;
                case BuyItemsState.TravelToHomeSystem:
                    TravelToHomeSystem();
                    break;
                case BuyItemsState.UnloadLoot:
                    UnloadLoot();
                    break;
            }
        }

        private void ActivateTransport()
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

            var shipsCargo = ESCache.Instance.DirectEve.GetShipsCargo();

            if (shipsCargo == null)
                return;

            var transportship = ESCache.Instance.DirectEve.GetShipHangar().Items
                .Where(i => i.IsSingleton && (i.GroupId == (int)Group.BlockadeRunner || i.GivenName.Equals(ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.TransportShipName))).ToList();

            if (ESCache.Instance.ActiveShip == null)
            {
                Log("Active ship is null.");
                return;
            }

            if (ESCache.Instance.ActiveShip.GroupId == (int)Group.BlockadeRunner || ESCache.Instance.ActiveShip.GivenName.Equals(ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.TransportShipName))
            {
                _buyItemsState = BuyItemsState.EmptyTransportShip;
                Log("We are in a transport ship now.");

                // Check if the volume of all items in the buylist exceeds current ships cargo in a single linq expression
                while (_buyList.Sum(b => Framework.GetInvType(b.Key).Volume * b.Value) > shipsCargo.Capacity)
                {
                    if (DirectEve.Interval(1000))
                        Log($"The volume of all items in the buylist exceeds current ships cargo. Total volume of the BuyList [{_buyList.Sum(b => Framework.GetInvType(b.Key).Volume * b.Value)}] ShipsCargo Capacity [{shipsCargo.Capacity}]");

                    foreach (var buyItemKvp in _buyList.ToList())
                    {
                        var typeId = buyItemKvp.Key;
                        var amount = buyItemKvp.Value;
                        // get the invtype
                        var invType = Framework.GetInvType(typeId);
                        // get the total volume
                        var totalVolume = invType.Volume * amount;
                        // reduce the total volume by 5%
                        var reducedVolume = totalVolume * 0.95;
                        // calculate the remaining amount based on the reduced volume
                        var reducedAmount = (int)Math.Floor(reducedVolume / invType.Volume);
                        // set the reduced amount
                        _buyList[typeId] = reducedAmount;
                    }
                }

                Log($"---- Buylist ----");
                foreach (var item in _buyList.ToList())
                {
                    Log($"TypeName [{Framework.GetInvType(item.Key)?.TypeName ?? "Unknown TypeName"}]TypeId [{item.Key}] Amount [{item.Value}] Total Volume [{Framework.GetInvType(item.Key)?.Volume * item.Value}]");
                }
                Log($"---- End Buylist ----");

                // Make a copy of buylist and save it as movetolist
                _moveToCargoList = _buyList.ToDictionary(entry => entry.Key,
                    entry => entry.Value);

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

        private void EmptyTransport()
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
                    Log($"There were items in the transport ship, starting over again to check if the items in the transport ship were the items we wanted");
                    LocalPulse = UTCNowAddMilliseconds(3000, 3500);
                    _state = AbyssalState.Start;
                    _buyItemsState = BuyItemsState.CreateBuyList;
                    return;
                }
            }
            else
            {
                // check if the items we just moved were the items we wanted to buy
                if (!DoWeNeedToBuyItems)
                {
                    Log("The items that were in the transport ship apparently were the ones we wanted to buy. (Happens if we crash during return from jita)");
                    _state = AbyssalState.Start;
                    _buyItemsState = BuyItemsState.CreateBuyList;
                    return;
                }

                _buyItemsState = BuyItemsState.TravelToJita;
                _travelerDestination = null;
            }
        }

        /// <summary>
        /// ItemHangar, ShipsCargo and ShipHangar needs to be opened before calling
        /// </summary>
        internal bool DoWeNeedToBuyItems
        {
            get
            {
                if (!ESCache.Instance.InDockableLocation)
                {
                    Log("!ESCache.Instance.InDockableLocation");
                    return false;
                }

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

                if (ESCache.Instance.CurrentShipsCargo == null)
                {
                    Log("ESCache.Instance.CurrentShipsCargo is null.");
                    return false;
                }

                // check if we are at the homestation, else false
                if (!AreWeDockedInHomeSystem())
                {
                    Log("Not docked in Homestation.");
                    return false;
                }

                var transportship = ESCache.Instance.DirectEve.GetShipHangar().Items
                    .Where(i => i.IsSingleton && (i.GroupId == (int)Group.BlockadeRunner || i.GivenName.Equals(ESCache.Instance.EveAccount.ClientSetting.AbyssalMainSetting.TransportShipName))).ToList();

                // check if we are docked and a transport ship is available in the ship hangar, else false
                if (!transportship.Any())
                {
                    Log("No transport ship found.");
                    return false;
                }

                var buyList = BuildBuyList();

                // Log the buy list amount
                Log($"BuyLust amount [{buyList.Comparer}]");

                if (buyList.Any())
                    return true;

                return false;
            }
        }

        private Dictionary<int, int> BuildBuyList()
        {
            var randomCacheDurationTimeSpan = TimeSpan.FromHours(2);
            var buyList = new Dictionary<int, int>();
            foreach (var item in _shipsCargoBayList)
            {
                var typeId = item.Item1;
                var amount = item.Item2;

                // Boosters are handled below, skip them
                if (_boosterList.Any(e => e.Item1 == typeId))
                    continue;

                var minMultiplier = 2;
                var maxMultiplier = DirectEve.CachedRandom(3, 6, randomCacheDurationTimeSpan, localUniqueName: typeId.ToString());

                // We only need 1 MTU
                if (typeId == _mtuTypeId)
                    maxMultiplier = 1;

                if (typeId == _naniteRepairPasteTypeId)
                    maxMultiplier = DirectEve.CachedRandom(8, 13, randomCacheDurationTimeSpan, localUniqueName: typeId.ToString());

                if (typeId == _filamentTypeId)
                    maxMultiplier = DirectEve.CachedRandom(6, 9, randomCacheDurationTimeSpan, localUniqueName: typeId.ToString());

                if (typeId == _ammoTypeId)
                    maxMultiplier = DirectEve.CachedRandom(17, 24, randomCacheDurationTimeSpan, localUniqueName: typeId.ToString());

                if (minMultiplier > maxMultiplier)
                    minMultiplier = maxMultiplier;

                var countInHangarAndShipsBay = GetAmountofTypeIdLeftItemhangarAndCargo(typeId);

                if (countInHangarAndShipsBay < amount * minMultiplier)
                {
                    //Log($"We are missing items of type [{typeId}] TypeName [{Framework.GetInvType(typeId).TypeName}]. countInHangarAndShipsBay [{countInHangarAndShipsBay}] amount * minMultiplier [{amount * minMultiplier}] .Adding amount [{amount * maxMultiplier}] to the buy list.");
                    buyList.Add(typeId, amount * maxMultiplier);
                }
            }

            foreach (var item in _droneBayItemList)
            {
                var typeId = item.Item1;
                var amount = item.Item2;
                var minMultiplier = 1;
                var maxMultiplier = DirectEve.CachedRandom(2, 4, randomCacheDurationTimeSpan, localUniqueName: typeId.ToString());
                var mutated = item.Item4;

                // Skip mutated drones, as they are not avail on the market
                if (mutated)
                    continue;

                var countInHangarAndDroneBay = GetAmountofTypeIdLeftItemhangarAndDroneBay(typeId, item.Item4);

                if (countInHangarAndDroneBay < amount * minMultiplier)
                {
                    //Log($"We are missing items of type [{typeId}] TypeName [{Framework.GetInvType(typeId).TypeName}]. countInHangarAndDroneBay [{countInHangarAndDroneBay}] amount * minMultiplier [{amount * minMultiplier}] .Adding amount [{amount * maxMultiplier}] to the buy list.");
                    buyList.Add(typeId, amount * maxMultiplier);
                }
            }

            foreach (var item in _boosterList)
            {
                var typeId = item.Item1;
                var amount = item.Item2;
                var minMultiplier = 5;
                var maxMultiplier = DirectEve.CachedRandom(15, 19, randomCacheDurationTimeSpan, localUniqueName: typeId.ToString());
                var countInHangarAndShipsBay = GetAmountofTypeIdLeftItemhangarAndCargo(typeId);

                if (countInHangarAndShipsBay < amount * minMultiplier)
                {
                    //Log($"We are missing items of type [{typeId}] TypeName [{Framework.GetInvType(typeId).TypeName}]. countInHangarAndShipsBay [{countInHangarAndShipsBay}] amount * minMultiplier [{amount * minMultiplier}] .Adding amount [{amount * maxMultiplier}] to the buy list.");
                    buyList.Add(typeId, amount * maxMultiplier);
                }
            }

            return buyList;
        }

        private void CreateBuyList()
        {

            _buyList = new Dictionary<int, int>();
            _orderIterations = 0;
            _moveToCargoList = new Dictionary<int, int>();

            // Create a buylist based on _shipsCargoBayList, _droneBayItemList, _boosterList, filamentTypeId
            var hangar = ESCache.Instance.DirectEve.GetItemHangar();
            var shipsCargo = ESCache.Instance.DirectEve.GetShipsCargo();
            var droneBay = ESCache.Instance.DirectEve.GetShipsDroneBay();

            if (hangar == null || shipsCargo == null || droneBay == null)
                return;

            _buyList = BuildBuyList();

            // Make a copy of buylist and save it as movetolist
            _moveToCargoList = _buyList.ToDictionary(entry => entry.Key,
                                               entry => entry.Value);

            if (_buyList.Any())
            {
                Log($"---- Buylist ----");
                foreach (var item in _buyList.ToList())
                {
                    Log($"TypeName [{Framework.GetInvType(item.Key)?.TypeName ?? "Unknown TypeName"}]TypeId [{item.Key}] Amount [{item.Value}] Total Volume [{Framework.GetInvType(item.Key)?.Volume * item.Value}]");
                }
                Log($"---- End Buylist ----");

                _buyItemsState = BuyItemsState.ActivateTransportShip;
            }
            else
            {
                Log("Warning: The buylist was empty.");
                _state = AbyssalState.Start;
            }
        }

        private void TravelToJita()
        {
            if (ESCache.Instance.DirectEve.Session.IsInSpace && ESCache.Instance.ActiveShip.Entity != null && ESCache.Instance.ActiveShip.Entity.IsWarpingByMode)
                return;

            if (_travelerDestination == null || (_travelerDestination is DockableLocationDestination && (_travelerDestination as DockableLocationDestination).DockableLocationId != _shopStationID) || _travelerDestination.GetType() != typeof(DockableLocationDestination))
            {
                Log("Setting _travelerDestination to set station.");
                _travelerDestination = new DockableLocationDestination(_shopStationID);
            }

            if (ESCache.Instance.Traveler.Destination != _travelerDestination)
                ESCache.Instance.Traveler.Destination = _travelerDestination;

            ESCache.Instance.Traveler.ProcessState();

            if (ESCache.Instance.State.CurrentTravelerState == TravelerState.AtDestination)
            {
                Log("Arrived at destination");
                _buyItemsState = BuyItemsState.BuyItems;
                ESCache.Instance.Traveler.Destination = null;

                return;
            }

            if (ESCache.Instance.State.CurrentTravelerState == TravelerState.Error)
            {
                if (ESCache.Instance.Traveler.Destination != null)
                    Log("Stopped traveling, traveller threw an error...");

                ESCache.Instance.Traveler.Destination = null;
                _state = AbyssalState.Error;
                return;
            }

        }

        private void BuyItemsInJita()
        {

            if (!ESCache.Instance.InDockableLocation)
                return;

            if (ESCache.Instance.DirectEve.GetItemHangar() == null)
                return;

            if (ESCache.Instance.CurrentShipsCargo == null)
                return;

            // Is there a market window?
            var marketWindow = ESCache.Instance.DirectEve.Windows.OfType<DirectMarketWindow>().FirstOrDefault();

            if (!_buyList.Any())
            {
                // Close the market window if there is one
                if (marketWindow != null)
                    marketWindow.Close();

                Log("Finished buying changing state to MoveItemsToCargo");
                _buyItemsState = BuyItemsState.MoveItemsToCargo;
                return;
            }

            var currentBuyListItem = _buyList.FirstOrDefault();

            var typeID = currentBuyListItem.Key;
            var itemQuantity = currentBuyListItem.Value;

            if (ESCache.Instance.DirectEve.GetItemHangar() == null)
                return;

            // Do we have the items we need in the Item Hangar?
            if (ESCache.Instance.DirectEve.GetItemHangar().Items.Where(i => i.TypeId == typeID).Sum(i => i.Stacksize) >= itemQuantity)
            {
                var itemInHangar = ESCache.Instance.DirectEve.GetItemHangar().Items.FirstOrDefault(i => i.TypeId == typeID);
                if (itemInHangar != null)
                    Log("We have [" +
                        ESCache.Instance.DirectEve.GetItemHangar().Items.Where(i => i.TypeId == typeID)
                            .Sum(i => i.Stacksize)
                            .ToString(CultureInfo.InvariantCulture) +
                        "] " + itemInHangar.TypeName + " in the item hangar.");

                _buyList.Remove(typeID);
                return;
            }

            // We do not have enough of that type, open the market window
            if (marketWindow == null)
            {
                LocalPulse = DateTime.UtcNow.AddSeconds(10);

                Log("Opening market window");

                ESCache.Instance.DirectEve.ExecuteCommand(DirectCmd.OpenMarket);
                return;
            }

            // Wait for the window to become ready
            if (!marketWindow.IsReady)
                return;

            // Are we currently viewing the correct orders?
            if (marketWindow.DetailTypeId != typeID)
            {
                // No, load the orders
                marketWindow.LoadTypeId(typeID);

                Log("Loading market window");

                LocalPulse = DateTime.UtcNow.AddSeconds(10);
                return;
            }

            // Get the median sell price
            var type = ESCache.Instance.DirectEve.GetInvType(typeID);

            var currentItem = type;
            double maxPrice = 0;

            if (currentItem != null)
            {
                var avgPrice = currentItem.AveragePrice();
                var basePrice = currentItem.BasePrice / currentItem.PortionSize;

                Log("Item [" + currentItem.TypeName + "] avgPrice [" + avgPrice + "] basePrice [" + basePrice +
                    "] groupID [" +
                    currentItem.GroupId + "] groupName [" + currentItem.GroupId + "]");

                if (avgPrice != 0)
                {
                    maxPrice = avgPrice * _maxAvgPriceMultiplier; // 3 times the avg price
                }
                else
                {
                    if (basePrice != 0)
                        maxPrice = basePrice * _maxBasePriceMultiplier; // 6 times the base price
                    else
                        maxPrice = 1000;
                }

                Log("Item [" + currentItem.TypeName + "] avgPrice [" + avgPrice + "] basePrice [" + basePrice + "]");
            }

            // Are there any order with a reasonable price?
            IEnumerable<DirectOrder> orders;
            if (maxPrice == 0)
            {
                Log("if(maxPrice == 0)");
                orders =
                    marketWindow.SellOrders.Where(o => o.StationId == ESCache.Instance.DirectEve.Session.StationId && o.TypeId == typeID).ToList();
            }
            else
            {
                Log("if(maxPrice != 0) max price [" + maxPrice + "]");
                orders =
                    marketWindow.SellOrders.Where(
                            o => o.StationId == ESCache.Instance.DirectEve.Session.StationId && o.Price < maxPrice && o.TypeId == typeID)
                        .ToList();
            }

            _orderIterations++;

            if (!orders.Any() && _orderIterations < 5)
            {
                LocalPulse = DateTime.UtcNow.AddSeconds(5);
                return;
            }

            // Is there any order left?
            if (!orders.Any())
            {
                Log("No reasonably priced item available! Removing this item from the buyList");
                _buyList.Remove(typeID);
                LocalPulse = DateTime.UtcNow.AddSeconds(3);
                return;
            }

            // How many items do we still need?
            var neededQuantity = itemQuantity - ESCache.Instance.DirectEve.GetItemHangar().Items.Where(i => i.TypeId == typeID).Sum(i => i.Stacksize);
            if (neededQuantity > 0)
            {
                // Get the first order
                var order = orders.OrderBy(o => o.Price).FirstOrDefault();
                if (order != null)
                {
                    // Calculate how many we still need
                    var remaining = Math.Min(neededQuantity, order.VolumeRemaining);
                    var orderPrice = (long)(remaining * order.Price);

                    if (orderPrice < ESCache.Instance.DirectEve.Me.Wealth)
                    {
                        Log("Buying [" + remaining + "] item price [" + order.Price + "]");
                        order.Buy(remaining, DirectOrderRange.Station);

                        // Wait for the order to go through
                        LocalPulse = DateTime.UtcNow.AddSeconds(10);
                    }
                    else
                    {
                        Log("Error: We don't have enough ISK on our wallet to finish that transaction.");
                        _state = AbyssalState.Error;
                        return;
                    }
                }
            }
        }


        private void MoveItemsToCargo()
        {
            if (!ESCache.Instance.InDockableLocation)
                return;

            if (ESCache.Instance.DirectEve.GetItemHangar() == null)
                return;

            if (ESCache.Instance.CurrentShipsCargo == null)
                return;

            IEnumerable<DirectItem> items = ESCache.Instance.DirectEve.GetItemHangar().Items.Where(i => _moveToCargoList.ContainsKey(i.TypeId)).ToList();
            if (items.Any())
            {
                if (!ESCache.Instance.DirectEve.NoLockedItemsOrWaitAndClearLocks())
                {
                    Log($"Waiting on locked items.");
                    return;
                }

                var item = items.FirstOrDefault();

                var maxAmountToMove = Math.Min(item.Stacksize, _moveToCargoList[item.TypeId]);
                maxAmountToMove = Math.Max(1, maxAmountToMove);
                var volumeToMove = item.Volume * maxAmountToMove;

                var remainingCapacity = ESCache.Instance.CurrentShipsCargo.Capacity - ESCache.Instance.CurrentShipsCargo.UsedCapacity;

                if (volumeToMove > remainingCapacity)
                {
                    Log($"Error. Not enough cargo space left in the current active ship.");
                    _state = AbyssalState.Error;
                    return;
                }

                if (ESCache.Instance.CurrentShipsCargo.Add(item, maxAmountToMove))
                {
                    LocalPulse = DateTime.UtcNow.AddSeconds(5);
                    Log($"Moving Amount [{maxAmountToMove}] TypeName [{item.TypeName}] to the current ships cargohold.");
                }
                return;
            }

            Log("Done moving items to cargohold");
            _buyItemsState = BuyItemsState.TravelToHomeSystem;
        }

        private void TravelToHomeSystem()
        {
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
                _buyItemsState = BuyItemsState.UnloadLoot;
            }
        }

        private void UnloadLoot()
        {

            if (!AreWeDockedInHomeSystem())
            {
                Log("We are not docking in the home system, going to the home system.");
                _buyItemsState = BuyItemsState.TravelToHomeSystem;
                return;
            }

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
                // done
                Log("We finished buying items");
                _state = AbyssalState.Start;
                _buyItemsState = BuyItemsState.CreateBuyList;
            }
        }
    }
}
