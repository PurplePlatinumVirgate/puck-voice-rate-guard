using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

// Dedicated server: RelayGuard and DataGuard.
// Game client or listen host: ReceiveGuard, DataGuard, ReceiveDataGuard and StopGuard.
// RelayGuard and ReceiveGuard also apply StartLimit.

namespace VoiceRateGuard
{
    public class VoiceRateGuardMod : IPuckPlugin
    {
        public const string ModId      = "pw.r.voiceRateGuard";
        public const string ModName    = "voice_rate_guard";
        public const string ModVersion = "0.3.0";
        public const string GameBuild  = "B1235";

        static Harmony harmony;
        static GameObject ticker;
        static string role;

        public bool OnEnable()
        {
            try
            {
                if (!RpcStage.Init())
                {
                    Debug.LogError("[voice_rate_guard] netcode RPC stage not found; not patching (game build changed?)");
                    return false;
                }

                var patches = new List<(MethodInfo target, Type guard)>();
                string action;

                // Exact argument types: a changed signature gives null, not a
                // wrong overload.
                MethodInfo dataIn = AccessTools.Method(typeof(PlayerVoiceRecorder),
                    nameof(PlayerVoiceRecorder.Client_VoiceDataRpc), new[] { typeof(byte[]), typeof(RpcParams) });

                if (ApplicationManager.IsDedicatedGameServer)
                {
                    role = "server";
                    patches.Add((AccessTools.Method(typeof(PlayerVoiceRecorder),
                        nameof(PlayerVoiceRecorder.Client_RequestVoiceStartRpc), new[] { typeof(uint), typeof(RpcParams) }),
                        typeof(RelayGuard)));
                    patches.Add((dataIn, typeof(DataGuard)));
                    action = "are not relayed";
                }
                else
                {
                    role = "player";
                    patches.Add((AccessTools.Method(typeof(PlayerVoiceRecorder),
                        nameof(PlayerVoiceRecorder.Server_VoiceStartRpc), new[] { typeof(uint) }),
                        typeof(ReceiveGuard)));
                    patches.Add((dataIn, typeof(DataGuard)));
                    patches.Add((AccessTools.Method(typeof(PlayerVoiceRecorder),
                        nameof(PlayerVoiceRecorder.Server_VoiceDataRpc), new[] { typeof(byte[]) }),
                        typeof(ReceiveDataGuard)));
                    patches.Add((AccessTools.Method(typeof(PlayerVoiceRecorder),
                        nameof(PlayerVoiceRecorder.Server_VoiceStopRpc), Type.EmptyTypes),
                        typeof(StopGuard)));
                    action = "are ignored";
                }

                // ReceiveDataGuard reads this private field.
                if (AccessTools.Field(typeof(PlayerVoiceRecorder), "audioQueue")?.FieldType != typeof(ConcurrentQueue<float>))
                {
                    Debug.LogError("[voice_rate_guard] " + role + " role: voice queue field not found; not patching (game build changed?)");
                    return false;
                }

                foreach (var patch in patches)
                {
                    if (patch.target == null)
                    {
                        Debug.LogError("[voice_rate_guard] " + role + " role: target method not found; not patching (game build changed?)");
                        return false;
                    }
                }

                // Harmony binds each Prefix parameter by name (the original
                // parameters, __instance, ___field). A Prefix that returns
                // false skips the original method.
                harmony = new Harmony(ModId);
                foreach (var patch in patches)
                {
                    // Run before any other mod's prefix on this method, so no
                    // other prefix sees the value first.
                    var prefix = new HarmonyMethod(patch.guard, "Prefix") { priority = Priority.First };
                    harmony.Patch(patch.target, prefix: prefix);
                }

                StartTicker();

                Debug.Log(
                    "[voice_rate_guard] enabled v" + ModVersion + " (" + GameBuild + ", " + role + " role): " +
                    "voice starts with sampleRate outside " + SampleRateRange.Min + "-" +
                    SampleRateRange.Max + " Hz, more than " + StartLimit.StartsPerSecond +
                    " voice starts per second, and malformed or excessive voice packets " + action);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[voice_rate_guard] OnEnable failed: " + e);
                SafeShutdown();
                return false;
            }
        }

        // Always returns true: Puck does not call OnDisable again after false,
        // so a failed step is logged, and the teardown still runs.
        public bool OnDisable()
        {
            int starts = RelayGuard.BlockedTotal + ReceiveGuard.BlockedTotal + StartLimit.BlockedTotal;
            int packets = DataGuard.BlockedTotal + ReceiveDataGuard.BlockedTotal;
            // Write every pending count before the logs are cleared.
            try { ThrottledLog.FlushAll(double.MaxValue); } catch (Exception e) { GuardLog.Error("[voice_rate_guard] log flush failed: " + e); }
            SafeShutdown();
            try
            {
                Debug.Log("[voice_rate_guard] disabled (" + (role ?? "no") + " role); stopped " +
                    starts + " voice start(s) and " + packets + " voice packet(s) this run");
            }
            catch (Exception e)
            {
                GuardLog.Error("[voice_rate_guard] OnDisable log failed: " + e);
            }
            return true;
        }

        // One hidden object for the whole run. A failure here does not stop
        // the guards: only the delayed log counts are lost.
        static void StartTicker()
        {
            try
            {
                if (ticker) return;
                ticker = new GameObject("voice_rate_guard");
                ticker.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(ticker);
                ticker.AddComponent<GuardTicker>();
            }
            catch (Exception e)
            {
                GuardLog.Error("[voice_rate_guard] log timer not started; pending counts are written only with the next line: " + e);
            }
        }

        // Each step has its own try, so one failure does not skip the others.
        static void SafeShutdown()
        {
            try { if (ticker) UnityEngine.Object.Destroy(ticker); } catch (Exception e) { GuardLog.Error("[voice_rate_guard] log timer stop failed: " + e); }
            ticker = null;
            try { harmony?.UnpatchSelf(); } catch (Exception e) { GuardLog.Error("[voice_rate_guard] unpatch failed: " + e); }
            harmony = null;
            RelayGuard.Reset();
            ReceiveGuard.Reset();
            DataGuard.Reset();
            ReceiveDataGuard.Reset();
            StartLimit.Reset();
            RpcStage.Reset();
        }
    }
}
