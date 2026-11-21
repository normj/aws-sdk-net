/*
 * Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License").
 * You may not use this file except in compliance with the License.
 * A copy of the License is located at
 *
 *  http://aws.amazon.com/apache2.0
 *
 * or in the "license" file accompanying this file. This file is distributed
 * on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the License for the specific language governing
 * permissions and limitations under the License.
 */

using System.Text;
using System.Collections.Generic;

namespace Amazon.Runtime.Internal.UserAgent
{
    /// <summary>
    /// Represents the User Agent details, including tracked features under User Agent 2.1 (UA2.1).
    /// This class ensures that feature metrics are collected efficiently and the final
    /// User-Agent string is generated only when needed.
    /// </summary>
    public class UserAgentDetails
    {
        private const int MaxSizeBytes = 1024; // 1 KB size limit
        // Distinct values in insertion order. A request adds only a handful of each, so a list with an ordinal
        // linear search is cheaper than hashing every value, and the lists are only allocated when first used
        // (most request objects never add anything).
        private List<string> _trackedFeatureIds;
        private List<string> _userAgentCustomComponents;

        /// <summary>
        /// Gets the list of tracked feature IDs.
        /// </summary>
        public IEnumerable<string> TrackedFeatureIds => _trackedFeatureIds ??= new List<string>();

        /// <summary>
        /// Adds a component to the user-agent string. 
        /// </summary>
        /// <param name="component">The user-agent component to append.</param>
        public void AddUserAgentComponent(string component)
        {
            if (!string.IsNullOrWhiteSpace(component))
            {
                AddDistinct(ref _userAgentCustomComponents, component.Trim());
            }
        }

        /// <summary>
        /// Adds a feature metric to be included in the User-Agent string.
        /// Duplicate entries are ignored.
        /// </summary>
        /// <param name="featureId">The feature metric ID to track.</param>
        public void AddFeature(UserAgentFeatureId featureId)
        {
            if (featureId == null) return;
            AddDistinct(ref _trackedFeatureIds, featureId.Value);
        }

        /// <summary>
        /// Returns the user agent components that were explicitly added via AddUserAgentComponent,
        /// </summary>
        public string GetCustomUserAgentComponents()
        {
            return Join(" ", _userAgentCustomComponents);
        }

        /// <summary>
        /// Appends the metrics user-agent to the existing user-agent and returns the full string.
        /// </summary>
        /// <returns>The final user-agent string including metrics data.</returns>
        public string GenerateUserAgentWithMetrics()
        {
            var customComponents = Join(" ", _userAgentCustomComponents);
            if (Count(_trackedFeatureIds) == 0)
            {
                return customComponents;
            }
            return string.Concat(customComponents, " ", GenerateMetricsUserAgent());
        }

        /// <summary>
        /// Creates an entry holding <see cref="GenerateUserAgentWithMetrics"/> and the components and features
        /// (in order) it was generated from.
        /// </summary>
        internal GeneratedUserAgent CreateGeneratedUserAgent()
        {
            var components = ToArray(_userAgentCustomComponents);
            var featureIds = ToArray(_trackedFeatureIds);
            return new GeneratedUserAgent(components, featureIds, GenerateUserAgentWithMetrics());
        }

        /// <summary>
        /// Returns true if <paramref name="cached"/> was generated from the same components and features
        /// (same values in the same order) as this instance.
        /// </summary>
        internal bool Matches(GeneratedUserAgent cached)
        {
            return cached != null &&
                SequenceEqual(_userAgentCustomComponents, cached.Components) &&
                SequenceEqual(_trackedFeatureIds, cached.FeatureIds);
        }

        private static bool SequenceEqual(List<string> list, string[] values)
        {
            if (Count(list) != values.Length)
                return false;

            for (int i = 0; i < values.Length; i++)
            {
                if (!string.Equals(list[i], values[i], System.StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Adds <paramref name="value"/> to <paramref name="list"/> unless an ordinally equal value is already in it,
        /// creating the list on first use. Matches <see cref="HashSet{T}.Add"/> with the default string comparer.
        /// </summary>
        private static void AddDistinct(ref List<string> list, string value)
        {
            if (list == null)
            {
                list = new List<string>();
            }
            else
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (string.Equals(list[i], value, System.StringComparison.Ordinal))
                        return;
                }
            }

            list.Add(value);
        }

        private static int Count(List<string> list) => list == null ? 0 : list.Count;

        private static string[] ToArray(List<string> list) => Count(list) == 0 ? new string[0] : list.ToArray();

        private static string Join(string separator, List<string> list) => Count(list) == 0 ? string.Empty : string.Join(separator, list);

        /// <summary>
        /// Copies the custom components and tracked features of <paramref name="source"/> into this instance.
        /// Equivalent to <c>AddUserAgentComponent(source.GetCustomUserAgentComponents())</c> followed by
        /// <c>AddFeature</c> for each of <c>source.TrackedFeatureIds</c>.
        /// </summary>
        internal void MergeFrom(UserAgentDetails source)
        {
            if (Count(source._userAgentCustomComponents) > 0)
            {
                AddUserAgentComponent(source.GetCustomUserAgentComponents());
            }
            var sourceFeatureIds = source._trackedFeatureIds;
            for (int i = 0; i < Count(sourceFeatureIds); i++)
            {
                AddDistinct(ref _trackedFeatureIds, sourceFeatureIds[i]);
            }
        }

        /// <summary>
        /// Generates the User-Agent metrics string.
        /// Ensures the final string does not exceed 1024 bytes.
        /// </summary>
        /// <returns>The formatted User-Agent metrics string.</returns>
        private string GenerateMetricsUserAgent()
        {
            if (Count(_trackedFeatureIds) == 0)
            {
                return string.Empty;
            }
            var metricsString = "m/" + string.Join(",", _trackedFeatureIds);
            metricsString = TruncateToSize(metricsString);

            return metricsString;
        }

        /// <summary>
        /// Truncates a comma-separated string to ensure it fits within a given byte limit.
        /// </summary>
        /// <param name="input">The input string.</param>
        /// <returns>A truncated version of the string within the byte limit.</returns>
        private static string TruncateToSize(string input)
        {
            if (Encoding.UTF8.GetByteCount(input) <= MaxSizeBytes)
            {
                return input;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(input);
            var cutOffIndex = MaxSizeBytes;
            while (cutOffIndex > 0 && bytes[cutOffIndex - 1] != ',')
            {
                cutOffIndex--;
            }

            return Encoding.UTF8.GetString(bytes, 0, cutOffIndex - 1); // Remove last comma
        }
    }

    /// <summary>
    /// A generated User-Agent string together with the ordered components and feature ids it was generated from.
    /// </summary>
    internal sealed class GeneratedUserAgent
    {
        internal GeneratedUserAgent(string[] components, string[] featureIds, string value)
        {
            Components = components;
            FeatureIds = featureIds;
            Value = value;
        }

        internal string[] Components { get; }
        internal string[] FeatureIds { get; }
        internal string Value { get; }
    }
}
