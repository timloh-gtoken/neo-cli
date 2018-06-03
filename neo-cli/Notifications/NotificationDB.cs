using Neo.Core;
using Neo.IO.Data.LevelDB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Neo.Implementations.Blockchains.LevelDB;
using Neo.VM;
using Newtonsoft.Json.Linq;
using Neo.SmartContract;
using System.Numerics;
using Neo.Wallets;
using JObject = Neo.IO.Json.JObject;
using Iterator = Neo.IO.Data.LevelDB.Iterator;
using VMArray = Neo.VM.Types.Array;

namespace Neo.Notifications
{
    public static class NotificationsPrefix
    {
        public const byte NP_ADDR = 0x01;
        public const byte NP_CONTRACT = 0x02;

        public const byte NP_BLOCK = 0x03;
        public const byte NP_COUNT = 0x04;

        public const byte NP_CONTRACT_LIST = 0x05;
        public const byte NP_TOKEN = 0x06;
    }

    public class NotificationDB
    {

        private DB db;
        private ReadOptions readOptions = new ReadOptions { FillCache = false };
        private WriteOptions writeOptions = WriteOptions.Default;


        private static NotificationDB _instance;

        public static NotificationDB Instance
        {
            get
            {
                if( _instance == null)
                {
                    _instance = new NotificationDB();
                }

                return _instance;
            }
        }

        private NotificationDB()
        {
            LevelDBBlockchain.ApplicationExecuted += LevelDBBlockchain_ApplicationExecuted;
            String path = Settings.Default.Paths.Notifications;
            db = DB.Open(path, new Options { CreateIfMissing = true });
        }

        public NotificationResult NotificationsForBlock(uint height, string event_type = null)
        {
            NotificationResult nResult = new NotificationResult { current_height = Blockchain.Default.Height, message = "Results for a block", results = new List<JToken>() };
            foreach(byte[] res in IterFind(SliceBuilder.Begin(NotificationsPrefix.NP_BLOCK).Add(height))) {
                JToken t = JToken.Parse(Encoding.Default.GetString(res));
                if (filter_result(t, event_type))
                {
                    nResult.results.Add(t);
                }
            }

            return nResult;
        }

        public NotificationResult NotificationsForContract(UInt160 contract, string event_type = null)
        {
            NotificationResult nResult = new NotificationResult { current_height = Blockchain.Default.Height, message = "Results for contract", results = new List<JToken>() };

            foreach (byte[] res in IterFind(SliceBuilder.Begin(NotificationsPrefix.NP_CONTRACT).Add(contract)))
            {
                JToken t = JToken.Parse(Encoding.Default.GetString(res));
                if (filter_result(t, event_type))
                {
                    nResult.results.Add(t);
                }
            }

            return nResult;
        }

        public NotificationResult NotificationsForAddress(UInt160 address, string event_type = null)
        {
            NotificationResult nResult = new NotificationResult { current_height = Blockchain.Default.Height, message = "Results for contract", results = new List<JToken>() };

            foreach (byte[] res in IterFind(SliceBuilder.Begin(NotificationsPrefix.NP_ADDR).Add(address)))
            {
                JToken t = JToken.Parse(Encoding.Default.GetString(res));
                if (filter_result(t, event_type))
                {
                    nResult.results.Add(t);
                }
            }

            return nResult;
        }

        public NotificationResult GetTokens()
        {
            NotificationResult nResult = new NotificationResult { current_height = Blockchain.Default.Height, message = "Results for tokens", results = new List<JToken>() };

            foreach (byte[] res in IterFind(SliceBuilder.Begin(NotificationsPrefix.NP_TOKEN)))
            {
                nResult.results.Add(JToken.Parse(Encoding.Default.GetString(res)));
            }

            return nResult;
        }


        public void Dispose()
        {
            LevelDBBlockchain.ApplicationExecuted -= LevelDBBlockchain_ApplicationExecuted;
            db.Dispose();
        }

        private bool filter_result(JToken token, string event_type)
        {
            if (event_type == null)
            {
                return true;
            }

            JToken notifyType = token.SelectToken("notify_type");
            if (notifyType != null && notifyType.ToString() == event_type)
            {
                return true;
            }

            return false;

        }

        private List<byte[]> IterFind(Slice prefix)
        {
            List<byte[]> results = new List<byte[]>();
            Iterator it = db.NewIterator(readOptions);
            for (it.Seek(prefix); it.Valid(); it.Next())
            {
                Slice key = it.Key();
                byte[] x = key.ToArray();
                byte[] y = prefix.ToArray();
                if (x.Length < y.Length) break;
                if (!x.Take(y.Length).SequenceEqual(y)) break;
                results.Add(it.Value().ToArray());
            }
            it.Dispose();
            return results;
        }



