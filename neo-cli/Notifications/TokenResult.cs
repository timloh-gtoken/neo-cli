using Neo.SmartContract;
using Neo.VM;
using System;
using Neo.IO.Json;

namespace Neo.Notifications
{
    public class NEP5Token
    {

        public string Name { get; set; }
        public string Symbol { get; set; }
        public int Decimals { get; set; }
        public string ScriptHash { get; set; }



        public JObject ToJson()
        {
            JObject json = new JObject();
            json["name"] = Name;
            json["symbol"] = Symbol;
            json["decimals"] = Decimals;
            json["script_hash"] = ScriptHash;
            return json;
        }


        public static NEP5Token QueryIsToken(UInt160 scriptHash)
        {
            byte[] script;
            using (ScriptBuilder sb = new ScriptBuilder())
            {
                sb.EmitAppCall(scriptHash, "decimals");
                sb.EmitAppCall(scriptHash, "name");
                sb.EmitAppCall(scriptHash, "symbol");
                script = sb.ToArray();
            }
            ApplicationEngine engine = ApplicationEngine.Run(script);
            if (engine.State.HasFlag(VMState.FAULT)) throw new ArgumentException();


            NEP5Token result = new NEP5Token { ScriptHash = scriptHash.ToString() };
            result.Symbol = engine.EvaluationStack.Pop().GetString();
            result.Name = engine.EvaluationStack.Pop().GetString();
            result.Decimals = (int)engine.EvaluationStack.Pop().GetBigInteger();


            return result;
        }
    }
}
