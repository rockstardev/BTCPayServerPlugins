using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.Services;
using Microsoft.Extensions.Caching.Memory;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Wabisabi
{
    public class WabisabiService
    {
        private readonly IStoreRepository _storeRepository;
        private readonly WabisabiCoordinatorClientInstanceManager _coordinatorClientInstanceManager;
        private readonly WalletProvider _walletProvider;
        private readonly WalletRepository _walletRepository;
        private readonly IMemoryCache _memoryCache;
        private string[] _ids => _coordinatorClientInstanceManager.HostedServices.Keys.ToArray();

        public WabisabiService(IStoreRepository storeRepository,
            WabisabiCoordinatorClientInstanceManager coordinatorClientInstanceManager,
            WalletProvider walletProvider,
            WalletRepository walletRepository,
            IMemoryCache memoryCache)
        {
            _storeRepository = storeRepository;
            _coordinatorClientInstanceManager = coordinatorClientInstanceManager;
            _walletProvider = walletProvider;
            _walletRepository = walletRepository;
            _memoryCache = memoryCache;
        }

        public static string GetCacheKey(string storeId)
        {
            return $"{nameof(WabisabiStoreSettings)}-{storeId}";
        }
        public async Task<WabisabiStoreSettings> GetWabisabiForStore(string storeId)
        {
            
            var res = await _memoryCache.GetOrCreate(GetCacheKey(storeId), async entry =>
            {
                var res = await  _storeRepository.GetSettingAsync<WabisabiStoreSettings>(storeId, nameof(WabisabiStoreSettings));
                res ??= new WabisabiStoreSettings();
                res.Settings = res.Settings.Where(settings => _ids.Contains(settings.Coordinator)).ToList();
                res.Settings.ForEach(settings =>
                {
                    if(settings.RoundWhenEnabled != null && string.IsNullOrEmpty(settings.RoundWhenEnabled.PlebsDontPayThreshold))
                    {
                        settings.RoundWhenEnabled.PlebsDontPayThreshold = "1000000";
                    }
                });
                return res;
            });
            
            foreach (var wabisabiCoordinatorManager in _coordinatorClientInstanceManager.HostedServices)
            {
                if (res.Settings.All(settings => settings.Coordinator != wabisabiCoordinatorManager.Key))
                {
                    res.Settings.Add(new WabisabiStoreCoordinatorSettings()
                    {
                        Coordinator = wabisabiCoordinatorManager.Key,
                    });
                }
            }

            return res;
        }

        public async Task SetWabisabiForStore(string storeId, WabisabiStoreSettings wabisabiSettings, string termsCoord = null)
        {
            foreach (var setting in wabisabiSettings.Settings.Where(setting => !setting.Enabled))
            {
                _walletProvider.LoadedWallets.TryGetValue(storeId, out var walletTask);
                if (walletTask != null)
                {
                    var wallet = await walletTask.Value;
                    await _coordinatorClientInstanceManager.StopWallet(wallet, setting.Coordinator);
                }
            }
   
            if (wabisabiSettings.Settings.All(settings => !settings.Enabled))
            {
                
                await _storeRepository.UpdateSetting<WabisabiStoreSettings>(storeId, nameof(WabisabiStoreSettings), null!);
            }
            else
            {
                var res = await GetWabisabiForStore(storeId);
                foreach (var wabisabiStoreCoordinatorSettings in wabisabiSettings.Settings)
                {
                    if (!wabisabiStoreCoordinatorSettings.Enabled)
                    {
                        wabisabiStoreCoordinatorSettings.RoundWhenEnabled = null;
                    }else if (
                        (termsCoord == wabisabiStoreCoordinatorSettings.Coordinator ||
                        res?.Settings?.Find(settings =>
                                  settings.Coordinator == wabisabiStoreCoordinatorSettings.Coordinator)?.RoundWhenEnabled is null) && 
                              _coordinatorClientInstanceManager.HostedServices.TryGetValue(wabisabiStoreCoordinatorSettings.Coordinator, out var coordinator))
                    {
                        var round = coordinator.RoundStateUpdater.RoundStates.LastOrDefault();
                        wabisabiStoreCoordinatorSettings.RoundWhenEnabled = new LastCoordinatorRoundConfig()
                        {
                            CoordinationFeeRate = round.Value.CoinjoinState.Parameters.CoordinationFeeRate.Rate,
                            PlebsDontPayThreshold = round.Value.CoinjoinState.Parameters.CoordinationFeeRate
                                .PlebsDontPayThreshold.Satoshi.ToString(),
                            MinInputCountByRound = round.Value.CoinjoinState.Parameters.MinInputCountByRound,
                        };
                    }
                    
                    
                }
                await _storeRepository.UpdateSetting(storeId, nameof(WabisabiStoreSettings), wabisabiSettings!);
            }
            _memoryCache.Remove(GetCacheKey(storeId));
            await _walletProvider.SettingsUpdated(storeId, wabisabiSettings);
           // var existingProcessor = (await  _payoutProcessorService.GetProcessors(new PayoutProcessorService.PayoutProcessorQuery()
           // {
           //     Stores = new[] {storeId},
           //     Processors = new[] {"Wabisabi"},
           //
           // })).FirstOrDefault();
           //  _eventAggregator.Publish(new PayoutProcessorUpdated()
           //  {
           //      Id = existingProcessor?.Id,
           //      Data = paybatching? new PayoutProcessorData()
           //      {
           //          Id = existingProcessor?.Id,
           //          Processor = "Wabisabi",
           //          StoreId = storeId,
           //          PaymentMethod = "BTC",
           //      }: null
           //  });
        }
        

        public async Task<List<BTCPayWallet.CoinjoinData>> GetCoinjoinHistory(string storeId, bool force = false)
        {
            var k = GetCacheKey(storeId) + "cjhistory";
            if (force)
            {
                _memoryCache.Remove(k);
            }

            return await _memoryCache.GetOrCreateAsync(k, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
                var result =  (await _walletRepository.GetWalletObjects(
                        new GetWalletObjectsQuery(new WalletId(storeId, "BTC"))
                        {
                            Type = "coinjoin"
                        })).Values.Where(data => !string.IsNullOrEmpty(data.Data))
                    .Select(data => JObject.Parse(data.Data).ToObject<BTCPayWallet.CoinjoinData>())
                    .OrderByDescending(tuple => tuple.Timestamp).ToList();
                entry.Value = result;
                
                return result;
            });

        }
    }
    
}