        private void LevelDBBlockchain_ApplicationExecuted(object sender, ApplicationExecutedEventArgs e)
        {
            // blockchain height isn't updated until after this event is dispatched
            uint blockHeight = Blockchain.Default.Height+1;
            string txid = e.Transaction.Hash.ToString();

            WriteBatch batch = new WriteBatch();

            bool checkedContract = false;

            foreach ( var p in e.Notifications)
            {

                if( !checkedContract)
                {
                    checkedContract = true;
                    bool contractIsSaved = checkContractExists(p.ScriptHash);
                    if( !contractIsSaved)
                    {
                        checkIsNEP5(p.ScriptHash, txid);   
                    }
                }

                try
                {
                    JObject notificationJson = NotificationToJson(p, blockHeight, txid);

                    VMArray states = p.State as VMArray;
                    if( states != null && states.Count > 0)
                    {
                        string notifType = states[0].GetString();

                        switch(notifType) {
                            case "transfer":
                                persistTransfer(p, notificationJson, states, batch, blockHeight);
                                break;

                            case "refund":
                                persistRefund(p, notificationJson, states, batch, blockHeight );
                                break;

                            case "mint":
                                persistMintOrBurn("mint", p, notificationJson, states, batch, blockHeight);
                                break;

                            case "burn":
                                persistMintOrBurn("burn", p, notificationJson, states, batch, blockHeight);
                                break;

                            default:
                                persistNotification(notifType, p, notificationJson, batch, blockHeight);
                                break;
                        }
                    }
                }
                catch (Exception error)
                {
                    Console.WriteLine($"Could not get notification state: {error.ToString()}");
                }
            }

            db.Write(writeOptions, batch);

        }

        private JObject NotificationToJson(NotifyEventArgs n, uint height, string txid)
        {
            JObject notification = new JObject();
            notification["contract"] = n.ScriptHash.ToString();
            notification["block"] = height;
            notification["tx"] = txid;
            return notification;
        }

        private byte[] JObjectToBytes(JObject json)
        {
            return Encoding.Default.GetBytes(json.ToString());
        }


        private uint incrementCount(Slice toIncrement, WriteBatch wb)
        {
            uint currentCount = 0;

            if (db.TryGet(readOptions, toIncrement, out Slice value))
            {
                currentCount = BitConverter.ToUInt32(value.ToArray(), 0);
            }
            currentCount += 1;

            wb.Put(toIncrement, currentCount);

            return currentCount;
        }

        private void checkIsNEP5(UInt160 scriptHash, string txid)
        {
            try
            {
                NEP5Token token = NEP5Token.QueryIsToken(scriptHash, txid);

                if( token != null)
                {
                    Slice tokenStore = SliceBuilder.Begin(NotificationsPrefix.NP_TOKEN).Add(scriptHash.ToArray());
                    if( !db.TryGet(readOptions, tokenStore, out Slice value))
                    {
                        db.Put(writeOptions, tokenStore, JObjectToBytes(token.ToJson()));
                    }
                }

            } catch( Exception )
            {
                // this happens when you ask a non-NEP5 contract about NEP5 details
            }
        }

        private bool checkContractExists(UInt160 scriptHash)
        {
            Slice contract_exists_query = SliceBuilder.Begin(NotificationsPrefix.NP_CONTRACT_LIST).Add(scriptHash.ToArray());
            if( db.TryGet(readOptions, contract_exists_query, out Slice value))
            {

                return true;
            }

            db.Put(writeOptions, contract_exists_query, new byte[1]);

            return false;
        }


        private void persistToAddr(byte[] addr, byte[] notification, WriteBatch wb)
        {
            Slice countKey = SliceBuilder.Begin(NotificationsPrefix.NP_COUNT).Add(addr);

            uint currentCount = incrementCount(countKey, wb);

            Slice notifKey = SliceBuilder.Begin(NotificationsPrefix.NP_ADDR).Add(addr).Add(currentCount);

            wb.Put(notifKey, notification);
        }

        private void persistToBlock(uint blockHeight, byte[] notification, WriteBatch wb)
        {
            Slice countKey = SliceBuilder.Begin(NotificationsPrefix.NP_COUNT).Add(blockHeight);

            uint currentCount = incrementCount(countKey, wb);

            Slice notifKey = SliceBuilder.Begin(NotificationsPrefix.NP_BLOCK).Add(blockHeight).Add(currentCount);

            wb.Put(notifKey, notification);
        }

