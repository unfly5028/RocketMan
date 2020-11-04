﻿using System;
using HugsLib;
using HarmonyLib;
using RimWorld;
using Verse;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.InteropServices.WindowsRuntime;
using UnityEngine;
using System.CodeDom;
using System.Threading;
using System.Diagnostics;
using UnityEngine.Assertions.Must;
using RimWorld.Planet;

namespace RocketMan
{
    [StaticConstructorOnStartup]
    public partial class Main : ModBase
    {
        public static Action[] onMapComponentsInitializing = new Action[]
        {

        };

        public static Action[] onEarlyInitialize = new Action[]
        {

        };

        public static Action[] onClearCache = new Action[]
        {

        };

        public static Action[] onTick = new Action[]
        {
            () => StatWorker_GetValueUnfinalized_Hijacked_Patch.CleanCache(),
            () => StatWorker_GetValueUnfinalized_Hijacked_Patch.FlushMessages(),
            () => WorldReachability_CanReach_Patch.FlushMessages(),
            () => RocketMod.UpdateExceptions()
        };

        public static Action[] onTickLong = new Action[]
        {
        () => {
                if(!Finder.enableGridRefresh)
                    return;
#if DEBUG
                if(Finder.debug) Log.Message("ROCKETMAN: Refreshing all light grid");
#endif
                Finder.refreshGrid = true;
                Find.CurrentMap.glowGrid.RecalculateAllGlow();
            }
        };


        public static Action[] onDefsLoaded = new Action[]
        {
            () => { Finder.Mod_ReGrowth = new ReGrowthHelper(); },
            () => { Finder.Mod_WallLight = new WallLightHelper(); },
            () => Finder.harmony.PatchAll(),
            () => Finder.rocket.PatchAll(),
            () => RocketMod.UpdateStats(),
            () => RocketMod.UpdateExceptions(),
            () => StatWorker_GetValueUnfinalized_Hijacked_Patch.Initialize()
        };

        public override void MapComponentsInitializing(Map map)
        {
            base.MapComponentsInitializing(map);

            for (int i = 0; i < onMapComponentsInitializing.Length; i++)
            {
                onMapComponentsInitializing[i].Invoke();
            }
        }

        public override void DefsLoaded()
        {
            base.DefsLoaded();

            for (int i = 0; i < onDefsLoaded.Length; i++)
            {
                onDefsLoaded[i].Invoke();
            }
        }

        public override void Tick(int currentTick)
        {
            base.Tick(currentTick);

            if (currentTick % Finder.universalCacheAge != 0) return;

            for (int i = 0; i < onTick.Length; i++)
            {
                onTick[i].Invoke();
            }

            if (currentTick % (Finder.universalCacheAge * 5) != 0) return;

            for (int i = 0; i < onTickLong.Length; i++)
            {
                onTickLong[i].Invoke();
            }
        }

        public override void EarlyInitialize()
        {
            base.EarlyInitialize();

            for (int i = 0; i < onEarlyInitialize.Length; i++)
            {
                onEarlyInitialize[i].Invoke();
            }
        }

        public void ClearCache()
        {
            for (int i = 0; i < onClearCache.Length; i++)
            {
                onClearCache[i].Invoke();
            }
        }

        [HarmonyPatch(typeof(StatWorker), "GetValueUnfinalized", new[] { typeof(StatRequest), typeof(bool) })]
        internal static class StatWorker_GetValueUnfinalized_Interrupt_Patch
        {
            public static HashSet<MethodBase> callingMethods = new HashSet<MethodBase>();

            public static MethodBase m_Interrupt = AccessTools.Method(typeof(StatWorker_GetValueUnfinalized_Interrupt_Patch), "Interrupt");

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return new CodeInstruction(OpCodes.Ldarg_1);
                yield return new CodeInstruction(OpCodes.Ldarg_2);
                yield return new CodeInstruction(OpCodes.Call, m_Interrupt);

                foreach (CodeInstruction code in instructions)
                    yield return code;
            }

            public static void Interrupt(StatWorker statWorker, StatRequest req, bool applyPostProcess)
            {
                if (Finder.learning && Finder.statLogging)
                {
                    StackTrace trace = new StackTrace();
                    StackFrame frame = trace.GetFrame(2);
                    MethodBase method = frame.GetMethod();

                    String handler = method.GetStringHandler();

                    Log.Message(string.Format("ROCKETMAN: called stats.GetUnfinalizedValue from {0}", handler));

                    callingMethods.Add(method);
                }
            }
        }

        [HarmonyPatch]
        internal static class StatWorker_GetValueUnfinalized_Hijacked_Patch
        {
            internal static MethodBase m_GetValueUnfinalized = AccessTools.Method(typeof(StatWorker), "GetValueUnfinalized", new[] { typeof(StatRequest), typeof(bool) });
            internal static MethodBase m_GetValueUnfinalized_Replacemant = AccessTools.Method(typeof(StatWorker_GetValueUnfinalized_Hijacked_Patch), "Replacemant");
            internal static MethodBase m_GetValueUnfinalized_Transpiler = AccessTools.Method(typeof(Main.StatWorker_GetValueUnfinalized_Hijacked_Patch), "Transpiler");

