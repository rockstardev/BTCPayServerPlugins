using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Data.Payouts.LightningLike;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services;
using BTCPayServer.Services.Stores;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;
using Newtonsoft.Json.Linq;
using LightningAddressData = BTCPayServer.Data.LightningAddressData;

namespace BTCPayServer.Plugins.Prism
{
    /// <summary>
    /// monitors stores that have prism enabled and detects incoming payments based on the lightning address splits the funds to the destinations once the threshold is reached
    /// </summary>
    public class SatBreaker : EventHostedServiceBase
    {
        private readonly StoreRepository _storeRepository;
        private readonly ILogger<SatBreaker> _logger;
        private readonly LightningAddressService _lightningAddressService;
        private readonly PullPaymentHostedService _pullPaymentHostedService;
        private readonly LightningLikePayoutHandler _lightningLikePayoutHandler;
        private readonly BTCPayNetworkProvider _btcPayNetworkProvider;
        private readonly LightningClientFactoryService _lightningClientFactoryService;
        private readonly IOptions<LightningNetworkOptions> _lightningNetworkOptions;
        private readonly BTCPayNetworkJsonSerializerSettings _btcPayNetworkJsonSerializerSettings;
        private readonly IPluginHookService _pluginHookService;
        private Dictionary<string, PrismSettings> _prismSettings;

        public event EventHandler<PrismPaymentDetectedEventArgs> PrismUpdated;

        public SatBreaker(StoreRepository storeRepository,
            EventAggregator eventAggregator,
            ILogger<SatBreaker> logger,
            LightningAddressService lightningAddressService,
            PullPaymentHostedService pullPaymentHostedService,
            LightningLikePayoutHandler lightningLikePayoutHandler,
            BTCPayNetworkProvider btcPayNetworkProvider,
            LightningClientFactoryService lightningClientFactoryService,
            IOptions<LightningNetworkOptions> lightningNetworkOptions,
            BTCPayNetworkJsonSerializerSettings btcPayNetworkJsonSerializerSettings,
            IPluginHookService pluginHookService) : base(eventAggregator, logger)
        {
            _storeRepository = storeRepository;
            _logger = logger;
            _lightningAddressService = lightningAddressService;
            _pullPaymentHostedService = pullPaymentHostedService;
            _lightningLikePayoutHandler = lightningLikePayoutHandler;
            _btcPayNetworkProvider = btcPayNetworkProvider;
            _lightningClientFactoryService = lightningClientFactoryService;
            _lightningNetworkOptions = lightningNetworkOptions;
            _btcPayNetworkJsonSerializerSettings = btcPayNetworkJsonSerializerSettings;
            _pluginHookService = pluginHookService;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _prismSettings = await _storeRepository.GetSettingsAsync<PrismSettings>(nameof(PrismSettings));
            foreach (var keyValuePair in _prismSettings)
            {
                keyValuePair.Value.Splits ??= new List<Split>();
                keyValuePair.Value.Destinations ??= new Dictionary<string, PrismDestination>();
                keyValuePair.Value.PendingPayouts ??= new Dictionary<string, PendingPayout>();
            }

            await base.StartAsync(cancellationToken);
            PushEvent(new CheckPayoutsEvt());
        }

        protected override void SubscribeToEvents()
        {
            base.SubscribeToEvents();
            Subscribe<InvoiceEvent>();
            Subscribe<PayoutEvent>();
            Subscribe<StoreRemovedEvent>();
        }

        class CheckPayoutsEvt
        {
        }

        private TaskCompletionSource _checkPayoutTcs = new();

