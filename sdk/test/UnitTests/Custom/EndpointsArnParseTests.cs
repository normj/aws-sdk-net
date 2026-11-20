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
using Amazon.Runtime.Internal.Endpoints.StandardLibrary;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins the endpoints standard library Arn.Parse / Arn.TryParse / Fn.ParseArn to the behavior of the
    /// original string.Split based implementation, which is kept below as a reference.
    /// </summary>
    [TestClass]
    public class EndpointsArnParseTests
    {
        private static readonly string[] Samples =
        {
            "arn:aws:s3:us-west-2:123456789012:accesspoint:my-endpoint",
            "arn:aws:s3:us-west-2:123456789012:accesspoint/my-endpoint",
            "arn:aws:s3-outposts:us-west-2:123456789012:outpost:op-01234567890123456:accesspoint:reports",
            "arn:aws:dynamodb:us-east-1:123456789012:table/my-table",
            "arn:aws:s3:::my-bucket",
            "arn:aws:s3:::my-bucket/key/with/slashes",
            "arn:aws:s3:::a:b:c/d",
            "arn:aws:iam::123456789012:user/David",
            "arn:aws:s3:us-west-2:123456789012:",
            "arn:aws:s3:us-west-2:123456789012::",
            "arn:aws:s3:us-west-2:123456789012:/",
            "arn:aws:s3:us-west-2:123456789012:a::b//c",
            "arn::s3:us-west-2:123456789012:resource",
            "arn:aws::us-west-2:123456789012:resource",
            "arn:aws:s3:us-west-2:123456789012",
            "arn:aws:s3",
            "arn:",
            "arn:::::",
            "arn:a:b:::x",
            "ARN:aws:s3:us-west-2:123456789012:resource",
            "arnx:aws:s3:us-west-2:123456789012:resource",
            "xarn:aws:s3:us-west-2:123456789012:resource",
            " arn:aws:s3:us-west-2:123456789012:resource",
            "my-application-bucket",
            "my-table",
            "",
            "arn",
            "ar",
        };

        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        public void ParseMatchesReference()
        {
            foreach (var sample in Samples)
            {
                AssertParseEquivalent(sample);
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        public void RandomCorpusMatchesReference()
        {
            const string alphabet = "a:/-1 ";
            var random = new Random(4242);
            var sb = new StringBuilder();
            for (int i = 0; i < 20000; i++)
            {
                sb.Clear();
                if (random.Next(4) != 0)
                {
                    sb.Append("arn:");
                }
                int length = random.Next(0, 16);
                for (int j = 0; j < length; j++)
                {
                    sb.Append(alphabet[random.Next(alphabet.Length)]);
                }
                AssertParseEquivalent(sb.ToString());
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        public void ParsedFields()
        {
            var arn = Arn.Parse("arn:aws:s3:us-west-2:123456789012:accesspoint/my-endpoint:extra");
            Assert.AreEqual("aws", arn.partition);
            Assert.AreEqual("s3", arn.service);
            Assert.AreEqual("us-west-2", arn.region);
            Assert.AreEqual("123456789012", arn.accountId);
            CollectionAssert.AreEqual(new List<string> { "accesspoint", "my-endpoint", "extra" }, arn.resourceId);

            var global = Arn.Parse("arn:aws:s3:::my-bucket");
            Assert.AreEqual("", global.region);
            Assert.AreEqual("", global.accountId);
            CollectionAssert.AreEqual(new List<string> { "my-bucket" }, global.resourceId);
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        public void NullHandling()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => Arn.Parse(null));
            Assert.IsFalse(Arn.TryParse(null, out var arn));
            Assert.IsNull(arn);
            Assert.IsNull(Fn.ParseArn(null));
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        public void ExceptionMessages()
        {
            Assert.AreEqual("Malformed ARN - no partition specified",
                Assert.ThrowsExactly<ArgumentException>(() => Arn.Parse("arn::s3:r:a:res")).Message);
            Assert.AreEqual("Malformed ARN - no service specified",
                Assert.ThrowsExactly<ArgumentException>(() => Arn.Parse("arn:aws::r:a:res")).Message);
            Assert.AreEqual("Malformed ARN - no resource specified",
                Assert.ThrowsExactly<ArgumentException>(() => Arn.Parse("arn:aws:s3:r:a:")).Message);
            Assert.AreEqual("ARN is in incorrect format. ARN format is: arn:<partition>:<service>:<region>:<account-id>:<resource>",
                Assert.ThrowsExactly<ArgumentException>(() => Arn.Parse("arn:aws:s3:r")).Message);
            Assert.AreEqual("ARN is in incorrect format. ARN format is: arn:<partition>:<service>:<region>:<account-id>:<resource>",
                Assert.ThrowsExactly<ArgumentException>(() => Arn.Parse("nra:aws:s3:r:a:res")).Message);
        }

        private static void AssertParseEquivalent(string input)
        {
            Exception expectedException = null, actualException = null;
            Arn expected = null, actual = null;
            try { expected = ReferenceParse(input); } catch (Exception e) { expectedException = e; }
            try { actual = Arn.Parse(input); } catch (Exception e) { actualException = e; }

            if (expectedException != null)
            {
                Assert.IsNotNull(actualException, "expected an exception for '" + input + "'");
                Assert.AreEqual(expectedException.GetType(), actualException.GetType(), input);
                Assert.AreEqual(expectedException.Message, actualException.Message, input);
            }
            else
            {
                Assert.IsNull(actualException, "unexpected exception for '" + input + "': " + actualException?.Message);
                AssertArnEqual(expected, actual, input);
            }

            bool expectedTry = ReferenceTryParse(input, out var expectedTryArn);
            bool actualTry = Arn.TryParse(input, out var actualTryArn);
            Assert.AreEqual(expectedTry, actualTry, input);
            if (expectedTry)
            {
                AssertArnEqual(expectedTryArn, actualTryArn, input);
                AssertArnEqual(expectedTryArn, Fn.ParseArn(input), input);
            }
            else
            {
                Assert.IsNull(actualTryArn, input);
                Assert.IsNull(Fn.ParseArn(input), input);
            }
        }

        private static void AssertArnEqual(Arn expected, Arn actual, string input)
        {
            Assert.AreEqual(expected.partition, actual.partition, input);
            Assert.AreEqual(expected.service, actual.service, input);
            Assert.AreEqual(expected.region, actual.region, input);
            Assert.AreEqual(expected.accountId, actual.accountId, input);
            CollectionAssert.AreEqual(expected.resourceId, actual.resourceId, input);
            Assert.AreEqual(expected.resourceId.Capacity, actual.resourceId.Capacity, input);
        }

        // The original implementation, kept verbatim as the reference behavior.
        private static bool ReferenceTryParse(string arnString, out Arn arn)
        {
            try
            {
                if (arnString != null && arnString.StartsWith("arn:"))
                {
                    arn = ReferenceParse(arnString);
                    return true;
                }
            }
            catch (Exception) { }
            arn = null;
            return false;
        }

        private static Arn ReferenceParse(string arnString)
        {
            if (arnString == null)
            {
                throw new ArgumentNullException(nameof(arnString));
            }

            const string malformedErrorMessage = "ARN is in incorrect format. ARN format is: arn:<partition>:<service>:<region>:<account-id>:<resource>";

            var tokens = arnString.Split(new char[] { ':' }, 6);
            if (tokens.Length != 6)
            {
                throw new ArgumentException(malformedErrorMessage);
            }
            if (tokens[0] != "arn")
            {
                throw new ArgumentException(malformedErrorMessage);
            }
            string partition = tokens[1];
            if (string.IsNullOrEmpty(partition))
            {
                throw new ArgumentException("Malformed ARN - no partition specified");
            }
            string service = tokens[2];
            if (string.IsNullOrEmpty(service))
            {
                throw new ArgumentException("Malformed ARN - no service specified");
            }
            string region = tokens[3];
            string accountId = tokens[4];
            string resource = tokens[5];
            if (string.IsNullOrEmpty(resource))
            {
                throw new ArgumentException("Malformed ARN - no resource specified");
            }
            return new Arn
            {
                partition = partition,
                service = service,
                region = region,
                accountId = accountId,
                resourceId = resource.Split(new[] { ':', '/' }).ToList()
            };
        }
    }
}
