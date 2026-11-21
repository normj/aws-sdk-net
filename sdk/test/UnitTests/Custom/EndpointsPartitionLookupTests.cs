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
using Amazon.Runtime;
using Amazon.Runtime.Internal.Endpoints.StandardLibrary;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins what <see cref="Fn.Partition(string)"/> returns for direct, regex and default matches,
    /// that every call gets its own mutable copy, and that <see cref="Partition.LoadPartitions(string)"/>
    /// replaces the data seen by later lookups.
    /// </summary>
    [TestClass]
    public class EndpointsPartitionLookupTests
    {
        [TestMethod]
        [DataRow("us-west-2", "aws", "amazonaws.com", "api.aws", "us-east-1")]
        [DataRow("aws-global", "aws", "amazonaws.com", "api.aws", "us-east-1")]
        [DataRow("cn-north-1", "aws-cn", "amazonaws.com.cn", "api.amazonwebservices.com.cn", "cn-northwest-1")]
        [DataRow("us-gov-west-1", "aws-us-gov", "amazonaws.com", "api.aws", "us-gov-west-1")]
        [DataRow("us-iso-east-1", "aws-iso", "c2s.ic.gov", "api.aws.ic.gov", "us-iso-east-1")]
        // Not in the region list: matched by the partition's region regex.
        [DataRow("us-west-99", "aws", "amazonaws.com", "api.aws", "us-east-1")]
        [DataRow("cn-west-9", "aws-cn", "amazonaws.com.cn", "api.amazonwebservices.com.cn", "cn-northwest-1")]
        [DataRow("us-gov-north-7", "aws-us-gov", "amazonaws.com", "api.aws", "us-gov-west-1")]
        [DataRow("us-isob-west-3", "aws-iso-b", "sc2s.sgov.gov", "api.aws.scloud", "us-isob-east-1")]
        // No region or regex match: the default (aws) partition.
        [DataRow("not-a-region", "aws", "amazonaws.com", "api.aws", "us-east-1")]
        [DataRow("", "aws", "amazonaws.com", "api.aws", "us-east-1")]
        [DataRow("US-WEST-2", "aws", "amazonaws.com", "api.aws", "us-east-1")]
        public void PartitionByRegion(string region, string name, string dnsSuffix, string dualStackDnsSuffix, string implicitGlobalRegion)
        {
            var partition = Fn.Partition(region);

            Assert.AreEqual(name, partition.name);
            Assert.AreEqual(dnsSuffix, partition.dnsSuffix);
            Assert.AreEqual(dualStackDnsSuffix, partition.dualStackDnsSuffix);
            Assert.IsTrue(partition.supportsFIPS);
            Assert.IsTrue(partition.supportsDualStack);
            Assert.AreEqual(implicitGlobalRegion, partition.implicitGlobalRegion);
            Assert.AreEqual(name, Fn.GetAttr(partition, "name"));
        }

        [TestMethod]
        public void NullRegionThrowsArgumentNullException()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => Fn.Partition(null));
        }

        [TestMethod]
        public void EachCallReturnsAnIndependentMutableCopy()
        {
            var first = Fn.Partition("us-west-2");
            var second = Fn.Partition("us-west-2");
            Assert.AreNotSame(first, second);

            first.name = "changed";
            first.dnsSuffix = "example.com";
            first["extra"] = 1;

            Assert.AreEqual("aws", second.name);
            var third = Fn.Partition("us-west-2");
            Assert.AreEqual("aws", third.name);
            Assert.AreEqual("amazonaws.com", third.dnsSuffix);
            Assert.IsNull(third["extra"]);

            // Copies made through the regex and default paths are independent too.
            var regex = Fn.Partition("cn-west-9");
            regex.name = "changed";
            Assert.AreEqual("aws-cn", Fn.Partition("cn-west-9").name);
            var fallback = Fn.Partition("not-a-region");
            fallback.name = "changed";
            Assert.AreEqual("aws", Fn.Partition("not-a-region").name);
        }

        [TestMethod]
        public void ConcurrentLookupsReturnCorrectPartitions()
        {
            Parallel.For(0, 2000, i =>
            {
                Assert.AreEqual("aws", Fn.Partition("us-west-2").name);
                Assert.AreEqual("aws-cn", Fn.Partition("cn-west-9").name);
                Assert.AreEqual("aws-us-gov", Fn.Partition("us-gov-east-1").name);
            });
        }

        [TestMethod]
        public void LoadPartitionsReplacesLookupData()
        {
            var json = @"{
  ""version"": ""1.1"",
  ""partitions"": [
    {
      ""id"": ""test"",
      ""regionRegex"": ""^tst\\-\\w+\\-\\d+$"",
      ""regions"": { ""test-region-1"": {} },
      ""outputs"": { ""name"": ""test"", ""dnsSuffix"": ""test.example"", ""dualStackDnsSuffix"": ""ds.test.example"", ""supportsFIPS"": false, ""supportsDualStack"": true, ""implicitGlobalRegion"": ""test-region-1"" }
    },
    {
      ""id"": ""aws"",
      ""regionRegex"": ""^zz\\-\\w+\\-\\d+$"",
      ""regions"": { ""zz-east-1"": {} },
      ""outputs"": { ""name"": ""aws-test"", ""dnsSuffix"": ""aws.example"", ""dualStackDnsSuffix"": ""ds.aws.example"", ""supportsFIPS"": true, ""supportsDualStack"": false, ""implicitGlobalRegion"": ""zz-east-1"" }
    }
  ]
}";
            // Warm up the lookup data before the reload.
            Assert.AreEqual("aws", Fn.Partition("us-west-2").name);

            using (new PartitionStateRestorer())
            {
                var file = Path.Combine(Path.GetTempPath(), "partitions-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(file, json);
                try
                {
                    Partition.LoadPartitions(file);
                }
                finally
                {
                    File.Delete(file);
                }

                Assert.AreEqual("test", Fn.Partition("test-region-1").name);
                Assert.IsFalse(Fn.Partition("test-region-1").supportsFIPS);
                Assert.AreEqual("test", Fn.Partition("tst-west-4").name);
                Assert.AreEqual("aws-test", Fn.Partition("zz-east-1").name);
                Assert.IsFalse(Fn.Partition("zz-east-1").supportsDualStack);
                // us-west-2 is no longer listed and matches no regex: the "aws" id is the default.
                Assert.AreEqual("aws-test", Fn.Partition("us-west-2").name);
                Assert.AreEqual("aws.example", Fn.Partition("us-west-2").dnsSuffix);
            }

            Assert.AreEqual("aws", Fn.Partition("us-west-2").name);
            Assert.AreEqual("amazonaws.com", Fn.Partition("us-west-2").dnsSuffix);
            Assert.AreEqual("aws-cn", Fn.Partition("cn-west-9").name);
        }

        [TestMethod]
        public void LoadPartitionsWithoutAwsPartitionHasNoDefault()
        {
            var json = @"{
  ""version"": ""1.1"",
  ""partitions"": [
    {
      ""id"": ""test"",
      ""regionRegex"": ""^tst\\-\\w+\\-\\d+$"",
      ""regions"": { ""test-region-1"": {} },
      ""outputs"": { ""name"": ""test"", ""dnsSuffix"": ""test.example"", ""dualStackDnsSuffix"": ""ds.test.example"", ""supportsFIPS"": false, ""supportsDualStack"": false, ""implicitGlobalRegion"": ""test-region-1"" }
    }
  ]
}";
            using (new PartitionStateRestorer())
            {
                var file = Path.Combine(Path.GetTempPath(), "partitions-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(file, json);
                try
                {
                    Partition.LoadPartitions(file);
                }
                finally
                {
                    File.Delete(file);
                }

                Assert.AreEqual("test", Fn.Partition("test-region-1").name);
                // With no default partition the lookup fails when it is used, not at load time.
                Assert.ThrowsExactly<ArgumentNullException>(() => Fn.Partition("us-west-2"));
                Assert.AreEqual("test", Fn.Partition("tst-east-2").name);
            }

            Assert.AreEqual("aws", Fn.Partition("us-west-2").name);
        }

        [TestMethod]
        public void LoadPartitionsWithDuplicateRegionKeepsPartiallyLoadedData()
        {
            // The second partition repeats a region, so LoadPartitions throws after it has already
            // replaced the tables with the first partition's data. Later lookups see that partial state.
            var json = @"{
  ""version"": ""1.1"",
  ""partitions"": [
    {
      ""id"": ""aws"",
      ""regionRegex"": ""^zz\\-\\w+\\-\\d+$"",
      ""regions"": { ""zz-east-1"": {} },
      ""outputs"": { ""name"": ""first"", ""dnsSuffix"": ""first.example"", ""dualStackDnsSuffix"": ""ds.first.example"", ""supportsFIPS"": true, ""supportsDualStack"": true, ""implicitGlobalRegion"": ""zz-east-1"" }
    },
    {
      ""id"": ""second"",
      ""regionRegex"": ""^yy\\-\\w+\\-\\d+$"",
      ""regions"": { ""zz-east-1"": {} },
      ""outputs"": { ""name"": ""second"", ""dnsSuffix"": ""second.example"", ""dualStackDnsSuffix"": ""ds.second.example"", ""supportsFIPS"": true, ""supportsDualStack"": true, ""implicitGlobalRegion"": ""yy-east-1"" }
    }
  ]
}";
            Assert.AreEqual("aws", Fn.Partition("us-west-2").name);

            using (new PartitionStateRestorer())
            {
                var file = Path.Combine(Path.GetTempPath(), "partitions-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(file, json);
                try
                {
                    Assert.ThrowsExactly<ArgumentException>(() => Partition.LoadPartitions(file));
                }
                finally
                {
                    File.Delete(file);
                }

                Assert.AreEqual("first", Fn.Partition("zz-east-1").name);
                Assert.AreEqual("first", Fn.Partition("us-west-2").name);
                // The second partition's regex was added before its duplicate region threw.
                Assert.AreEqual("second", Fn.Partition("yy-west-3").name);
            }

            Assert.AreEqual("aws", Fn.Partition("us-west-2").name);
        }

        [TestMethod]
        public void LoadPartitionsWithPartitionMissingOutputsFailsOnlyForThatPartition()
        {
            var json = @"{
  ""version"": ""1.1"",
  ""partitions"": [
    {
      ""id"": ""aws"",
      ""regionRegex"": ""^zz\\-\\w+\\-\\d+$"",
      ""regions"": { ""zz-east-1"": {} },
      ""outputs"": { ""name"": ""aws-test"", ""dnsSuffix"": ""aws.example"", ""dualStackDnsSuffix"": ""ds.aws.example"", ""supportsFIPS"": true, ""supportsDualStack"": true, ""implicitGlobalRegion"": ""zz-east-1"" }
    },
    {
      ""id"": ""broken"",
      ""regionRegex"": ""^br\\-\\w+\\-\\d+$"",
      ""regions"": { ""broken-region-1"": {} }
    }
  ]
}";
            using (new PartitionStateRestorer())
            {
                var file = Path.Combine(Path.GetTempPath(), "partitions-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(file, json);
                try
                {
                    Partition.LoadPartitions(file);
                }
                finally
                {
                    File.Delete(file);
                }

                Assert.AreEqual("aws-test", Fn.Partition("zz-east-1").name);
                Assert.AreEqual("aws-test", Fn.Partition("us-west-2").name);
                Assert.ThrowsExactly<ArgumentNullException>(() => Fn.Partition("broken-region-1"));
                Assert.ThrowsExactly<ArgumentNullException>(() => Fn.Partition("br-west-1"));
                Assert.AreEqual("aws-test", Fn.Partition("zz-west-2").name);
            }

            Assert.AreEqual("aws", Fn.Partition("us-west-2").name);
        }

        [TestMethod]
        public void LoadPartitionsMissingFileKeepsData()
        {
            Assert.AreEqual("aws", Fn.Partition("us-west-2").name);
            var file = Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".json");
            Assert.ThrowsExactly<AmazonClientException>(() => Partition.LoadPartitions(file));
            Assert.AreEqual("aws", Fn.Partition("us-west-2").name);
            Assert.AreEqual("aws-cn", Fn.Partition("cn-west-9").name);
        }

        /// <summary>
        /// Saves the generated partition tables and puts the same instances back on dispose, through
        /// the private fields <see cref="Partition.LoadPartitions(string)"/> writes. Any cached view of
        /// the tables is dropped as well.
        /// </summary>
        private sealed class PartitionStateRestorer : IDisposable
        {
            private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Static;
            private readonly Dictionary<string, PartitionAttributesShape> _byRegionName;
            private readonly List<(Regex Regex, PartitionAttributesShape Partition)> _byRegex;
            private readonly PartitionAttributesShape _default;

            public PartitionStateRestorer()
            {
                _byRegionName = new Dictionary<string, PartitionAttributesShape>(ByRegionNameField());
                _byRegex = new List<(Regex, PartitionAttributesShape)>(ByRegexField());
                _default = (PartitionAttributesShape)typeof(Partition).GetField("_defaultPartition", Flags).GetValue(null);
            }

            private static Dictionary<string, PartitionAttributesShape> ByRegionNameField() =>
                (Dictionary<string, PartitionAttributesShape>)typeof(Partition).GetField("_partitionsByRegionName", Flags).GetValue(null);

            private static List<(Regex Regex, PartitionAttributesShape Partition)> ByRegexField() =>
                (List<(Regex Regex, PartitionAttributesShape Partition)>)typeof(Partition).GetField("_partitionsByRegex", Flags).GetValue(null);

            public void Dispose()
            {
                var byRegionName = ByRegionNameField();
                byRegionName.Clear();
                foreach (var kvp in _byRegionName) byRegionName.Add(kvp.Key, kvp.Value);
                var byRegex = ByRegexField();
                byRegex.Clear();
                byRegex.AddRange(_byRegex);
                typeof(Partition).GetField("_defaultPartition", Flags).SetValue(null, _default);
                var snapshot = typeof(Partition).GetField("_snapshot", Flags);
                snapshot?.SetValue(null, null);
            }
        }
    }
}