        private void persistToContract(byte[] contract, byte[] notification, WriteBatch wb)
        {
            Slice countKey = SliceBuilder.Begin(NotificationsPrefix.NP_COUNT).Add(contract);

            uint currentCount = incrementCount(countKey, wb);

            Slice notifKey = SliceBuilder.Begin(NotificationsPrefix.NP_CONTRACT).Add(contract).Add(currentCount);

            wb.Put(notifKey, notification);

        }

        private void persistTransfer(NotifyEventArgs n, JObject nJson, VMArray states, WriteBatch wb, uint height)
        {
            nJson["notify_type"] = "transfer";

            if( states.Count >= 4)
            {

                try
                {

                    string from_addr = "";
                    string to_addr = "";

                    byte[] from_ba = states[1].GetByteArray();
                    byte[] to_ba = states[2].GetByteArray();

                    bool hasFromAddress = from_ba != null && from_ba.Length == 20 ? true : false;

                    if( hasFromAddress)
                    {
                        from_addr = Wallet.ToAddress( new UInt160(from_ba));

                    }

                    to_addr = Wallet.ToAddress(new UInt160(to_ba));
                    BigInteger amt = states[3].GetBigInteger();

                    nJson["addr_from"] = from_addr;
                    nJson["addr_to"] = to_addr;
                    nJson["amount"] = amt.ToString();

                    byte[] output = JObjectToBytes(nJson);

                    persistToAddr(to_ba, output, wb);

                    if( hasFromAddress)
                    {
                        persistToAddr(from_ba, output, wb);
                    }

                    persistToBlock(height, output, wb);
                    persistToContract(n.ScriptHash.ToArray(), output, wb);

                }
                catch (Exception error)
                {
                    Console.WriteLine($"Could not write transfer: {error.ToString()}");
                }

            }
        }

        private void persistRefund(NotifyEventArgs n, JObject nJson, VMArray states, WriteBatch wb, uint height)
        {
            nJson["notify_type"] = "refund";

            if (states.Count >= 3)
            {
                try
                {
                    string to_addr = "";
                    byte[] to_ba = states[1].GetByteArray();
                    to_addr = Wallet.ToAddress(new UInt160(to_ba));
                    BigInteger amt = states[2].GetBigInteger();

                    nJson["addr_to"] = to_addr;
                    nJson["amount"] = amt.ToString();
                    nJson["state"] = null;
                    if( states.Count >= 4)
                    {
                        nJson["asset"] = states[3].GetString();
                    }

                    byte[] output = JObjectToBytes(nJson);

                    persistToAddr(to_ba, output, wb);
                    persistToBlock(height, output, wb);
                    persistToContract(n.ScriptHash.ToArray(), output, wb);

                }
                catch (Exception error)
                {
                    Console.WriteLine($"Could not write transfer: {error.ToString()}");
                }

            }
        }

        private void persistMintOrBurn(string mintOrBurn, NotifyEventArgs n, JObject nJson, VMArray states, WriteBatch wb, uint height)
        {
            nJson["notify_type"] = mintOrBurn;

            if (states.Count >= 3)
            {
                try
                {
                    string to_addr = "";
                    byte[] to_ba = states[1].GetByteArray();
                    to_addr = Wallet.ToAddress(new UInt160(to_ba));
                    BigInteger amt = states[2].GetBigInteger();

                    nJson["addr_to"] = to_addr;
                    nJson["amount"] = amt.ToString();
                    nJson["state"] = null;
                    byte[] output = JObjectToBytes(nJson);

                    persistToAddr(to_ba, output, wb);
                    persistToBlock(height, output, wb);
                    persistToContract(n.ScriptHash.ToArray(), output, wb);

                }
                catch (Exception error)
                {
                    Console.WriteLine($"Could not write transfer: {error.ToString()}");
                }

            }
        }

        private void persistNotification(string notifType, NotifyEventArgs n, JObject nJson, WriteBatch wb, uint height)
        {
            UInt160 contractHash = n.ScriptHash;
            nJson["notify_type"] = notifType;
            nJson["state"] = n.State.ToParameter().ToJson();
            byte[] output = JObjectToBytes(nJson);

            persistToBlock(height, output, wb);
            persistToContract(n.ScriptHash.ToArray(), output, wb);
        }
    }
}