        /// <summary>
        /// Go through generated payouts and check if they are completed or cancelled, and then remove them from the list.
        /// If the fee can be fetched, we compute what the difference was from the original fee we computed (hardcoded 2% of the balance)
        /// and we adjust the balance with the difference( credit if the fee was lower, debit if the fee was higher)
        /// </summary>
        /// <param name="cancellationToken"></param>
        private async Task CheckPayouts(CancellationToken cancellationToken)
        {
            try
            {
                _checkPayoutTcs = new TaskCompletionSource();

                var payoutsToCheck =
                    _prismSettings.ToDictionary(pair => pair.Key, pair => pair.Value.PendingPayouts);
                var payoutIds = payoutsToCheck
                    .SelectMany(pair => pair.Value?.Keys.ToArray() ?? Array.Empty<string>()).ToArray();
                var payouts = (await _pullPaymentHostedService.GetPayouts(new PullPaymentHostedService.PayoutQuery()
                {
                    PayoutIds = payoutIds,
                    States = new[] {PayoutState.Cancelled, PayoutState.Completed}
                }));
                var lnClients = new Dictionary<string, ILightningClient>();
                var payoutsByStore = payouts.GroupBy(data => data.StoreDataId);
                foreach (var storePayouts in payoutsByStore)
                {
                    if (!_prismSettings.TryGetValue(storePayouts.Key, out var prismSettings))
                    {
                        continue;
                    }

                    if (!payoutsToCheck.TryGetValue(storePayouts.Key, out var pendingPayouts))
                    {
                        continue;
                    }

                    foreach (var payout in storePayouts)
                    {
                        if (!pendingPayouts.TryGetValue(payout.Id, out var pendingPayout))
                        {
                            continue;
                        }

                        long toCredit = 0;
                        switch (payout.State)
                        {
                            case PayoutState.Completed:

                                var proof = _lightningLikePayoutHandler.ParseProof(payout) as PayoutLightningBlob;

                                long? feePaid = null;
                                if (!string.IsNullOrEmpty(proof?.PaymentHash))
                                {
                                    if (!lnClients.TryGetValue(payout.StoreDataId, out var lnClient))
                                    {
                                        var store = await _storeRepository.FindStore(payout.StoreDataId);

                                        var network = _btcPayNetworkProvider.GetNetwork<BTCPayNetwork>("BTC");
                                        var id = new PaymentMethodId("BTC", LightningPaymentType.Instance);
                                        var existing = store.GetSupportedPaymentMethods(_btcPayNetworkProvider)
                                            .OfType<LightningSupportedPaymentMethod>()
                                            .FirstOrDefault(d => d.PaymentId == id);
                                        if (existing?.GetExternalLightningUrl() is { } connectionString)
                                        {
                                            lnClient = _lightningClientFactoryService.Create(connectionString,
                                                network);
                                        }
                                        else if (existing?.IsInternalNode is true &&
                                                 _lightningNetworkOptions.Value.InternalLightningByCryptoCode
                                                     .TryGetValue(network.CryptoCode,
                                                         out var internalLightningNode))
                                        {
                                            lnClient = _lightningClientFactoryService.Create(internalLightningNode,
                                                network);
                                        }


                                        lnClients.Add(payout.StoreDataId, lnClient);
                                    }

                                    if (lnClient is not null && proof?.PaymentHash is not null)
                                    {
                                        try
                                        {
                                            var p = await lnClient.GetPayment(proof.PaymentHash, CancellationToken);
                                            feePaid = (long) p?.Fee?.ToUnit(LightMoneyUnit.Satoshi);
                                        }
                                        catch (Exception e)
                                        {
                                            _logger.LogError(e,
                                                "The payment fee could not be fetched from the lightning node due to an error, so we will use the allocated 2% as the fee.");
                                        }
                                    }
                                }

                                if (feePaid is not null)
                                {
                                    toCredit = pendingPayout.FeeCharged - feePaid.Value;
                                }

                                break;
                            case PayoutState.Cancelled:
                                toCredit = pendingPayout.PayoutAmount + pendingPayout.FeeCharged;
                                break;
                        }

                        var destinationId = pendingPayout.DestinationId ??
                                            payout.GetBlob(_btcPayNetworkJsonSerializerSettings).Destination;
                        if (prismSettings.DestinationBalance.TryGetValue(destinationId,
                                out var currentBalance))
                        {
                            prismSettings.DestinationBalance[destinationId] =
                                currentBalance + (toCredit * 1000);
                        }
                        else
                        {
                            prismSettings.DestinationBalance.Add(destinationId,
                                (toCredit * 1000));
                        }

                        prismSettings.PendingPayouts.Remove(payout.Id);
                    }

                    if (await CreatePayouts(storePayouts.Key, prismSettings))
                    {
                        await UpdatePrismSettingsForStore(storePayouts.Key, prismSettings, true);
                    }
                }

            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error while checking payouts");
            }

            _ = Task.WhenAny(_checkPayoutTcs.Task, Task.Delay(TimeSpan.FromMinutes(1), cancellationToken)).ContinueWith(
                task =>
                {
                    if (!task.IsCanceled)
                        PushEvent(new CheckPayoutsEvt());
                }, cancellationToken);
        }

        public async Task WaitAndLock(CancellationToken cancellationToken = default)
        {
            await _updateLock.WaitAsync(cancellationToken);
        }

        public void Unlock()
        {
            _updateLock.Release();
        }
        private readonly SemaphoreSlim _updateLock = new(1, 1);

        public async Task<PrismSettings> Get(string storeId)
        {
            return JObject
                .FromObject(_prismSettings.TryGetValue(storeId, out var settings) && settings is not null
                    ? settings
                    : new PrismSettings()).ToObject<PrismSettings>();
        }

