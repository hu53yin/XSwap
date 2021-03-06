﻿using NBitcoin;
using System;
using System.Collections.Generic;
using System.Text;
using NBitcoin.RPC;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Text.RegularExpressions;
using NBitcoin.DataEncoders;

namespace XSwap.CLI
{
	public class SupportedChain
	{
		RPCArgs _Args;
		public SupportedChain(RPCArgs args, ChainInformation info)
		{
			this._Args = args;
			Information = info;
		}

		public SupportedChain()
		{

		}

		public void EnsureIsSetup(bool forceRenew = false)
		{
			if(forceRenew || RPCClient == null)
			{
				RPCClient = _Args.ConfigureRPCClient(Information.Network);
				try
				{
					RPCClient.EstimateFeeRate(2);
				}
				catch(Exception)
				{

					if(!this.Information.IsTest)
						throw new ConfigException("Fee unavaialble, wait for your fullnode to have more fee information and retry again");
				}
			}
		}

		public RPCClient RPCClient
		{
			get; set;
		}

		public ChainInformation Information
		{
			get; set;
		}
	}
	public class ChainUnknownException : Exception
	{
		public ChainUnknownException(string message) : base(message)
		{

		}
	}
	public class SwapFacade
	{
		Repository _Repository;
		private readonly IEnumerable<SupportedChain> _Chains;

		public SwapFacade(IEnumerable<SupportedChain> chains, Repository repository)
		{
			if(chains == null)
				throw new ArgumentNullException(nameof(chains));
			if(repository == null)
				throw new ArgumentNullException(nameof(repository));
			_Chains = chains;
			_Repository = repository;
		}

		public PubKey NewPubkey()
		{
			var key = new Key();
			_Repository.SaveKey(key);
			return key.PubKey;
		}

		public async Task<uint256> WaitOfferAsync(OfferData offer, CancellationToken cancellation = default(CancellationToken))
		{
			var scriptPubKey = offer.CreateScriptPubkey();
			var chain = GetChain(offer.Initiator.Asset.Chain);
			return await WaitConfirmationCoreAsync(chain, scriptPubKey, offer.Initiator.Asset.Amount, 0, cancellation);
		}

		public async Task<bool> WaitOfferTakenAsync(OfferData offerData, CancellationToken cancellation = default(CancellationToken))
		{
			var decodedTxById = new Dictionary<uint256, JObject>();
			var initiator = GetChain(offerData.Initiator.Asset.Chain);

			var redeemHash = offerData.CreateRedeemScript().Hash;

			int offset = 0;
			int takeCount = 10;
			while(true)
			{
				cancellation.ThrowIfCancellationRequested();
				var transactions = await initiator.RPCClient.SendCommandAsync(RPCOperations.listtransactions, "*", takeCount, offset, true).ConfigureAwait(false);
				offset += takeCount;

				//If we reach end of the list, start over
				if(transactions.Result == null || ((JArray)transactions.Result).Count() == 0)
				{
					offset = 0;
					await Task.Delay(1000, cancellation).ConfigureAwait(false);
					continue;
				}

				bool startOver = false;
				//Check if the preimage is present
				foreach(var tx in ((JArray)transactions.Result))
				{
					int confirmation = 0;
					if(tx["confirmations"] != null)
						confirmation = tx["confirmations"].Value<int>();
					//Old transaction, skip
					if(confirmation > 144)
					{
						startOver = true;
						continue;
					}
					//Coinbase we can't have the preimage
					var category = tx["category"]?.Value<string>();
					if(category == "immature" || category == "generate")
						continue;

					var txId = new uint256(tx["txid"].Value<string>());
					JObject txObj = null;
					if(!decodedTxById.TryGetValue(txId, out txObj))
					{
						var getTransaction = await initiator.RPCClient.SendCommandAsync("gettransaction", txId.ToString(), true);
						var decodeRawTransaction = await initiator.RPCClient.SendCommandAsync("decoderawtransaction", getTransaction.Result["hex"]);
						txObj = (JObject)decodeRawTransaction.Result;
						decodedTxById.Add(txId, txObj);
					}
					foreach(var input in txObj["vin"])
					{
						var scriptSig = input["scriptSig"]["asm"].Value<string>();
						var sigParts = scriptSig.Split(' ').ToList();
						// Add the hash in txin if segwit
						if(input["txinwitness"] is JArray scriptWitness)
						{
							sigParts.AddRange(scriptWitness.Select(c => c.Value<string>()));
						}

						foreach(var sigPart in sigParts)
						{
							if(!HexEncoder.IsWellFormed(sigPart))
								continue;
							var preimage = new Preimage(Encoders.Hex.DecodeData(sigPart));
							if(preimage.GetHash() == offerData.Hash)
							{
								_Repository.SavePreimage(preimage);
								return true;
							}
						}
					}
				}

				//If we reach end of the list, start over
				if(startOver)
				{
					offset = 0;
					await Task.Delay(1000, cancellation).ConfigureAwait(false);
					continue;
				}
			}

		}

		private async Task<uint256> WaitConfirmationCoreAsync(SupportedChain chain, Script scriptPubKey, Money amount, int confCount, CancellationToken cancellation)
		{

			var h = await chain.RPCClient.GetBestBlockHashAsync().ConfigureAwait(false);
			await chain.RPCClient.ImportAddressAsync(scriptPubKey).ConfigureAwait(false);
			while(true)
			{
				cancellation.ThrowIfCancellationRequested();
				var unspent = await chain.RPCClient.ListUnspentAsync(confCount, 1000, scriptPubKey.GetDestinationAddress(chain.RPCClient.Network));
				var utxo = unspent.FirstOrDefault(u => u.Amount == amount);
				if(utxo != null)
				{
					_Repository.SaveOffer(scriptPubKey, utxo.OutPoint);
					return utxo.OutPoint.Hash;
				}
				cancellation.WaitHandle.WaitOne(1000);
			}
		}

