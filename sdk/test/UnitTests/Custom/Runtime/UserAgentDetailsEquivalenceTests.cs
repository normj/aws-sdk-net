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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Amazon.Runtime.Internal.UserAgent;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins <see cref="UserAgentDetails"/> against a reference copy of its earlier HashSet-based implementation:
    /// ordinal de-duplication, insertion order, trimming, the 1 KB metrics limit, merging and cache matching.
    /// </summary>
    [TestClass]
    public class UserAgentDetailsEquivalenceTests
    {
        [TestMethod]
        public void EmptyDetails()
        {
            var details = new UserAgentDetails();

            Assert.AreEqual(string.Empty, details.GetCustomUserAgentComponents());
            Assert.AreEqual(string.Empty, details.GenerateUserAgentWithMetrics());
            Assert.AreEqual(0, details.TrackedFeatureIds.Count());

            var generated = details.CreateGeneratedUserAgent();
            Assert.AreEqual(0, generated.Components.Length);
            Assert.AreEqual(0, generated.FeatureIds.Length);
            Assert.AreEqual(string.Empty, generated.Value);
            Assert.IsTrue(details.Matches(generated));
            Assert.IsFalse(details.Matches(null));
        }

        [TestMethod]
        public void DuplicatesAreDroppedOrdinallyAndOrderIsKept()
        {
            var details = new UserAgentDetails();
            var reference = new ReferenceUserAgentDetails();
            foreach (var component in new[] { "b/1", "a/1", " b/1 ", "B/1", "a/1", "\tc/1\n", "c/1", "", "   ", null })
            {
                details.AddUserAgentComponent(component);
                reference.AddUserAgentComponent(component);
            }
            foreach (var feature in new[] { UserAgentFeatureId.RETRY_MODE_STANDARD, UserAgentFeatureId.PAGINATOR, UserAgentFeatureId.RETRY_MODE_STANDARD, null })
            {
                details.AddFeature(feature);
                reference.AddFeature(feature);
            }

            Assert.AreEqual("b/1 a/1 B/1 c/1", details.GetCustomUserAgentComponents());
            AssertEquivalent(reference, details);
        }

        [TestMethod]
        public void TrackedFeatureIdsIsALiveView()
        {
            var details = new UserAgentDetails();
            var view = details.TrackedFeatureIds;
            details.AddFeature(UserAgentFeatureId.PAGINATOR);
            details.AddFeature(UserAgentFeatureId.RETRY_MODE_ADAPTIVE);

            CollectionAssert.AreEqual(
                new[] { UserAgentFeatureId.PAGINATOR.Value, UserAgentFeatureId.RETRY_MODE_ADAPTIVE.Value },
                view.ToArray());
        }

        [TestMethod]
        public void MetricsAreTruncatedAtTheLastCommaWithin1024Bytes()
        {
            var details = new UserAgentDetails();
            var reference = new ReferenceUserAgentDetails();
            details.AddUserAgentComponent("ua/1.0");
            reference.AddUserAgentComponent("ua/1.0");

            // Feature ids are plain strings underneath, so long and non-ASCII ones exercise the byte limit.
            foreach (var feature in AllFeatureIds().Concat(Enumerable.Range(0, 400).Select(i => "\u00e9" + i)))
            {
                details.AddFeatureValue(feature);
                reference.AddFeatureValue(feature);
            }

            AssertEquivalent(reference, details);
            Assert.IsTrue(Encoding.UTF8.GetByteCount(details.GenerateUserAgentWithMetrics()) <= "ua/1.0 ".Length + 1024);
        }

        [TestMethod]
        public void MergeFromMatchesReference()
        {
            var source = new UserAgentDetails();
            var referenceSource = new ReferenceUserAgentDetails();
            foreach (var c in new[] { "ft/paginator", "x/1" })
            {
                source.AddUserAgentComponent(c);
                referenceSource.AddUserAgentComponent(c);
            }
            foreach (var f in new[] { UserAgentFeatureId.PAGINATOR, UserAgentFeatureId.S3_TRANSFER })
            {
                source.AddFeature(f);
                referenceSource.AddFeature(f);
            }

            var target = new UserAgentDetails();
            var referenceTarget = new ReferenceUserAgentDetails();
            target.AddUserAgentComponent("ua/1.0");
            referenceTarget.AddUserAgentComponent("ua/1.0");
            target.AddFeature(UserAgentFeatureId.S3_TRANSFER);
            referenceTarget.AddFeature(UserAgentFeatureId.S3_TRANSFER);

            // Merging twice, as happens on a retry, adds nothing the second time.
            for (int i = 0; i < 2; i++)
            {
                target.MergeFrom(source);
                referenceTarget.MergeFrom(referenceSource);
                AssertEquivalent(referenceTarget, target);
            }

            // Merging an empty source changes nothing.
            target.MergeFrom(new UserAgentDetails());
            referenceTarget.MergeFrom(new ReferenceUserAgentDetails());
            AssertEquivalent(referenceTarget, target);
        }

        [TestMethod]
        public void MatchesComparesValuesAndOrder()
        {
            var details = new UserAgentDetails();
            details.AddUserAgentComponent("a/1");
            details.AddUserAgentComponent("b/1");
            details.AddFeature(UserAgentFeatureId.PAGINATOR);
            var generated = details.CreateGeneratedUserAgent();
            Assert.IsTrue(details.Matches(generated));

            var reordered = new UserAgentDetails();
            reordered.AddUserAgentComponent("b/1");
            reordered.AddUserAgentComponent("a/1");
            reordered.AddFeature(UserAgentFeatureId.PAGINATOR);
            Assert.IsFalse(reordered.Matches(generated));

            var extraFeature = new UserAgentDetails();
            extraFeature.AddUserAgentComponent("a/1");
            extraFeature.AddUserAgentComponent("b/1");
            extraFeature.AddFeature(UserAgentFeatureId.PAGINATOR);
            extraFeature.AddFeature(UserAgentFeatureId.S3_TRANSFER);
            Assert.IsFalse(extraFeature.Matches(generated));

            var noFeatures = new UserAgentDetails();
            noFeatures.AddUserAgentComponent("a/1");
            noFeatures.AddUserAgentComponent("b/1");
            Assert.IsFalse(noFeatures.Matches(generated));
        }

        [TestMethod]
        public void RandomSequencesMatchReference()
        {
            var components = new[] { "ua/1.0", "md/ClientAsync", "md/ClientSync", "cfg/init-coll#0", " app/x ", "APP/X", "app/x", "", " ", null };
            var features = AllFeatureIds().Take(12).ToArray();
            var random = new Random(1234);

            for (int run = 0; run < 200; run++)
            {
                var details = new UserAgentDetails();
                var reference = new ReferenceUserAgentDetails();
                var other = new UserAgentDetails();
                var referenceOther = new ReferenceUserAgentDetails();

                int operations = random.Next(0, 25);
                for (int op = 0; op < operations; op++)
                {
                    switch (random.Next(5))
                    {
                        case 0:
                        case 1:
                            var c = components[random.Next(components.Length)];
                            details.AddUserAgentComponent(c);
                            reference.AddUserAgentComponent(c);
                            break;
                        case 2:
                        case 3:
                            var f = features[random.Next(features.Length)];
                            details.AddFeatureValue(f);
                            reference.AddFeatureValue(f);
                            break;
                        default:
                            var oc = components[random.Next(components.Length)];
                            var of = features[random.Next(features.Length)];
                            other.AddUserAgentComponent(oc);
                            referenceOther.AddUserAgentComponent(oc);
                            other.AddFeatureValue(of);
                            referenceOther.AddFeatureValue(of);
                            details.MergeFrom(other);
                            reference.MergeFrom(referenceOther);
                            break;
                    }
                }

                AssertEquivalent(reference, details);
            }
        }

        private static void AssertEquivalent(ReferenceUserAgentDetails expected, UserAgentDetails actual)
        {
            Assert.AreEqual(expected.GetCustomUserAgentComponents(), actual.GetCustomUserAgentComponents());
            Assert.AreEqual(expected.GenerateUserAgentWithMetrics(), actual.GenerateUserAgentWithMetrics());
            CollectionAssert.AreEqual(expected.TrackedFeatureIds.ToArray(), actual.TrackedFeatureIds.ToArray());

            var generated = actual.CreateGeneratedUserAgent();
            CollectionAssert.AreEqual(expected.Components, generated.Components);
            CollectionAssert.AreEqual(expected.TrackedFeatureIds.ToArray(), generated.FeatureIds);
            Assert.AreEqual(expected.GenerateUserAgentWithMetrics(), generated.Value);
            Assert.IsTrue(actual.Matches(generated));
        }

        private static IEnumerable<string> AllFeatureIds() =>
            typeof(UserAgentFeatureId)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(f => f.FieldType == typeof(UserAgentFeatureId))
                .Select(f => ((UserAgentFeatureId)f.GetValue(null)).Value);

        /// <summary>
        /// The HashSet-based implementation of <see cref="UserAgentDetails"/> before it moved to lists.
        /// </summary>
        private sealed class ReferenceUserAgentDetails
        {
            private const int MaxSizeBytes = 1024;
            private readonly HashSet<string> _trackedFeatureIds = new HashSet<string>();
            private readonly HashSet<string> _userAgentCustomComponents = new HashSet<string>();

            public IEnumerable<string> TrackedFeatureIds => _trackedFeatureIds;

            public string[] Components => _userAgentCustomComponents.ToArray();

            public void AddUserAgentComponent(string component)
            {
                if (!string.IsNullOrWhiteSpace(component))
                {
                    _userAgentCustomComponents.Add(component.Trim());
                }
            }

            public void AddFeature(UserAgentFeatureId featureId)
            {
                if (featureId == null) return;
                _trackedFeatureIds.Add(featureId.Value);
            }

            public void AddFeatureValue(string value) => _trackedFeatureIds.Add(value);

            public string GetCustomUserAgentComponents() => string.Join(" ", _userAgentCustomComponents);

            public string GenerateUserAgentWithMetrics()
            {
                var customComponents = string.Join(" ", _userAgentCustomComponents);
                if (_trackedFeatureIds.Count == 0)
                {
                    return customComponents;
                }
                return string.Concat(customComponents, " ", GenerateMetricsUserAgent());
            }

            public void MergeFrom(ReferenceUserAgentDetails source)
            {
                if (source._userAgentCustomComponents.Count > 0)
                {
                    AddUserAgentComponent(source.GetCustomUserAgentComponents());
                }
                foreach (var featureId in source._trackedFeatureIds)
                {
                    _trackedFeatureIds.Add(featureId);
                }
            }

            private string GenerateMetricsUserAgent()
            {
                var metricsString = "m/" + string.Join(",", _trackedFeatureIds);
                if (Encoding.UTF8.GetByteCount(metricsString) <= MaxSizeBytes)
                {
                    return metricsString;
                }

                byte[] bytes = Encoding.UTF8.GetBytes(metricsString);
                var cutOffIndex = MaxSizeBytes;
                while (cutOffIndex > 0 && bytes[cutOffIndex - 1] != ',')
                {
                    cutOffIndex--;
                }

                return Encoding.UTF8.GetString(bytes, 0, cutOffIndex - 1);
            }
        }
    }

    internal static class UserAgentDetailsTestExtensions
    {
        /// <summary>
        /// Adds a feature id by its string value, through the public AddFeature path.
        /// </summary>
        public static void AddFeatureValue(this UserAgentDetails details, string value)
        {
            var ctor = typeof(UserAgentFeatureId).GetConstructor(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                null, new[] { typeof(string) }, null);
            details.AddFeature((UserAgentFeatureId)ctor.Invoke(new object[] { value }));
        }
    }
}
