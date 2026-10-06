namespace BBTCPayServer.Plugins.ZCash.RPC
{
    public class ZcashEvent
    {
        public string BlockHash { get; set; }
        public string TransactionHash { get; set; }
        public string CryptoCode { get; set; }
        public long? AccountIndex { get; set; }

        public override string ToString()
        {
            return
                $"{CryptoCode} ({AccountIndex}: {(string.IsNullOrEmpty(TransactionHash) ? string.Empty : "Tx Update")}{(string.IsNullOrEmpty(BlockHash) ? string.Empty : "New Block")} ({TransactionHash ?? string.Empty}{BlockHash ?? string.Empty})";
        }
    }
}