		SupportedChain GetChain(string chainName)
		{
			var chain = _Chains.Where(c => c.Information.Names.Contains(chainName, StringComparer.OrdinalIgnoreCase)).FirstOrDefault();
			if(chain == null)
				throw new ChainUnknownException($"Chain {chainName} is unknown");
			chain.EnsureIsSetup();
			return chain;
		}

		public async Task<OfferData> ProposeOffer(Proposal proposal, PubKey bobPubKey)
		{
			var key = new Key();
			_Repository.SaveKey(key);

			var initiator = GetChain(proposal.From.Chain);
			var taker = GetChain(proposal.To.Chain);

			var preimage = new Preimage();
			_Repository.SavePreimage(preimage);

			var lockTimes = new[]
			{
				CalculateLockTime(proposal.From, TimeSpan.FromHours(2.0)),
				CalculateLockTime(proposal.To, TimeSpan.FromHours(1.0))
			};
			return new OfferData()
			{
				Initiator = new OfferParty()
				{
					Asset = proposal.From,
					PubKey = key.PubKey
				},
				Taker = new OfferParty()
				{
					Asset = proposal.To,
					PubKey = bobPubKey
				},
				LockTime = await lockTimes[0].ConfigureAwait(false),
				CounterOfferLockTime = await lockTimes[1].ConfigureAwait(false),
				Hash = preimage.GetHash()
			};
		}

		private async Task<LockTime> CalculateLockTime(ChainAsset asset, TimeSpan offset)
		{
			var chain = GetChain(asset.Chain);
			var blockCount = await chain.RPCClient.GetBlockCountAsync().ConfigureAwait(false);
			return blockCount + chain.Information.GetBlockCount(offset);
		}

		private static async Task<uint256> FundAndBroadcast(RPCClient rpcClient, Transaction tx)
		{
			var change = await rpcClient.GetRawChangeAddressAsync().ConfigureAwait(false);
			var funded = await rpcClient.FundRawTransactionAsync(tx, new FundRawTransactionOptions()
			{
				ChangeAddress = change,
				LockUnspents = true,
				IncludeWatching = false
			}).ConfigureAwait(false);

			var result = await rpcClient.SendCommandAsync("signrawtransaction", funded.Transaction.ToHex()).ConfigureAwait(false);
			var txId = await rpcClient.SendCommandAsync("sendrawtransaction", ((JObject)result.Result)["hex"].Value<string>());
			return new uint256(txId.Result.Value<string>());
		}

		public async Task<uint256> BroadcastOffer(OfferData offerData, bool watch)
		{
			var intitator = GetChain(offerData.Initiator.Asset.Chain);
			Transaction tx = new Transaction();
			tx.AddOutput(offerData.CreateOffer());
			if(watch)
				await intitator.RPCClient.ImportAddressAsync(offerData.CreateScriptPubkey()).ConfigureAwait(false);
			return await FundAndBroadcast(intitator.RPCClient, tx).ConfigureAwait(false);
		}

		public async Task<uint256> TakeOffer(OfferData offer)
		{
			var taker = GetChain(offer.Taker.Asset.Chain);
			var initiator = GetChain(offer.Initiator.Asset.Chain);

			var preimage = _Repository.GetPreimage(offer.Hash);
			if(preimage == null)
				throw new InvalidOperationException("Unknown preimage");
			var key = _Repository.GetPrivateKey(offer.Taker.PubKey);
			if(key == null)
				throw new InvalidOperationException("Unknown pubkey");

			var offerOutpoint = _Repository.GetOffer(offer.CreateScriptPubkey());
			if(offerOutpoint == null)
				throw new InvalidOperationException("Unknown offer");

			var destination = await initiator.RPCClient.GetNewAddressAsync().ConfigureAwait(false);
			var tx = new Transaction();
			tx.AddInput(new TxIn(offerOutpoint, offer.TakeOffer(new TransactionSignature(key.Sign(uint256.One), SigHash.All), preimage)));
			tx.AddOutput(new TxOut(offer.Initiator.Asset.Amount, destination));
			var fee = (await GetFeeAsync(initiator).ConfigureAwait(false)).GetFee(tx.GetVirtualSize());
			tx.Outputs[0].Value -= fee;

			var offerCoin = new Coin(offerOutpoint, new TxOut(offer.Initiator.Asset.Amount, offer.CreateScriptPubkey())).ToScriptCoin(offer.CreateRedeemScript());
			var sig = tx.Inputs.AsIndexedInputs().First().Sign(key, offerCoin, SigHash.All);
			tx.Inputs[0].ScriptSig = offer.TakeOffer(sig, preimage);
			await initiator.RPCClient.SendRawTransactionAsync(tx).ConfigureAwait(false);
			return tx.GetHash();
		}

		private async Task<FeeRate> GetFeeAsync(SupportedChain chain)
		{
			try
			{
				return await chain.RPCClient.EstimateFeeRateAsync(2).ConfigureAwait(false);
			}
			catch
			{
				if(chain.Information.IsTest)
					return new FeeRate(Money.Satoshis(50), 1);
				throw;
			}
		}

		public Task<uint256> WaitOfferConfirmationAsync(OfferData offer, CancellationToken cancellation = default(CancellationToken))
		{
			return WaitConfirmationCoreAsync(GetChain(offer.Initiator.Asset.Chain), offer.CreateScriptPubkey(), offer.Initiator.Asset.Amount, 1, cancellation);
		}
	}
}
