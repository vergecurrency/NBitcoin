﻿using System;
using System.Collections.Generic;
using System.Text;
using UnKnownKVMap = System.Collections.Generic.SortedDictionary<byte[], byte[]>;
using HDKeyPathKVMap = System.Collections.Generic.SortedDictionary<NBitcoin.PubKey, NBitcoin.RootedKeyPath>;

namespace NBitcoin
{
	public abstract class PSBTCoin
	{
		protected HDKeyPathKVMap hd_keypaths = new HDKeyPathKVMap(new PubKeyComparer());
		protected UnKnownKVMap unknown = new SortedDictionary<byte[], byte[]>(BytesComparer.Instance);
		protected Script redeem_script;
		protected Script witness_script;
		protected readonly PSBT Parent;
		public PSBTCoin(PSBT parent)
		{
			hd_keypaths = new HDKeyPathKVMap(new PubKeyComparer());
			unknown = new UnKnownKVMap(BytesComparer.Instance);
			Parent = parent;
		}

		public SortedDictionary<byte[], byte[]> Unknown
		{
			get
			{
				return unknown;
			}
		}

		public HDKeyPathKVMap HDKeyPaths
		{
			get
			{
				return hd_keypaths;
			}
		}

		public Script RedeemScript
		{
			get
			{
				return redeem_script;
			}
			set
			{
				redeem_script = value;
			}
		}

		public Script WitnessScript
		{
			get
			{
				return witness_script;
			}
			set
			{
				witness_script = value;
			}
		}

		public void AddKeyPath(ExtPubKey extPubKey, KeyPath path)
		{
			if (extPubKey == null)
				throw new ArgumentNullException(nameof(extPubKey));
			if (path == null)
				throw new ArgumentNullException(nameof(path));
			AddKeyPath(extPubKey.ParentFingerprint, extPubKey.PubKey, path);
		}
		public virtual void AddKeyPath(PubKey pubKey, RootedKeyPath rootedKeyPath)
        {
			if (rootedKeyPath == null)
				throw new ArgumentNullException(nameof(rootedKeyPath));
			if (pubKey == null)
				throw new ArgumentNullException(nameof(pubKey));
			hd_keypaths.AddOrReplace(pubKey, rootedKeyPath);

			// Let's try to be smart, if the added key match the scriptPubKey then we are in p2psh p2wpkh
			if (Parent.Settings.IsSmart && redeem_script == null)
			{
				var output = GetCoin();
				if (output != null)
				{
					if (pubKey.WitHash.ScriptPubKey.Hash.ScriptPubKey == output.ScriptPubKey)
					{
						redeem_script = pubKey.WitHash.ScriptPubKey;
					}
				}
			}
		}

		public void AddKeyPath(HDFingerprint fingerprint, PubKey key, KeyPath path)
		{
			if (path == null)
				throw new ArgumentNullException(nameof(path));
		}

		public abstract Coin GetCoin();