        public async Task<bool> UpdatePrismSettingsForStore(string storeId, PrismSettings updatedSettings,
            bool skipLock = false)
        {
            try
            {
                if (!skipLock)
                    await WaitAndLock();
                var currentSettings = await Get(storeId);

                if (currentSettings.Version != updatedSettings.Version)
                {
                    return false; // Indicate that the update failed due to a version mismatch
                }

                updatedSettings.Version++; // Increment the version number

                // Update the settings in the dictionary
                _prismSettings.AddOrReplace(storeId, updatedSettings);

                // Update the settings in the StoreRepository
                await _storeRepository.UpdateSetting(storeId, nameof(PrismSettings), updatedSettings);
            }

            finally
            {
                if (!skipLock)
                    Unlock();
            }

            var prismPaymentDetectedEventArgs = new PrismPaymentDetectedEventArgs()
            {
                StoreId = storeId,
                Settings = updatedSettings
            };
            PrismUpdated?.Invoke(this, prismPaymentDetectedEventArgs);
            return true; // Indicate that the update succeeded
        }

        /// <summary>
        /// if an invoice is completed, check if it was created through a lightning address, and if the store has prism enabled and one of the splits' source is the same lightning address, grab the paid amount, split it based on the destination percentages, and credit it inside the prism destbalances.
        /// When the threshold is reached (plus a 2% reserve fee to account for fees), create a payout and deduct the balance. 
        /// </summary>
        /// <param name="evt"></param>
        /// <param name="cancellationToken"></param>
        protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
        {
            try
            {
                await WaitAndLock(cancellationToken);

                switch (evt)
                {
                    case StoreRemovedEvent storeRemovedEvent:
                        _prismSettings.Remove(storeRemovedEvent.StoreId);
                        return;
                    case InvoiceEvent invoiceEvent when
                        new[] {InvoiceEventCode.Confirmed, InvoiceEventCode.MarkedCompleted}.Contains(
                            invoiceEvent.EventCode):
                    {
                        if (!_prismSettings.TryGetValue(invoiceEvent.Invoice.StoreId, out var prismSettings) ||
                            !prismSettings.Enabled)
                        {
                            return;
                        }

                        var onChainCatchAllIdentifier = "*" + PaymentTypes.BTCLike.ToStringNormalized();
                        var catchAllPrism = prismSettings.Splits.FirstOrDefault(split =>
                            split.Source == "*" || split.Source == onChainCatchAllIdentifier);
                        Split prism = null;
                        LightningAddressData address = null;

                        var pm = invoiceEvent.Invoice.GetPaymentMethod(new PaymentMethodId("BTC",
                            LNURLPayPaymentType.Instance));
                        var pmd = pm?.GetPaymentMethodDetails() as LNURLPayPaymentMethodDetails;
                        if (string.IsNullOrEmpty(pmd?.ConsumedLightningAddress) && catchAllPrism is null)
                        {
                            return;
                        }
                        else if (!string.IsNullOrEmpty(pmd?.ConsumedLightningAddress))
                        {
                            address = await _lightningAddressService.ResolveByAddress(
                                pmd.ConsumedLightningAddress.Split("@")[0]);
                            if (address is null)
                            {
                                return;
                            }

                            prism = prismSettings.Splits.FirstOrDefault(s =>
                                s.Source.Equals(address.Username, StringComparison.InvariantCultureIgnoreCase));
                        }
                        else if (catchAllPrism?.Source == onChainCatchAllIdentifier)
                        {
                            pm = invoiceEvent.Invoice.GetPaymentMethod(new PaymentMethodId("BTC", PaymentTypes.BTCLike));
                            prism = catchAllPrism;
                        }
                        else
                        {
                            pm = invoiceEvent.Invoice.GetPaymentMethod(invoiceEvent.Invoice.GetPayments(true)
                                .FirstOrDefault()?.GetPaymentMethodId());
                            prism = catchAllPrism;
                        }

                        var splits = prism?.Destinations;
                        if (splits?.Any() is not true || pm is null)
                        {
                            return;
                        }


                        var msats = LightMoney.FromUnit(pm.Calculate().CryptoPaid, LightMoneyUnit.BTC)
                            .ToUnit(LightMoneyUnit.MilliSatoshi);
                        if (msats <= 0)
                        {
                            return;
                        }

                        //compute the sats for each destination  based on splits percentage
                        var msatsPerDestination =
                            splits.ToDictionary(s => s.Destination, s => (long) (msats * (s.Percentage / 100)));

                        prismSettings.DestinationBalance ??= new Dictionary<string, long>();
                        foreach (var (destination, splitMSats) in msatsPerDestination)
                        {
                            if (prismSettings.DestinationBalance.TryGetValue(destination, out var currentBalance))
                            {
                                prismSettings.DestinationBalance[destination] = currentBalance + splitMSats;
                            }
                            else if (splitMSats > 0)
                            {
                                prismSettings.DestinationBalance.Add(destination, splitMSats);
                            }
                        }

                        await UpdatePrismSettingsForStore(invoiceEvent.Invoice.StoreId, prismSettings, true);
                        if (await CreatePayouts(invoiceEvent.Invoice.StoreId, prismSettings))
                        {
                            await UpdatePrismSettingsForStore(invoiceEvent.Invoice.StoreId, prismSettings, true);
                        }

                        break;
                    }
                    case CheckPayoutsEvt:
                        await CheckPayouts(cancellationToken);
                        break;
                    case PayoutEvent payoutEvent when !_prismSettings.TryGetValue(payoutEvent.Payout.StoreDataId, out var prismSettings) || payoutEvent.Type != PayoutEvent.PayoutEventType.Approved:
                        return;
                    case PayoutEvent payoutEvent:
                        _checkPayoutTcs?.TrySetResult();
                        break;
                }
            }
            catch (Exception e)
            {
                Logs.PayServer.LogWarning(e, "Error while processing prism event");
            }
            finally
            {
                Unlock();
            }
        }


