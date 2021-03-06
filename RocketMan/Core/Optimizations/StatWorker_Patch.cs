﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RocketMan.Optimizations
{
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
        internal static MethodBase m_GetValueUnfinalized_Transpiler = AccessTools.Method(typeof(StatWorker_GetValueUnfinalized_Hijacked_Patch), "Transpiler");

        internal static Dictionary<int, int> signatures = new Dictionary<int, int>();
        internal static Dictionary<int, Tuple<float, int, int>> cache = new Dictionary<int, Tuple<float, int, int>>(1000);
        internal static List<Tuple<int, int, float>> requests = new List<Tuple<int, int, float>>();

        internal static Dictionary<int, float> expiryCache = new Dictionary<int, float>();
        internal static List<string> messages = new List<string>();

        internal static int counter = 0;
        internal static int ticker = 0;
        internal static int cleanUps = 0;
        internal static int stage = 0;

        internal static void ProcessExpiryCache()
        {
            if (requests.Count == 0)
                return;
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            if (Finder.learning && !Find.TickManager.Paused && Find.TickManager.TickRateMultiplier <= 3f)
                if (counter++ % 20 == 0 && expiryCache.Count != 0)
                {
                    foreach (var unit in expiryCache)
                    {
                        Finder.statExpiry[unit.Key] = (byte)Mathf.Clamp(unit.Value, 0f, 255f);
                        cleanUps++;
                    }
                    expiryCache.Clear();
                }
            while (requests.Count > 0 && stopwatch.ElapsedMilliseconds <= 1)
            {
                unsafe
                {
                    Tuple<int, int, float> request;
                    request = requests.Pop();
                    var statIndex = request.Item1;

                    var deltaT = Mathf.Abs(request.Item2);
                    var deltaX = Mathf.Abs(request.Item3);

                    if (expiryCache.TryGetValue(statIndex, out float value))
                        expiryCache[statIndex] += Mathf.Clamp(Finder.learningRate * (deltaT / 100 - deltaX * deltaT), -5, 5);
                    else
                        expiryCache[statIndex] = Finder.statExpiry[statIndex];
                }
            }
        }

        [Main.OnTickLong]
        public static void CleanCache()
        {
            if (Find.TickManager.TickRateMultiplier <= 3f)
                cache.Clear();
        }

        public static void Dirty(Pawn pawn)
        {
            signatures[pawn.thingIDNumber] = Rand.Int.GetHashCode();
            if (Finder.debug) Log.Message(string.Format("ROCKETMAN: changed signature for pawn {0}", pawn));
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

        internal static float UpdateCache(int key, StatWorker statWorker, StatRequest req, bool applyPostProcess, int tick, Tuple<float, int, int> store)
        {
            var value = statWorker.GetValueUnfinalized(req, applyPostProcess);
            if (Finder.statLogging && !Finder.learning)
            {
                Log.Message(string.Format("ROCKETMAN: state {0} for {1} took {2} with key {3}", statWorker.stat.defName, req.thingInt, tick - (store?.Item2 ?? 0), key));
            }
            else if (Finder.learning)
            {
                requests.Add(new Tuple<int, int, float>(statWorker.stat.index, tick - (store?.Item2 ?? tick), Mathf.Abs(value - (store?.Item1 ?? value))));
                if (Rand.Chance(0.1f))
                {
                    ProcessExpiryCache();
                }
            }
            int signature = -1;
            if (req.HasThing && req.Thing is Pawn pawn && pawn != null && !signatures.TryGetValue(pawn.thingIDNumber, out signature))
            {
                signatures[pawn.thingIDNumber] = signature = Rand.Int.GetHashCode();
            }
            cache[key] = new Tuple<float, int, int>(value, tick, signature);
            return value;
        }

        public static float Replacemant(StatWorker statWorker, StatRequest req, bool applyPostProcess)
        {
            var tick = GenTicks.TicksGame;

            if (true
                && Finder.enabled
                && Current.Game != null
                && tick >= 600)
            {
                int key = Tools.GetKey(statWorker, req, applyPostProcess);
                int signature = -1;
                if (req.HasThing && req.Thing is Pawn pawn && pawn != null && !signatures.TryGetValue(pawn.thingIDNumber, out signature))
                {
                    signatures[pawn.thingIDNumber] = signature = Rand.Int.GetHashCode();
                    return UpdateCache(key, statWorker, req, applyPostProcess, tick, null);
                }
                if (!cache.TryGetValue(key, out var store))
                {
                    return UpdateCache(key, statWorker, req, applyPostProcess, tick, store);
                }
                if (tick - store.Item2 > Finder.statExpiry[statWorker.stat.index] || signature != store.Item3)
                {
                    if (Finder.debug && signature != store.Item3) Log.Message(string.Format("ROCKETMAN: Invalidated pawn cache with old sig:{0} and new {1}", store.Item3, signature));
                    return UpdateCache(key, statWorker, req, applyPostProcess, tick, store);
                }
                return store.Item1;
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
}
