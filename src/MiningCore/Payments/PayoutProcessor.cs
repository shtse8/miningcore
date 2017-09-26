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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.Metadata;
using MiningCore.Configuration;
using MiningCore.Extensions;
using MiningCore.Notifications;
using MiningCore.Persistence;
using MiningCore.Persistence.Model;
using MiningCore.Persistence.Repositories;
using NLog;
using Contract = MiningCore.Contracts.Contract;

namespace MiningCore.Payments
{
    /// <summary>
    /// Coin agnostic payment processor
    /// </summary>
    public class PayoutProcessor
    {
        public PayoutProcessor(IComponentContext ctx,
            IConnectionFactory cf,
            IBlockRepository blockRepo,
            IShareRepository shareRepo,
            IBalanceRepository balanceRepo,
            IEnumerable<Meta<INotificationSender, NotificationSenderMetadataAttribute>> notificationSenders)
        {
            Contract.RequiresNonNull(ctx, nameof(ctx));
            Contract.RequiresNonNull(cf, nameof(cf));
            Contract.RequiresNonNull(blockRepo, nameof(blockRepo));
            Contract.RequiresNonNull(shareRepo, nameof(shareRepo));
            Contract.RequiresNonNull(balanceRepo, nameof(balanceRepo));
            Contract.RequiresNonNull(notificationSenders, nameof(notificationSenders));

            this.ctx = ctx;
            this.cf = cf;
            this.blockRepo = blockRepo;
            this.shareRepo = shareRepo;
            this.balanceRepo = balanceRepo;
            this.notificationSenders = notificationSenders;
        }

        private readonly IBalanceRepository balanceRepo;
        private readonly IBlockRepository blockRepo;
        private readonly IConnectionFactory cf;
        private readonly IComponentContext ctx;
        private readonly IShareRepository shareRepo;
        private readonly IEnumerable<Meta<INotificationSender, NotificationSenderMetadataAttribute>> notificationSenders;
        private readonly AutoResetEvent stopEvent = new AutoResetEvent(false);
        private ClusterConfig clusterConfig;
        private Thread thread;
        private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

        private async Task ProcessPoolsAsync()
        {
            foreach (var pool in clusterConfig.Pools)
            {
                logger.Info(() => $"Processing payments for pool {pool.Id}");

                try
                {
                    // resolve payout handler
                    var handlerImpl = ctx.Resolve<IEnumerable<Meta<Lazy<IPayoutHandler, CoinMetadataAttribute>>>>()
                        .First(x => x.Value.Metadata.SupportedCoins.Contains(pool.Coin.Type)).Value;

                    var handler = handlerImpl.Value;
                    handler.Configure(clusterConfig, pool);

                    // resolve payout scheme
                    var scheme = ctx.ResolveKeyed<IPayoutScheme>(pool.PaymentProcessing.PayoutScheme);

                    await UpdatePoolBalancesAsync(pool, handler, scheme);
                    await PayoutPoolBalancesAsync(pool, handler);
                }

                catch (Exception ex)
                {
                    logger.Error(ex, () => $"[{pool.Id}] Payment processing failed");
                }
            }
        }

        private async Task UpdatePoolBalancesAsync(PoolConfig pool, IPayoutHandler handler, IPayoutScheme scheme)
        {
            // get pending blockRepo for pool
            var pendingBlocks = cf.Run(con => blockRepo.GetPendingBlocksForPool(con, pool.Id));

            // classify
            var updatedBlocks = await handler.ClassifyBlocksAsync(pendingBlocks);

            if (updatedBlocks.Any())
            {
                foreach (var block in updatedBlocks.OrderBy(x => x.Created))
                {
                    logger.Info(() => $"Processing payments for pool {pool.Id}, block {block.Blockheight}");

                    await cf.RunTxAsync(async (con, tx) =>
                    {
                        if (block.Status == BlockStatus.Confirmed)
                        {
                            // blockchains that do not support block-reward payments via coinbase Tx
                            // must generate balance records for all reward recipients instead
                            await handler.UpdateBlockRewardBalancesAsync(con, tx, block, pool);

                            // update share submitter balances through configured payout scheme 
                            await scheme.UpdateBalancesAsync(con, tx, pool, handler, block);

                            // finally update block status
                            blockRepo.UpdateBlock(con, tx, block);
                        }

                        else if (block.Status == BlockStatus.Orphaned)
                        {
                            blockRepo.DeleteBlock(con, tx, block);
                        }
                    });
                }
            }

            else
                logger.Info(() => $"No updated blocks for {pool.Id}");
        }

        private async Task PayoutPoolBalancesAsync(PoolConfig pool, IPayoutHandler handler)
        {
            var poolBalancesOverMinimum = cf.Run(con =>
                balanceRepo.GetPoolBalancesOverThreshold(con, pool.Id, pool.PaymentProcessing.MinimumPayment));

            if (poolBalancesOverMinimum.Length > 0)
            {
                try
                {
                    await handler.PayoutAsync(poolBalancesOverMinimum);
                }

                catch (Exception ex)
                {
                    await NotifyPayoutFailureAsync(poolBalancesOverMinimum, pool, ex);
                    throw;
                }
            }

            else
                logger.Info(() => $"No balances over configured minimum payout for pool {pool.Id}");
        }

        private async Task NotifyPayoutFailureAsync(Balance[] balances, PoolConfig pool, Exception ex)
        {
            // admin notifications
            if (clusterConfig.Notifications?.Admin?.Enabled == true)
            {
                try
                {
                    var adminEmail = clusterConfig.Notifications.Admin.EmailAddress;

                    var emailSender = notificationSenders
                        .Where(x => x.Metadata.NotificationType == NotificationType.Email)
                        .Select(x => x.Value)
                        .First();

                    await emailSender.NotifyAsync(adminEmail, "Payout Failure Notification", $"Failed to pay out {balances.Sum(x => x.Amount)} {pool.Coin.Type} from pool {pool.Id}: {ex.Message}");
                }

                catch (Exception ex2)
                {
                    logger.Error(ex2);
                }
            }
        }

        #region API-Surface

        public void Configure(ClusterConfig clusterConfig)
        {
            this.clusterConfig = clusterConfig;
        }

        public void Start()
        {
            thread = new Thread(async () =>
            {
                logger.Info(() => "Online");

                var interval = TimeSpan.FromSeconds(
                    clusterConfig.PaymentProcessing.Interval > 0 ? clusterConfig.PaymentProcessing.Interval : 600);

                while (true)
                {
                    try
                    {
                        await ProcessPoolsAsync();
                    }

                    catch (Exception ex)
                    {
                        logger.Error(ex);
                    }

                    var waitResult = stopEvent.WaitOne(interval);

                    // check if stop was signalled
                    if (waitResult)
                        break;
                }
            });

            thread.IsBackground = true;
            thread.Priority = ThreadPriority.AboveNormal;
            thread.Name = "Payment Processing";
            thread.Start();
        }

        public void Stop()
        {
            logger.Info(() => "Stopping ..");

            stopEvent.Set();
            thread.Join();

            logger.Info(() => "Stopped");
        }

        #endregion // API-Surface
    }
}
