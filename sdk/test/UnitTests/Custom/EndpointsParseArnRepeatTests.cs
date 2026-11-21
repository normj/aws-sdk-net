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
using Amazon.Runtime.Endpoints;
using Amazon.Runtime.Internal.Endpoints.StandardLibrary;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins Fn.ParseArn for repeated input: every call returns an independent Arn identical to a fresh Arn.Parse.
    /// </summary>
    [TestClass]
    public class EndpointsParseArnRepeatTests
    {
        private static string Unique() => Guid.NewGuid().ToString("N");

        private static List<KeyValuePair<string, object>> Entries(PropertyBag bag)
        {
            var field = typeof(PropertyBag).GetField("properties", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return ((Dictionary<string, object>)field.GetValue(bag)).ToList();
        }

        private static void AssertSameAsFreshParse(string arnString, Arn actual)
        {
            var expected = Arn.Parse(arnString);
            Assert.IsNotNull(actual);
            Assert.AreEqual(typeof(Arn), actual.GetType());
            var e = Entries(expected);
            var a = Entries(actual);
            CollectionAssert.AreEqual(e.Select(x => x.Key).ToList(), a.Select(x => x.Key).ToList());
            Assert.AreEqual(expected.partition, actual.partition);
            Assert.AreEqual(expected.service, actual.service);
            Assert.AreEqual(expected.region, actual.region);
            Assert.AreEqual(expected.accountId, actual.accountId);
            Assert.AreEqual(typeof(List<string>), actual["resourceId"].GetType());
            CollectionAssert.AreEqual(expected.resourceId, actual.resourceId);
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void RepeatedArnsMatchFreshParse()
        {
            var arns = new[]
            {
                "arn:aws:s3:us-west-2:123456789012:accesspoint:my-endpoint-" + Unique(),
                "arn:aws:dynamodb:us-east-1:123456789012:table/my-table-" + Unique(),
                "arn:aws:s3:::bucket-" + Unique(),
                "arn:aws-cn:s3-outposts:cn-north-1:123456789012:outpost/op-01234567890123456/accesspoint/" + Unique(),
                "arn:aws:iam::123456789012:user/a:b/c::/" + Unique(),
                "arn:aws:s3:us-west-2:123456789012:" + Unique() + ":",
                "arn:aws:sns:us-east-1:123456789012:topic:" + Unique() + ":extra:colons",
            };

            foreach (var arn in arns)
            {
                for (int i = 0; i < 3; i++)
                {
                    AssertSameAsFreshParse(arn, Fn.ParseArn(arn));
                }
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void RepeatedArnsReturnIndependentObjects()
        {
            var arnString = "arn:aws:s3:us-west-2:123456789012:accesspoint:" + Unique();
            var first = Fn.ParseArn(arnString);
            var second = Fn.ParseArn(arnString);
            Assert.AreNotSame(first, second);
            Assert.AreNotSame(first.resourceId, second.resourceId);

            first.partition = "changed";
            first.region = null;
            first["extra"] = "x";
            first.resourceId.Add("added");
            first.resourceId[0] = "mutated";
            second.resourceId = new List<string> { "replaced" };

            AssertSameAsFreshParse(arnString, Fn.ParseArn(arnString));
            Assert.IsNull(Fn.ParseArn(arnString)["extra"]);
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void InvalidArnsReturnNullOnEveryCall()
        {
            var invalid = new[]
            {
                null,
                "",
                "my-table",
                "arn",
                "arn:",
                "arn:aws:s3:us-west-2:123456789012",
                "arn::s3:us-west-2:123456789012:res" + Unique(),
                "arn:aws::us-west-2:123456789012:res" + Unique(),
                "arn:aws:s3:us-west-2:123456789012:",
                "ARN:aws:s3:us-west-2:123456789012:res" + Unique(),
                " arn:aws:s3:us-west-2:123456789012:res" + Unique(),
            };

            foreach (var s in invalid)
            {
                for (int i = 0; i < 3; i++)
                {
                    Assert.IsNull(Fn.ParseArn(s), s);
                }
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void ManyDistinctAndLongArnsStillParse()
        {
            for (int i = 0; i < 600; i++)
            {
                var arn = "arn:aws:dynamodb:us-east-1:123456789012:table/t" + i + "-" + Unique();
                AssertSameAsFreshParse(arn, Fn.ParseArn(arn));
                AssertSameAsFreshParse(arn, Fn.ParseArn(arn));
            }

            var longArn = "arn:aws:s3:::" + new string('b', 5000) + Unique();
            for (int i = 0; i < 2; i++)
            {
                AssertSameAsFreshParse(longArn, Fn.ParseArn(longArn));
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void ConcurrentArnParse()
        {
            var arns = Enumerable.Range(0, 8)
                .Select(i => "arn:aws:s3:us-west-2:123456789012:accesspoint/ap" + i + "-" + Unique())
                .ToArray();

            Parallel.For(0, 4000, i =>
            {
                var s = arns[i % arns.Length];
                var arn = Fn.ParseArn(s);
                AssertSameAsFreshParse(s, arn);
                arn.resourceId.Clear();
                arn.partition = null;
            });
        }
    }
}
