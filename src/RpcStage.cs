using System;
using System.Reflection;
using HarmonyLib;
using Unity.Netcode;

namespace VoiceRateGuard
{
    // Tells if an RPC method runs in the execute stage (the receiver) or in the
    // send stage. A listen host runs some RPC methods in both stages.
    internal static class RpcStage
    {
        const int Execute = 1;

        // Harmony's field accessor. The field's type is a protected enum, so it
        // is read as its underlying int (Harmony allows this for enum fields).
        static AccessTools.FieldRef<NetworkBehaviour, int> read;

        // Returns false if the netcode field is not as expected.
        internal static bool Init()
        {
            FieldInfo field = AccessTools.Field(typeof(NetworkBehaviour), "__rpc_exec_stage");
            if (field == null || !field.FieldType.IsEnum || Enum.GetUnderlyingType(field.FieldType) != typeof(int))
                return false;
            if (Convert.ToInt32(Enum.Parse(field.FieldType, "Execute")) != Execute)
                return false;

            read = AccessTools.FieldRefAccess<NetworkBehaviour, int>(field);
            return true;
        }

        internal static bool IsExecute(NetworkBehaviour behaviour) => read != null && read(behaviour) == Execute;

        internal static void Reset() => read = null;
    }
}