		public Coin GetSignableCoin()
		{
			return GetSignableCoin(out _);
		}
		public virtual Coin GetSignableCoin(out string error)
		{
			var coin = GetCoin();
			if (coin == null)
			{
				error = "Impossible to know the TxOut this coin refers to";
				return null;
			}
			if (PayToScriptHashTemplate.Instance.ExtractScriptPubKeyParameters(coin.ScriptPubKey) is ScriptId scriptId)
			{
				if (RedeemScript == null)
				{
					error = "Spending p2sh output but redeem_script is not set";
					return null;
				}

				if (RedeemScript.Hash != scriptId)
				{
					error = "Spending p2sh output but redeem_script is not matching the utxo scriptPubKey";
					return null;
				}

				if (PayToWitTemplate.Instance.ExtractScriptPubKeyParameters2(RedeemScript) is WitProgramParameters prog
					&& prog.NeedWitnessRedeemScript())
				{
					if (WitnessScript == null)
					{
						error = "Spending p2sh-p2wsh output but witness_script is not set";
						return null;
					}
					if (!prog.VerifyWitnessRedeemScript(WitnessScript))
					{
						error = "Spending p2sh-p2wsh output but witness_script does not match redeem_script";
						return null;
					}
					coin = coin.ToScriptCoin(WitnessScript);
					error = null;
					return coin;
				}
				else
				{
					coin = coin.ToScriptCoin(RedeemScript);
					error = null;
					return coin;
				}
			}
			else
			{
				if (RedeemScript != null)
				{
					error = "Spending non p2sh output but redeem_script is set";
					return null;
				}
				if (PayToWitTemplate.Instance.ExtractScriptPubKeyParameters2(coin.ScriptPubKey) is WitProgramParameters prog
					&& prog.NeedWitnessRedeemScript())
				{
					if (WitnessScript == null)
					{
						error = "Spending p2wsh output but witness_script is not set";
						return null;
					}
					if (!prog.VerifyWitnessRedeemScript(WitnessScript))
					{
						error = "Spending p2wsh output but witness_script does not match the scriptPubKey";
						return null;
					}
					coin = coin.ToScriptCoin(WitnessScript);
					error = null;
					return coin;
				}
				else
				{
					error = null;
					return coin;
				}
			}
		}
		/// <summary>
		/// Filter the hd keys which contains a HD Key path matching this masterFingerprint/account key
		/// </summary>
		/// <param name="masterFingerprint">The master root fingerprint</param>
		/// <param name="accountKey">The account key (ie. 49'/0'/0')</param>
		/// <returns>HD Keys matching master root key</returns>
		public IEnumerable<PSBTHDKeyMatch> HDKeysFor(IHDKey accountKey, RootedKeyPath accountKeyPath = null)
		{
			if (accountKey == null)
				throw new ArgumentNullException(nameof(accountKey));
			return HDKeysFor(accountKey, accountKeyPath, accountKey.GetPublicKey().GetHDFingerPrint());
		}
		internal IEnumerable<PSBTHDKeyMatch> HDKeysFor(IHDKey accountKey, RootedKeyPath accountKeyPath, HDFingerprint accountFingerprint)
		{
			accountKey = accountKey.AsHDKeyCache();
			foreach (var hdKey in HDKeyPaths)
			{
				bool matched = false;

				// The case where the fingerprint of the hdkey is exactly equal to the accountKey
				if (hdKey.Value.MasterFingerprint == accountFingerprint)
				{
					// The fingerprint match, but we need to check the public keys, because fingerprint collision is easy to provoke
					if (!hdKey.Value.KeyPath.IsHardenedPath || accountKey.CanDeriveHardenedPath())
					{
						if (accountKey.Derive(hdKey.Value.KeyPath).GetPublicKey() == hdKey.Key)
						{
							yield return CreateHDKeyMatch(accountKey, hdKey.Value.KeyPath, hdKey);
							matched = true;
						}
					}
				}

				// The typical case where accountkey is based on an hardened derivation (eg. 49'/0'/0')
				if (!matched && accountKeyPath?.MasterFingerprint is HDFingerprint mp && hdKey.Value.MasterFingerprint == mp)
				{
					var addressPath = hdKey.Value.KeyPath.GetAddressKeyPath();
					// The cases where addresses are generated on a non-hardened path below it (eg. 49'/0'/0'/0/1)
					if (addressPath.Indexes.Length != 0)
					{
						if (accountKey.Derive(addressPath).GetPublicKey() == hdKey.Key)
						{
							yield return CreateHDKeyMatch(accountKey, addressPath, hdKey);
							matched = true;
						}
					}
					// in some cases addresses are generated on a hardened path below the account key (eg. 49'/0'/0'/0'/1') in which case we
					// need to brute force what the address key path is
					else if (accountKey.CanDeriveHardenedPath()) // We can only do this if we can derive hardened paths
					{
						int addressPathSize = 0;
						var hdKeyIndexes = hdKey.Value.KeyPath.Indexes;
						while (addressPathSize <= hdKey.Value.KeyPath.Indexes.Length)
						{
							var indexes = new uint[addressPathSize];
							Array.Copy(hdKeyIndexes, hdKey.Value.KeyPath.Length - addressPathSize, indexes, 0, addressPathSize);
							addressPath = new KeyPath(indexes);
							if (accountKey.Derive(addressPath).GetPublicKey() == hdKey.Key)
							{
								yield return CreateHDKeyMatch(accountKey, addressPath, hdKey);
								matched = true;
								break;
							}
							addressPathSize++;
						}
					}
				}
			}
		}

		protected abstract PSBTHDKeyMatch CreateHDKeyMatch(IHDKey accountKey, KeyPath addressKeyPath, KeyValuePair<PubKey, RootedKeyPath> kv);
	}
}