        private async Task<bool> CreatePayouts(string storeId, PrismSettings prismSettings)
        {
            if (!prismSettings.Enabled)
            {
                return false;
            }
            var result = false;
            prismSettings.DestinationBalance ??= new Dictionary<string, long>();
            prismSettings.Destinations ??= new Dictionary<string, PrismDestination>();
            foreach (var (destination, amtMsats) in prismSettings.DestinationBalance)
            {
                prismSettings.Destinations.TryGetValue(destination, out var destinationSettings);
                var satThreshold = destinationSettings?.SatThreshold ?? prismSettings.SatThreshold;
                var reserve = destinationSettings?.Reserve ?? prismSettings.Reserve;

                var amt = amtMsats / 1000;
                if (amt < satThreshold) continue;
                var percentage = reserve / 100;
                var reserveFee = (long) Math.Max(0, Math.Round(amt * percentage, 0, MidpointRounding.AwayFromZero));
                var payoutAmount = amt - reserveFee;
                if (payoutAmount <= 0)
                {
                    continue;
                }

                var pmi = string.IsNullOrEmpty(destinationSettings?.PaymentMethodId) ||
                          !PaymentMethodId.TryParse(destinationSettings?.PaymentMethodId, out var pmi2)
                    ? new PaymentMethodId("BTC", LightningPaymentType.Instance)
                    : pmi2;

                var source = "Prism";
                if (destinationSettings is not null)
                {
                    source+= $" ({destination})";
                }
                var claimRequest = new ClaimRequest()
                {
                    Destination = new LNURLPayClaimDestinaton(destinationSettings?.Destination ?? destination),
                    PreApprove = true,
                    StoreId = storeId,
                    PaymentMethodId = pmi,
                    Value = Money.Satoshis(payoutAmount).ToDecimal(MoneyUnit.BTC),
                    Metadata = JObject.FromObject(new
                    {
                        Source = source 
                    })
                };
                claimRequest =
                    (await _pluginHookService.ApplyFilter("prism-claim-create", claimRequest)) as ClaimRequest;

                if (claimRequest is null)
                {
                    continue;
                }

                var payout = await _pullPaymentHostedService.Claim(claimRequest);
                if (payout.Result != ClaimRequest.ClaimResult.Ok) continue;
                prismSettings.PendingPayouts ??= new Dictionary<string, PendingPayout>();
                prismSettings.PendingPayouts.Add(payout.PayoutData.Id,
                    new PendingPayout(payoutAmount, reserveFee, destination));
                var newAmount = amtMsats - (payoutAmount + reserveFee) * 1000;
                if (newAmount == 0)
                    prismSettings.DestinationBalance.Remove(destination);
                else
                {
                    prismSettings.DestinationBalance.AddOrReplace(destination, newAmount);
                }

                result = true;
            }

            return result;
        }

        public void TriggerPayoutCheck()
        {
            _checkPayoutTcs?.TrySetResult();
        }
    }

    public class PrismPaymentDetectedEventArgs
    {
        public string StoreId { get; set; }
        public PrismSettings Settings { get; set; }
    }
}