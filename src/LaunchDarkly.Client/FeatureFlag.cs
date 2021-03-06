﻿using System;
using System.Collections.Generic;
using Common.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LaunchDarkly.Client
{
    internal class FeatureFlag : IVersionedData
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(FeatureFlag));

        public string Key { get; private set; }
        public int Version { get; set; }
        internal bool On { get; private set; }
        internal List<Prerequisite> Prerequisites { get; private set; }
        internal string Salt { get; private set; }
        internal List<Target> Targets { get; private set; }
        internal List<Rule> Rules { get; private set; }
        internal VariationOrRollout Fallthrough { get; private set; }
        internal int? OffVariation { get; private set; }
        internal List<JToken> Variations { get; private set; }
        public bool Deleted { get; set; }

        [JsonConstructor]
        internal FeatureFlag(string key, int version, bool on, List<Prerequisite> prerequisites, string salt,
            List<Target> targets, List<Rule> rules, VariationOrRollout fallthrough, int? offVariation,
            List<JToken> variations,
            bool deleted)
        {
            Key = key;
            Version = version;
            On = on;
            Prerequisites = prerequisites;
            Salt = salt;
            Targets = targets;
            Rules = rules;
            Fallthrough = fallthrough;
            OffVariation = offVariation;
            Variations = variations;
            Deleted = deleted;
        }


        internal FeatureFlag()
        {
        }

        internal struct EvalResult
        {
            internal JToken Result;
            internal readonly IList<FeatureRequestEvent> PrerequisiteEvents;

            internal EvalResult(JToken result, IList<FeatureRequestEvent> events) : this()
            {
                Result = result;
                PrerequisiteEvents = events;
            }
        }


        internal EvalResult Evaluate(User user, IFeatureStore featureStore, Configuration config)
        {
            IList<FeatureRequestEvent> prereqEvents = new List<FeatureRequestEvent>();
            EvalResult evalResult = new EvalResult(null, prereqEvents);
            if (user == null || user.Key == null)
            {
                Log.WarnFormat("User or user key is null when evaluating flag: {0} returning null",
                    Key);

                return evalResult;
            }

            if (On)
            {
                evalResult.Result = Evaluate(user, featureStore, prereqEvents, config);
                if (evalResult.Result != null)
                {
                    return evalResult;
                }
            }
            evalResult.Result = OffVariationValue;
            return evalResult;
        }

        // Returning either a nil EvalResult or EvalResult.value indicates prereq failure/error.
        private JToken Evaluate(User user, IFeatureStore featureStore, IList<FeatureRequestEvent> events, Configuration config)
        {
            var prereqOk = true;
            if (Prerequisites != null)
            {
                foreach (var prereq in Prerequisites)
                {
                    var prereqFeatureFlag = featureStore.Get(VersionedDataKind.Features, prereq.Key);
                    JToken prereqEvalResult = null;
                    if (prereqFeatureFlag == null)
                    {
                        Log.ErrorFormat("Could not retrieve prerequisite flag: {0} when evaluating: {1}",
                            prereq.Key,
                            Key);
                        return null;
                    }
                    else if (prereqFeatureFlag.On)
                    {
                        prereqEvalResult = prereqFeatureFlag.Evaluate(user, featureStore, events, config);
                        try
                        {
                            JToken variation = prereqFeatureFlag.GetVariation(prereq.Variation);
                            if (prereqEvalResult == null || variation == null || !prereqEvalResult.Equals(variation))
                            {
                                prereqOk = false;
                            }
                        }
                        catch (EvaluationException e)
                        {
                            Log.WarnFormat("Error evaluating prerequisites: {0}",
                                e,
                                Util.ExceptionMessage(e));

                            prereqOk = false;
                        }
                    }
                    else
                    {
                        prereqOk = false;
                    }
                    //We don't short circuit and also send events for each prereq.
                    events.Add(new FeatureRequestEvent(prereqFeatureFlag.Key, EventUser.FromUser(user, config),
                        prereqEvalResult, null, prereqFeatureFlag.Version, prereq.Key));
                }
            }
            if (prereqOk)
            {
                return GetVariation(EvaluateIndex(user, featureStore));
            }
            return null;
        }


        private int? EvaluateIndex(User user, IFeatureStore store)
        {
            // Check to see if targets match
            foreach (var target in Targets)
            {
                foreach (var v in target.Values)
                {
                    if (v.Equals(user.Key))
                    {
                        return target.Variation;
                    }
                }
            }

            // Now walk through the rules and see if any match
            foreach (Rule rule in Rules)
            {
                if (rule.MatchesUser(user, store))
                {
                    return rule.VariationIndexForUser(user, Key, Salt);
                }
            }

            // Walk through the fallthrough and see if it matches
            return Fallthrough.VariationIndexForUser(user, Key, Salt);
        }

        private JToken GetVariation(int? index)
        {
            // If the supplied index is null, then rules didn't match, and we want to return
            // the off variation
            if (index == null)
            {
                return null;
            }
            // If the index doesn't refer to a valid variation, that's an unexpected exception and we will
            // return the default variation
            else if (index >= Variations.Count)
            {
                throw new EvaluationException("Invalid index");
            }
            else
            {
                return Variations[index.Value];
            }
        }

        internal JToken OffVariationValue
        {
            get
            {
                if (!OffVariation.HasValue)
                {
                    return null;
                }

                if (OffVariation.Value >= Variations.Count)
                {
                    throw new EvaluationException("Invalid off variation index");
                }

                return Variations[OffVariation.Value];
            }
        }
    }

    class Rollout
    {
        internal List<WeightedVariation> Variations { get; private set; }
        internal string BucketBy { get; private set; }

        [JsonConstructor]
        internal Rollout(List<WeightedVariation> variations, string bucketBy)
        {
            Variations = variations;
            BucketBy = bucketBy;
        }
    }

    class WeightedVariation
    {
        internal int Variation { get; private set; }
        internal int Weight { get; private set; }

        [JsonConstructor]
        internal WeightedVariation(int variation, int weight)
        {
            Variation = variation;
            Weight = weight;
        }
    }

    class Target
    {
        internal List<string> Values { get; private set; }
        internal int Variation { get; private set; }

        [JsonConstructor]
        internal Target(List<string> values, int variation)
        {
            Values = values;
            Variation = variation;
        }
    }

    class Prerequisite
    {
        internal string Key { get; private set; }
        internal int Variation { get; private set; }

        [JsonConstructor]
        internal Prerequisite(string key, int variation)
        {
            Key = key;
            Variation = variation;
        }
    }

    class EvaluationException : Exception
    {
        public EvaluationException(string message)
            : base(message)
        {
        }
    }
}