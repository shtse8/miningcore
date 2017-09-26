﻿/* 
Copyright 2017 Coin Foundry (coinfoundry.org)
Authors: Oliver Weichhold (oliver@weichhold.com)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the "Software"), to deal in the Software without restriction, 
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, 
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, 
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial 
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT 
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. 
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, 
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE 
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.Metadata;
using AutoMapper;
using MiningCore.Blockchain.Bitcoin.DaemonResponses;
using MiningCore.Configuration;
using MiningCore.DaemonInterface;
using MiningCore.Notifications;
using MiningCore.Payments;
using MiningCore.Persistence;
using MiningCore.Persistence.Model;
using MiningCore.Persistence.Repositories;
using MiningCore.Util;
using Newtonsoft.Json;
using Contract = MiningCore.Contracts.Contract;

namespace MiningCore.Blockchain.Bitcoin
{
    [CoinMetadata(
        CoinType.BTC, CoinType.BCC, CoinType.NMC, CoinType.PPC,
        CoinType.LTC, CoinType.DOGE, CoinType.DGB, CoinType.VIA,
        CoinType.GRS, CoinType.DASH, CoinType.ZEC)]
    public class BitcoinPayoutHandler : PayoutHandlerBase,
        IPayoutHandler
    {
        public BitcoinPayoutHandler(
            IComponentContext ctx,
            IConnectionFactory cf, 
            IMapper mapper,
            IShareRepository shareRepo,
            IBlockRepository blockRepo,
            IBalanceRepository balanceRepo,
            IPaymentRepository paymentRepo,
            IEnumerable<Meta<INotificationSender, NotificationSenderMetadataAttribute>> notificationSenders) :
            base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, notificationSenders)
        {
            Contract.RequiresNonNull(ctx, nameof(ctx));
            Contract.RequiresNonNull(balanceRepo, nameof(balanceRepo));
            Contract.RequiresNonNull(paymentRepo, nameof(paymentRepo));

            this.ctx = ctx;
        }

        private readonly IComponentContext ctx;
        private DaemonClient daemon;

        protected override string LogCategory => "Bitcoin Payout Handler";

        #region IPayoutHandler

        public void Configure(ClusterConfig clusterConfig, PoolConfig poolConfig)
        {
            Contract.RequiresNonNull(poolConfig, nameof(poolConfig));

            this.poolConfig = poolConfig;
            this.clusterConfig = clusterConfig;

            logger = LogUtil.GetPoolScopedLogger(typeof(BitcoinPayoutHandler), poolConfig);

            var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();
            daemon = new DaemonClient(jsonSerializerSettings);
            daemon.Configure(poolConfig.Daemons);
        }

        public async Task<Block[]> ClassifyBlocksAsync(Block[] blocks)
        {
            Contract.RequiresNonNull(poolConfig, nameof(poolConfig));
            Contract.RequiresNonNull(blocks, nameof(blocks));

            var pageSize = 100;
            var pageCount = (int) Math.Ceiling(blocks.Length / (double) pageSize);
            var result = new List<Block>();

            var immatureCount = 0;

            for (var i = 0; i < pageCount; i++)
            {
                // get a page full of blocks
                var page = blocks
                    .Skip(i * pageSize)
                    .Take(pageSize)
                    .ToArray();

                // build command batch (block.TransactionConfirmationData is the hash of the blocks coinbase transaction)
                var batch = page.Select(block => new DaemonCmd(BitcoinCommands.GetTransaction,
                    new[] {block.TransactionConfirmationData})).ToArray();

                // execute batch
                var results = await daemon.ExecuteBatchAnyAsync(batch);

                for (var j = 0; j < results.Length; j++)
                {
                    var cmdResult = results[j];

                    var transactionInfo = cmdResult.Response?.ToObject<Transaction>();
                    var block = page[j];

                    // check error
                    if (cmdResult.Error != null)
                    {
                        // Code -5 interpreted as "orphaned"
                        if (cmdResult.Error.Code == -5)
                        {
                            block.Status = BlockStatus.Orphaned;
                            result.Add(block);
                        }

                        else
                        {
                            logger.Warn(() => $"[{LogCategory}] Daemon reports error '{cmdResult.Error.Message}' (Code {cmdResult.Error.Code}) for transaction {page[j].TransactionConfirmationData}");
                        }
                    }

                    // missing transaction details are interpreted as "orphaned"
                    else if (transactionInfo?.Details == null || transactionInfo.Details.Length == 0)
                    {
                        block.Status = BlockStatus.Orphaned;
                        result.Add(block);
                    }

                    else
                    {
                        switch (transactionInfo.Details[0].Category)
                        {
                            case "immature":
                                // coinbase transaction that is not spendable yet, do nothing and let it mature
                                immatureCount++;
                                break;

                            case "generate":
                                // matured and spendable coinbase transaction
                                block.Status = BlockStatus.Confirmed;
                                result.Add(block);
                                break;

                            default:
                                block.Status = BlockStatus.Orphaned;
                                result.Add(block);
                                break;
                        }
                    }
                }
            }

            return result.ToArray();
        }

        public Task UpdateBlockRewardBalancesAsync(IDbConnection con, IDbTransaction tx, Block block, PoolConfig pool)
        {
            // reward-payouts are handled through coinbase-tx for bitcoin and family
            return Task.FromResult(false);
        }

        public async Task PayoutAsync(Balance[] balances)
        {
            Contract.RequiresNonNull(balances, nameof(balances));

            // build args
            var amounts = balances
                .Where(x => x.Amount > 0)
                .ToDictionary(x => x.Address, x => Math.Round(x.Amount, 8));

            if (amounts.Count == 0)
                return;

            logger.Info(() => $"[{LogCategory}] Paying out {FormatAmount(balances.Sum(x => x.Amount))} to {balances.Length} addresses");

            var subtractFeesFrom = amounts.Keys.ToArray();

            var args = new object[]
            {
                string.Empty,           // default account 
                amounts,                // addresses and associated amounts
                1,                      // only spend funds covered by this many confirmations
                "CMPool Payout",    // comment
                subtractFeesFrom        // distribute transaction fee equally over all recipients
            };

            // send command
            var result = await daemon.ExecuteCmdAnyAsync<string>(BitcoinCommands.SendMany, args, new JsonSerializerSettings());

            if (result.Error == null)
            {
                var txId = result.Response;

                // check result
                if (string.IsNullOrEmpty(txId))
                    logger.Error(() => $"[{LogCategory}] Daemon command '{BitcoinCommands.SendMany}' did not return a transaction id!");
                else
                    logger.Info(() => $"[{LogCategory}] Payout transaction id: {txId}");

                PersistPayments(balances, txId);

                await NotifyPayoutSuccess(balances, txId, null);
            }

            else
            {
                logger.Error(() => $"[{LogCategory}] Daemon command '{BitcoinCommands.SendMany}' returned error: {result.Error.Message} code {result.Error.Code}");

                await NotifyPayoutFailureAsync(balances, $"Daemon command '{BitcoinCommands.SendMany}' returned error: {result.Error.Message} code {result.Error.Code}", null);
            }
        }

        #endregion // IPayoutHandler
    }
}