            internal static Dictionary<int, Pair<float, int>> cache = new Dictionary<int, Pair<float, int>>(1000);
            internal static Dictionary<int, List<int>> pawnCachedKeys = new Dictionary<int, List<int>>();

            internal static List<int> pawnsCleanupQueue = new List<int>();

            internal static List<Tuple<int, int, float>> requests = new List<Tuple<int, int, float>>();

            private static ThreadStart starter = new ThreadStart(OffMainThreadProcessing);
            private static Thread worker = null;

            public static void Initialize()
            {
                worker = new Thread(starter);
                worker.Start();
            }

            public static void FlushMessages()
            {
                if (!Finder.debug) return;
                while (messages.Count > 0)
                    Log.Message(messages.Pop());
            }

            internal static Dictionary<int, float> expiryCache = new Dictionary<int, float>();
            internal static List<string> messages = new List<string>();

            internal static int counter = 0;
            internal static int ticker = 0;
            internal static int cleanUps = 0;
            internal static int stage = 0;

            internal static void OffMainThreadProcessing()
            {
                while (true)
                {
                    try
                    {
                        Thread.Sleep((int)Mathf.Clamp(15 - expiryCache.Count, 0, 15));
                        if (Current.Game == null)
                            continue;
                        if (Find.TickManager.Paused)
                            continue;
                        stage = 1;
                        if (Finder.learning)
                        {
                            if (counter++ % 20 == 0 && expiryCache.Count != 0)
                            {
                                foreach (var unit in expiryCache)
                                {
                                    Finder.statExpiry[unit.Key] = (byte)Mathf.Clamp(unit.Value, 0f, 255f);
                                    cleanUps++;
                                }
                                expiryCache.Clear();
                            }
                            stage = 2;
                            if (requests.Count > 0)
                            {
                                var request = requests.Pop();
                                var statIndex = request.Item1;

                                var deltaT = Mathf.Abs(request.Item2);
                                var deltaX = Mathf.Abs(request.Item3);

                                if (expiryCache.TryGetValue(statIndex, out float value))
                                    expiryCache[statIndex] += Mathf.Clamp(Finder.learningRate * (deltaT / 100 - deltaX * deltaT), -5, 5);
                                else
                                    expiryCache[statIndex] = Finder.statExpiry[statIndex];
                            }
                        }
                        stage = 3;
                        while (pawnsCleanupQueue.Count > 0)
                        {
                            var pawnIndex = pawnsCleanupQueue.Pop();
                            if (pawnCachedKeys.ContainsKey(pawnIndex))
                                foreach (var key in pawnCachedKeys[pawnIndex])
                                {
                                    cache.RemoveAll(u => u.Key == key);
                                    cleanUps++;
                                }
                        }
                    }
                    catch (Exception er)
                    {
                        messages.Add(string.Format("ROCKETMAN: error off the main thread in stage {0} with error {1} at {2}", stage, er.Message, er.StackTrace));
                    }
                    finally
                    {
                        if (ticker++ % 128 == 0 && Finder.debug)
                            messages.Add(string.Format("ROCKETMAN: off the main thead cleaned {0} and counted {1}", cleanUps, counter));
                    }
                }
            }

            internal static IEnumerable<MethodBase> TargetMethodsUnfinalized()
            {
                yield return AccessTools.Method(typeof(BeautyUtility), "CellBeauty");
                yield return AccessTools.Method(typeof(BeautyUtility), "AverageBeautyPerceptible");
                yield return AccessTools.Method(typeof(StatExtension), "GetStatValue");
                yield return AccessTools.Method(typeof(StatWorker), "GetValue", new[] { typeof(StatRequest), typeof(bool) });

                foreach (Type type in typeof(StatWorker).AllSubclassesNonAbstract())
                {
                    yield return AccessTools.Method(type, "GetValue", new[] { typeof(StatRequest), typeof(bool) });
                }

                foreach (Type type in typeof(StatPart).AllSubclassesNonAbstract())
                {
                    yield return AccessTools.Method(type, "TransformValue");
                }

                foreach (Type type in typeof(StatExtension).AllSubclassesNonAbstract())
                {
                    yield return AccessTools.Method(type, "GetStatValue");
                    yield return AccessTools.Method(type, "GetStatValueAbstract");
                }
            }

            internal static IEnumerable<MethodBase> TargetMethods()
            {
                var methods = TargetMethodsUnfinalized().Where(m => true
                    && m != null
                    && !m.IsAbstract
                    && !m.DeclaringType.IsAbstract).ToHashSet();

                return methods;
            }

