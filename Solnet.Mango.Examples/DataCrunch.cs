using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Solnet.Mango.Models;
using Solnet.Programs;
using Solnet.Rpc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Solnet.Mango.Examples
{
    public class MangoAccountInfo
    {
        public string Account { get; set; }

        public decimal Equity { get; set; }

        public decimal MaintenanceHealth { get; set; }

        public decimal InitHealth { get; set; }

        public decimal Leverage { get; set; }

        public decimal AssetsValue { get; set; }

        public decimal LiabilitiesValue { get; set; }
    }

    public class LongShortInfo
    {
        public string Market { get; set; }

        public int AccountLongs { get; set; }

        public int AccountShorts { get; set; }

        public decimal LongNotional { get; set; }

        public decimal ShortNotional { get; set; }
    }

    public class DataCrunch : IRunnableExample
    {

        private readonly IRpcClient _rpcClient;
        private readonly IStreamingRpcClient _streamingRpcClient;
        private readonly ILogger _logger;
        private readonly IMangoClient _mangoClient;
        private readonly MangoProgram _mango;

        public DataCrunch()
        {
            Console.WriteLine($"Initializing {ToString()}");

            //init stuff
            _logger = LoggerFactory.Create(x =>
            {
                x.AddSimpleConsole(o =>
                {
                    o.UseUtcTimestamp = true;
                    o.IncludeScopes = true;
                    o.ColorBehavior = LoggerColorBehavior.Enabled;
                    o.TimestampFormat = "HH:mm:ss ";
                })
                .SetMinimumLevel(LogLevel.Debug);
            }).CreateLogger<IRpcClient>();

             _mango = MangoProgram.CreateMainNet();

            // the clients
            _rpcClient = Rpc.ClientFactory.GetClient("https://citadel.genesysgo.net", _logger);
            _streamingRpcClient = Rpc.ClientFactory.GetStreamingClient("https://citadel.genesysgo.net", _logger);
            _mangoClient = ClientFactory.GetClient(_rpcClient, _streamingRpcClient, _logger, _mango.ProgramIdKey);

        }
        public void Run()
        {
            var accountInfoList = new List<MangoAccountInfo>();
            var longShortInfoList = new List<LongShortInfo>();

            var mangoGroup = _mangoClient.GetMangoGroup(Constants.MangoGroup).ParsedResult;
            mangoGroup.LoadRootBanks(_mangoClient, _logger);
            mangoGroup.LoadPerpMarkets(_mangoClient);

            for (int i = 0; i < mangoGroup.PerpMarketAccounts.Count; i++)
            {
                var longShortInfo = new LongShortInfo();
                if (mangoGroup.PerpetualMarkets[i].Market.Equals(SystemProgram.ProgramIdKey))
                {
                    longShortInfoList.Add(longShortInfo);
                    continue;
                }
                longShortInfo.Market = mangoGroup.PerpetualMarkets[i].Market;
                longShortInfoList.Add(longShortInfo);
            }
            var mangoCache = _mangoClient.GetMangoCache(mangoGroup.MangoCache).ParsedResult;

            var mangoAccounts = _rpcClient.GetProgramAccounts(_mango.ProgramIdKey, dataSize: MangoAccount.Layout.Length);

            if (!mangoAccounts.WasSuccessful)
            {
                Console.WriteLine($"Could not fetch mango accounts. Reason: {mangoAccounts.Reason}.");

                Console.ReadLine();
                return;
            }
            Console.WriteLine($"Successfully fetched {mangoAccounts.Result.Count} mango accounts.");


            for(int i = 0; i < mangoAccounts.Result.Count; i++)
            {
                var mangoAccount = MangoAccount.Deserialize(Convert.FromBase64String(mangoAccounts.Result[i].Account.Data[0]));

                var equity = mangoAccount.ComputeValue(mangoGroup, mangoCache).ToDecimal();
                var leverage = mangoAccount.GetLeverage(mangoGroup, mangoCache).ToDecimal();
                var mHealth = mangoAccount.GetHealth(mangoGroup, mangoCache, HealthType.Maintenance).ToDecimal();
                var iHealth = mangoAccount.GetHealth(mangoGroup, mangoCache, HealthType.Initialization).ToDecimal();
                var assetsVal = mangoAccount.GetAssetsValue(mangoGroup, mangoCache).ToDecimal();
                var liabVal = mangoAccount.GetLiabilitiesValue(mangoGroup, mangoCache).ToDecimal();

                var accountInfo = new MangoAccountInfo
                {
                    Account = mangoAccounts.Result[i].PublicKey,
                    Equity = equity,
                    Leverage = leverage,
                    MaintenanceHealth = mHealth, 
                    InitHealth = iHealth,
                    AssetsValue = assetsVal,
                    LiabilitiesValue = liabVal
                };
                accountInfoList.Add(accountInfo);


                for(int j = 0; j < longShortInfoList.Count; j++)
                {
                    if (longShortInfoList[j].Market == null) continue;
                    var notional = mangoAccount.PerpetualAccounts[j].GetNotionalSize(mangoGroup, mangoCache, mangoGroup.PerpMarketAccounts[j], j);
                    var absNotional = notional < 0 ? notional * -1 : notional;

                    if (notional == 0) continue;
                    if (notional < 0)
                    {
                        longShortInfoList[j].ShortNotional += absNotional;
                        longShortInfoList[j].AccountShorts += 1;
                    }
                    else
                    {
                        longShortInfoList[j].LongNotional += absNotional;
                        longShortInfoList[j].AccountLongs += 1;
                    }
                }
            }

            accountInfoList.Sort(Comparer<MangoAccountInfo>.Create((a, b) => a.Equity.CompareTo(b.Equity)));

            for (int i = 0; i < accountInfoList.Count; i++)
            {
                Console.WriteLine($"{i}/{accountInfoList.Count} Account: {accountInfoList[i].Account} " +
                    $"Value: ${accountInfoList[i].Equity,15:N4}\tLeverage: {accountInfoList[i].Leverage,8:N2}x\tHealth: {accountInfoList[i].MaintenanceHealth,25:N4}\t");
            }

            var now = DateTime.UtcNow.Subtract(new DateTime(1970,1,1)).TotalSeconds;

            var accountsInfoSerBytes = JsonSerializer.Serialize(accountInfoList);

            File.WriteAllText($"mangoaccountsinfo-{now}.json", accountsInfoSerBytes);

            Console.WriteLine($"Successfully processed {mangoAccounts.Result.Count} mango accounts.");

            foreach (var longShortInfo in longShortInfoList)
            {
                if(longShortInfo.Market == null)
                {
                    Console.WriteLine($"Market: NOT ALLOCATED ");
                    continue;
                }
                Console.WriteLine($"Market: {longShortInfo.Market} " +
                    $"\tShort Accounts: {longShortInfo.AccountShorts}" +
                    $"\tLong Accounts: {longShortInfo.AccountLongs}" +
                    $"\tNotional: ${longShortInfo.LongNotional,15:N4}" +
                    $"\tAccounts w/ Positions: {(longShortInfo.AccountShorts + longShortInfo.AccountLongs) / (float) mangoAccounts.Result.Count,8:P2}" +
                    $"\tLong Ratio: {longShortInfo.AccountLongs / (float) (longShortInfo.AccountShorts + longShortInfo.AccountLongs),8:P2}" +
                    $"\tShort Ratio: {longShortInfo.AccountShorts / (float) (longShortInfo.AccountShorts + longShortInfo.AccountLongs),8:P2}");
            }

            var longShortInfoSerBytes = JsonSerializer.Serialize(longShortInfoList);

            File.WriteAllText($"longshortinfo-{now}.json", longShortInfoSerBytes);

            Console.ReadLine();
        }
    }
}
