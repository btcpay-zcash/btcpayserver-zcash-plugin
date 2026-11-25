using System;
using System.Collections.Generic;
using System.Text;
using NBitcoin;
using NBitcoin.DataEncoders;
using Org.BouncyCastle.Crypto.Digests;

namespace BTCPayServer.Plugins.ZCash.Services;

// ZIP 316 permits a UFVK to contain a subset of pools. Comparing its string alone
// would let two stores import different encodings of the same receiving authority.
internal static class ZcashViewingKey
{
    public static HashSet<string> ReceivingComponents(string key, ChainName network, bool walletExport = false)
    {
        if (walletExport && key?.StartsWith("xpub", StringComparison.Ordinal) == true)
        {
            // Stock zkool exports a transparent-only account as a BIP32 xpub.
            var xpub = ExtPubKey.Parse(key, Network.Main);
            return new() { Component(0, xpub.PubKey.ToBytes()) };
        }

        var hrp = network == ChainName.Mainnet ? "uview" : network == ChainName.Regtest ? "uviewregtest" : "uviewtest";
        if (key is null || key.Length > 517 || !key.StartsWith(hrp + "1", StringComparison.OrdinalIgnoreCase) ||
            (key != key.ToLowerInvariant() && key != key.ToUpperInvariant()))
            throw InvalidKey();

        var encoder = new Bech32Encoder(Encoding.ASCII.GetBytes(hrp)) { StrictLength = false, SquashBytes = true };
        var data = encoder.DecodeDataRaw(key, out var encoding);
        // The supported ZIP 316 elements are 65, 128 and 96 bytes, plus a
        // one-byte type and length per element and the 16-byte padded HRP.
        if (encoding != Bech32EncodingType.BECH32M || data.Length is < 48 or > 311)
            throw InvalidKey();
        var canonical = encoder.EncodeData(data, Bech32EncodingType.BECH32M);
        if (!string.Equals(key, canonical, StringComparison.OrdinalIgnoreCase))
            throw InvalidKey();

        var leftLength = Math.Min(64, data.Length / 2);
        var left = data.AsSpan(0, leftLength);
        var right = data.AsSpan(leftLength);
        // Inverse F4Jumble, ZIP 316. BLAKE2b personalizations include round
        // and little-endian chunk number. Each G digest is always 64 bytes.
        for (int round = 1; round >= 0; round--)
        {
            XorDigest(right, left, true, round, 0);
            for (int offset = 0; offset < right.Length; offset += 64)
                XorDigest(left, right.Slice(offset, Math.Min(64, right.Length - offset)), false, round, offset / 64);
        }

        var padding = new byte[16];
        Encoding.ASCII.GetBytes(hrp, padding);
        if (!data.AsSpan(data.Length - 16).SequenceEqual(padding)) throw InvalidKey();
        var items = data.AsSpan(0, data.Length - 16);
        var result = new HashSet<string>();
        int previousType = -1;
        while (!items.IsEmpty)
        {
            if (items.Length < 2) throw InvalidKey();
            int type = items[0], size = items[1];
            if (type <= previousType || size != (type switch { 0 => 65, 2 => 128, 3 => 96, _ => -1 }) ||
                items.Length < 2 + size) throw InvalidKey();
            var bytes = items.Slice(2, size);
            // The shielded spend validating key is shared across external/internal scopes.
            result.Add(Component(type, type == 0 ? bytes[32..] : bytes[..32]));
            previousType = type;
            items = items[(2 + size)..];
        }
        if (previousType < 2) throw InvalidKey();
        return result;
    }

    private static string Component(int type, ReadOnlySpan<byte> key) => type + "/" + Convert.ToHexString(key);
    private static FormatException InvalidKey() => new("Expected a unified full viewing key for this network.");

    private static void XorDigest(ReadOnlySpan<byte> input, Span<byte> target, bool h, int round, int chunk)
    {
        var personal = new byte[16];
        Encoding.ASCII.GetBytes(h ? "UA_F4Jumble_H" : "UA_F4Jumble_G", personal);
        personal[13] = (byte)round;
        personal[14] = (byte)chunk;
        personal[15] = (byte)(chunk >> 8);
        var hash = new byte[h ? target.Length : 64];
        var digest = new Blake2bDigest(null, hash.Length, null, personal);
        digest.BlockUpdate(input);
        digest.DoFinal(hash, 0);
        for (int i = 0; i < target.Length; i++) target[i] ^= hash[i];
    }
}