            internal static float UpdateCache(int key, StatWorker statWorker, StatRequest req, bool applyPostProcess, int tick, Pair<float, int> store)
            {
                var value = statWorker.GetValueUnfinalized(req, applyPostProcess);

                if (Finder.statLogging && !Finder.learning)
                {
                    Log.Message(string.Format("ROCKETMAN: state {0} for {1} took {2} with key {3}", statWorker.stat.defName, req.thingInt, tick - store.second, key));
                }
                else if (Finder.learning)
                {
                    requests.Add(new Tuple<int, int, float>(statWorker.stat.index, tick - store.second, Mathf.Abs(value - store.first)));
                }

                if (req.HasThing && req.Thing is Pawn pawn && pawn != null)
                {
                    if (!pawnCachedKeys.TryGetValue(pawn.thingIDNumber, out List<int> keys))
                    {
                        pawnCachedKeys[pawn.thingIDNumber] = (keys = new List<int>());
                    }

                    keys.Add(key);
                }

                cache[key] = new Pair<float, int>(value, tick);
                return value;
            }

            public static void CleanCache()
            {
                cache.Clear();
            }

            public static float Replacemant(StatWorker statWorker, StatRequest req, bool applyPostProcess)
            {
                var tick = GenTicks.TicksGame;

                if (true
                    && Finder.enabled
                    && Current.Game != null
                    && tick >= 600)
                {
                    var key = Tools.GetKey(statWorker, req, applyPostProcess);

                    if (!cache.TryGetValue(key, out var store))
                    {
                        return UpdateCache(key, statWorker, req, applyPostProcess, tick, store);
                    }

                    if (tick - store.Second > Finder.statExpiry[statWorker.stat.index])
                    {
                        return UpdateCache(key, statWorker, req, applyPostProcess, tick, store);
                    }

                    return store.First;
                }
                else
                {
                    return statWorker.GetValueUnfinalized(req, applyPostProcess);
                }
            }

            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return Transpilers.MethodReplacer(instructions, m_GetValueUnfinalized, m_GetValueUnfinalized_Replacemant);
            }
        }

        [HarmonyPatch]
        public static class Pawn_Notify_Dirty
        {
            [HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.Notify_ApparelAdded))]
            [HarmonyPostfix]
            public static void Notify_ApparelAdded_Postfix(Pawn_ApparelTracker __instance, Apparel apparel)
            {
                __instance.pawn.Notify_Dirty();
            }

            [HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.Notify_ApparelRemoved))]
            [HarmonyPostfix]
            public static void Notify_ApparelRemoved_Postfix(Pawn_ApparelTracker __instance, Apparel apparel)
            {
                __instance.pawn.Notify_Dirty();
            }

            [HarmonyPatch(typeof(Pawn), nameof(Pawn.Destroy))]
            [HarmonyPostfix]
            public static void Destroy_Postfix(Pawn __instance)
            {
                __instance.Notify_Dirty();
            }

            [HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.Notify_LostBodyPart))]
            [HarmonyPostfix]
            public static void Notify_LostBodyPart_Postfix(Pawn_ApparelTracker __instance)
            {
                __instance.pawn.Notify_Dirty();
            }

            [HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.ApparelChanged))]
            [HarmonyPostfix]
            public static void Notify_ApparelChanged_Postfix(Pawn_ApparelTracker __instance)
            {
                __instance.pawn.Notify_Dirty();
            }

            [HarmonyPatch(typeof(Pawn), nameof(Pawn.Notify_BulletImpactNearby))]
            [HarmonyPostfix]
            public static void Notify_BulletImpactNearby_Postfix(Pawn __instance)
            {
                __instance.Notify_Dirty();
            }

            [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.Notify_HediffChanged))]
            [HarmonyPostfix]
            public static void Notify_HediffChanged_Postfix(Pawn_HealthTracker __instance)
            {
                __instance.pawn.Notify_Dirty();
            }

            [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.Notify_UsedVerb))]
            [HarmonyPostfix]
            public static void Notify_UsedVerb_Postfix(Pawn_HealthTracker __instance)
            {
                __instance.pawn.Notify_Dirty();
            }
        }

        [HarmonyPatch(typeof(Pawn_TimetableTracker), nameof(Pawn_TimetableTracker.GetAssignment))]
        public static class Pawn_TimetableTracker_GetAssignment_Patch
        {
            static Exception Finalizer(Exception __exception, Pawn_TimetableTracker __instance, int hour, ref TimeAssignmentDef __result)
            {
                if (__exception != null)
                {
                    try
                    {
                        __result = TimeAssignmentDefOf.Anything;
                        __instance.SetAssignment(hour, TimeAssignmentDefOf.Anything);
                    }
                    catch
                    {
                        return __exception;
                    }
                    finally
                    {

                    }
                }

                return null;
            }
        }

    }
}