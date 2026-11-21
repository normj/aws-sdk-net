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
using Amazon.Runtime.Internal;
using Amazon.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Text;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins AWSSDKUtils.ResolveResourcePathV2 to the result of SplitResourcePathIntoSegmentsV2 followed by
    /// JoinResourcePathSegmentsV2, which is how it was implemented before it was rewritten as a single pass.
    /// </summary>
    [TestClass]
    public class ResolveResourcePathEquivalenceTests
    {
        private static string Reference(string resourcePath, IDictionary<string, string> pathResources)
        {
            if (string.IsNullOrEmpty(resourcePath))
                return resourcePath;
            return AWSSDKUtils.JoinResourcePathSegmentsV2(AWSSDKUtils.SplitResourcePathIntoSegmentsV2(resourcePath, pathResources));
        }

        private static void AssertSame(string resourcePath, IDictionary<string, string> pathResources)
        {
            var expected = Reference(resourcePath, pathResources);
            var actual = AWSSDKUtils.ResolveResourcePathV2(resourcePath, pathResources);
            Assert.AreEqual(expected, actual, $"path '{resourcePath}'");
        }

        private static Dictionary<string, string> Ordinal(params string[] keyValues)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < keyValues.Length; i += 2)
                d.Add(keyValues[i], keyValues[i + 1]);
            return d;
        }

        private static readonly string[] Paths =
        {
            "",
            "/",
            "//",
            "a",
            "/a/b/c",
            "a/b/",
            "2015-03-31/functions/{FunctionName}/invocations",
            "{Bucket}/{Key+}",
            "{Key+}",
            "/{Key+}",
            "{Key+}/",
            "prefix/{Id}/{Id}/suffix",
            "literal with space/ümlaut/%25/+/:/~-._",
            "{NotAKey}/{Id}",
            "Id/{Id}/ID",
            "a//{Id}//b",
            "\uD83D\uDE00/{Id}/\uD800",
        };

        private static readonly string[] Values =
        {
            null,
            "",
            "simple",
            "my-function",
            "photos/2024/summer/beach day.jpg",
            "/leading/and/trailing/",
            "//",
            "a+b:c~d!e*f'g(h)i",
            "ümlaut/日本語/\uD83D\uDE00",
            "%2F already/encoded",
            "\uDC00lone/\uD800",
            new string('x', 300) + "/" + new string('é', 200),
        };

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Util")]
        public void NoPathResources_MatchesReference()
        {
            foreach (var path in Paths)
            {
                AssertSame(path, null);
                AssertSame(path, Ordinal());
                AssertSame(path, new Dictionary<string, string>());
            }
            AssertSame(null, null);
            AssertSame(new string('a', 400) + "/" + new string('ü', 400), null);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Util")]
        public void WithPathResources_MatchesReference()
        {
            foreach (var path in Paths)
            {
                foreach (var value in Values)
                {
                    foreach (var other in new[] { "other", "x/y z" })
                    {
                        var dictionaries = new IDictionary<string, string>[]
                        {
                            Ordinal("{FunctionName}", value, "{Bucket}", other, "{Id}", value),
                            new Dictionary<string, string> { { "{Id}", value }, { "{Bucket}", other } },
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "{id}", value }, { "{bucket}", other } },
                            new SortedDictionary<string, string>(StringComparer.Ordinal) { { "{Id}", value }, { "{FunctionName}", other } },
                            Ordinal("Id", value, "a", other),
                        };

                        foreach (var d in dictionaries)
                        {
                            AssertSame(path, d);
                        }

                        // Greedy keys can only be null-free here: a null greedy value throws (pinned below).
                        if (value != null)
                        {
                            AssertSame(path, Ordinal("{Key+}", value, "{Bucket}", other));
                            AssertSame(path, new SortedDictionary<string, string> { { "{Key+}", value } });
                            AssertSame(path, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "{key+}", value } });
                        }
                    }
                }
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Util")]
        public void NullGreedyValue_ThrowsSameExceptionAsReference()
        {
            var resources = Ordinal("{Key+}", null);
            Exception expected = null;
            try { Reference("a/{Key+}", resources); } catch (Exception e) { expected = e; }
            Assert.IsNotNull(expected);

            Exception actual = null;
            try { AWSSDKUtils.ResolveResourcePathV2("a/{Key+}", resources); } catch (Exception e) { actual = e; }
            Assert.IsNotNull(actual);
            Assert.AreEqual(expected.GetType(), actual.GetType());
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Util")]
        public void NullAndEmptyPath_ReturnedUnchanged()
        {
            Assert.IsNull(AWSSDKUtils.ResolveResourcePathV2(null, Ordinal("{Id}", "x")));
            Assert.AreEqual("", AWSSDKUtils.ResolveResourcePathV2("", Ordinal("{Id}", "x")));
            Assert.IsNull(AWSSDKUtils.ResolveResourcePathV2(null, null));
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Util")]
        public void KnownValues()
        {
            Assert.AreEqual("2015-03-31/functions/my%20function/invocations",
                AWSSDKUtils.ResolveResourcePathV2("2015-03-31/functions/{FunctionName}/invocations", Ordinal("{FunctionName}", "my function")));
            Assert.AreEqual("photos/2024/a%2Bb%3Ac.jpg",
                AWSSDKUtils.ResolveResourcePathV2("{Key+}", Ordinal("{Key+}", "photos/2024/a+b:c.jpg")));
            Assert.AreEqual("a%2Fb", AWSSDKUtils.ResolveResourcePathV2("{Key}", Ordinal("{Key}", "a/b")));
            Assert.AreEqual("a+b%3Ac/%C3%BC", AWSSDKUtils.ResolveResourcePathV2("a+b:c/ü", null));
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Util")]
        public void RandomizedPaths_MatchReference()
        {
            var random = new Random(20261121);
            const string alphabet = "ab/{}+ %:~é\uD83D\uDE00\uD800日";
            for (var n = 0; n < 5000; n++)
            {
                var path = RandomString(random, alphabet, random.Next(0, 24));
                var segments = path.Split('/');
                var resources = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var segment in segments)
                {
                    if (!resources.ContainsKey(segment) && random.Next(3) == 0)
                    {
                        var value = RandomString(random, alphabet, random.Next(0, 16));
                        resources[segment] = value;
                    }
                }
                AssertSame(path, resources);
                AssertSame(path, null);
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Util")]
        public void ComposeUrl_RestRequest_Unchanged()
        {
            var request = new DefaultRequest(new TestRequest(), "TestService")
            {
                Endpoint = new Uri("https://bucket.s3.us-west-2.amazonaws.com"),
                ResourcePath = "/{Key+}",
            };
            request.AddPathResource("{Key+}", "photos/2024/summer/beach day.jpg");
            Assert.AreEqual("https://bucket.s3.us-west-2.amazonaws.com/photos/2024/summer/beach%20day.jpg",
                AmazonServiceClient.ComposeUrl(request).AbsoluteUri);
        }

        private static string RandomString(Random random, string alphabet, int length)
        {
            var sb = new StringBuilder(length);
            for (var i = 0; i < length; i++)
                sb.Append(alphabet[random.Next(alphabet.Length)]);
            return sb.ToString();
        }

        private sealed class TestRequest : AmazonWebServiceRequest { }
    }
}
