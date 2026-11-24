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
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Amazon.Runtime.Internal.Util;
using Amazon.Util;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// DefaultRequest creates SubResources, PathResources and TrailingHeaders on first access, and the
    /// SDK's URL building and signing read them without creating them. These tests pin what callers see
    /// through the public properties and that URLs and signatures do not depend on whether the
    /// dictionaries were ever created.
    /// </summary>
    [TestClass]
    public class DefaultRequestLazyResourceMapsTests
    {
        private const string AccessKey = "AKIDEXAMPLE";
        private const string SecretKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
        private static readonly DateTime SignedAt = new DateTime(2026, 11, 21, 6, 0, 0, DateTimeKind.Utc);

        private sealed class TestRequest : AmazonWebServiceRequest { }

        private static DefaultRequest NewRequest(string resourcePath = "/")
        {
            return new DefaultRequest(new TestRequest(), "TestService")
            {
                Endpoint = new Uri("https://service.us-west-2.amazonaws.com"),
                ResourcePath = resourcePath,
                HttpMethod = "POST",
                Content = Encoding.UTF8.GetBytes("{\"a\":1}"),
            };
        }

        private static ClientConfig NewConfig()
        {
            return new MockClientConfig
            {
                RegionEndpoint = RegionEndpoint.USWest2,
                AuthenticationServiceName = "service",
            };
        }

        [TestMethod]
        public void Properties_AreNeverNullAndReturnTheSameMutableInstance()
        {
            IRequest request = NewRequest();
            foreach (var get in new Func<IDictionary<string, string>>[] { () => request.SubResources, () => request.PathResources, () => request.TrailingHeaders })
            {
                var first = get();
                Assert.IsNotNull(first);
                Assert.AreEqual(0, first.Count);
                Assert.IsFalse(first.IsReadOnly);
                Assert.AreSame(first, get());
                first["k"] = "v";
                Assert.AreEqual("v", get()["k"]);
            }
        }

        [TestMethod]
        public void Properties_KeepTheirKeyComparers()
        {
            IRequest request = NewRequest();
            request.SubResources["Acl"] = null;
            Assert.IsTrue(request.SubResources.ContainsKey("acl"), "SubResources ignore case");
            request.TrailingHeaders["X-Amz-Checksum-Crc32"] = "";
            Assert.IsTrue(request.TrailingHeaders.ContainsKey("x-amz-checksum-crc32"), "TrailingHeaders ignore case");
            request.PathResources["{Bucket}"] = "b";
            Assert.IsFalse(request.PathResources.ContainsKey("{bucket}"), "PathResources are case sensitive");
            Assert.IsFalse(request.SubResources.ContainsKey("\u0130cl"), "SubResources compare ordinally");
        }

        [TestMethod]
        public void AddMethods_GoThroughTheProperties()
        {
            IRequest request = NewRequest();
            request.AddSubResource("acl");
            request.AddSubResource("versionId", "3");
            request.AddPathResource("{Key+}", "a/b");
            CollectionAssert.AreEqual(new[] { "acl", "versionId" }, request.SubResources.Keys.ToArray());
            Assert.IsNull(request.SubResources["acl"]);
            Assert.AreEqual("a/b", request.PathResources["{Key+}"]);
            Assert.ThrowsExactly<ArgumentException>(() => request.AddSubResource("ACL"));
            Assert.ThrowsExactly<ArgumentException>(() => request.AddPathResource("{Key+}", "c"));
            Assert.ThrowsExactly<ArgumentNullException>(() => request.AddSubResource(null));
        }

        [TestMethod]
        public void ConcurrentFirstAccess_ReturnsOneInstance()
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                IRequest request = NewRequest();
                var seen = new IDictionary<string, string>[3, 8];
                using (var start = new ManualResetEventSlim(false))
                {
                    var tasks = Enumerable.Range(0, 8).Select(t => Task.Factory.StartNew(() =>
                    {
                        start.Wait();
                        seen[0, t] = request.SubResources;
                        seen[1, t] = request.PathResources;
                        seen[2, t] = request.TrailingHeaders;
                    }, TaskCreationOptions.LongRunning)).ToArray();
                    start.Set();
                    Task.WaitAll(tasks);
                }
                for (int t = 1; t < 8; t++)
                {
                    Assert.AreSame(seen[0, 0], seen[0, t]);
                    Assert.AreSame(seen[1, 0], seen[1, t]);
                    Assert.AreSame(seen[2, 0], seen[2, t]);
                }
            }
        }

        [TestMethod]
        public void ComposeUrl_SameWhetherOrNotTheDictionariesWereCreated()
        {
            var untouched = NewRequest("/items/x");
            var touched = NewRequest("/items/x");
            _ = touched.SubResources.Count;
            _ = touched.PathResources.Count;
            _ = touched.TrailingHeaders.Count;

            Assert.AreEqual("https://service.us-west-2.amazonaws.com/items/x", AmazonServiceClient.ComposeUrl(untouched).AbsoluteUri);
            Assert.AreEqual(AmazonServiceClient.ComposeUrl(touched).AbsoluteUri, AmazonServiceClient.ComposeUrl(untouched).AbsoluteUri);

            Assert.IsNull(untouched.SubResourcesIfCreated);
            Assert.IsNull(untouched.PathResourcesIfCreated);
            Assert.IsNull(untouched.TrailingHeadersIfCreated);
        }

        [TestMethod]
        public void ComposeUrl_UsesSubResourcesAndPathResources()
        {
            var request = NewRequest("/{Bucket}/{Key+}");
            request.AddPathResource("{Bucket}", "my-bucket");
            request.AddPathResource("{Key+}", "dir/file.txt");
            request.AddSubResource("acl");
            request.AddSubResource("versionId", "3");

            Assert.AreEqual("https://service.us-west-2.amazonaws.com/my-bucket/dir/file.txt?acl&versionId=3",
                AmazonServiceClient.ComposeUrl(request).AbsoluteUri);
        }

        [TestMethod]
        public void Sign_SameSignatureWhetherOrNotTheDictionariesWereCreated()
        {
            var untouched = NewRequest("/items/x");
            var touched = NewRequest("/items/x");
            _ = touched.SubResources.Count;
            _ = touched.PathResources.Count;
            _ = touched.TrailingHeaders.Count;

            var untouchedResult = new AWS4Signer().SignRequest(untouched, NewConfig(), null, AccessKey, SecretKey, SignedAt);
            var touchedResult = new AWS4Signer().SignRequest(touched, NewConfig(), null, AccessKey, SecretKey, SignedAt);

            Assert.AreEqual(touchedResult.ForAuthorizationHeader, untouchedResult.ForAuthorizationHeader);
            Assert.IsFalse(untouched.Headers.ContainsKey(HeaderKeys.XAmzTrailerHeader));
            Assert.IsNull(untouched.SubResourcesIfCreated);
            Assert.IsNull(untouched.PathResourcesIfCreated);
            Assert.IsNull(untouched.TrailingHeadersIfCreated);
            CollectionAssert.AreEqual(touched.Headers.ToList(), untouched.Headers.ToList());
        }

        [TestMethod]
        public void Sign_IncludesSubResourcesPathResourcesAndTrailingHeaders()
        {
            var plain = NewRequest("/{Bucket}/{Key+}");
            plain.AddPathResource("{Bucket}", "b");
            plain.AddPathResource("{Key+}", "k");
            var withSubResource = NewRequest("/{Bucket}/{Key+}");
            withSubResource.AddPathResource("{Bucket}", "b");
            withSubResource.AddPathResource("{Key+}", "k");
            withSubResource.AddSubResource("acl");
            var otherPath = NewRequest("/{Bucket}/{Key+}");
            otherPath.AddPathResource("{Bucket}", "b");
            otherPath.AddPathResource("{Key+}", "other");

            var plainAuth = new AWS4Signer().SignRequest(plain, NewConfig(), null, AccessKey, SecretKey, SignedAt).ForAuthorizationHeader;
            Assert.AreNotEqual(plainAuth, new AWS4Signer().SignRequest(withSubResource, NewConfig(), null, AccessKey, SecretKey, SignedAt).ForAuthorizationHeader);
            Assert.AreNotEqual(plainAuth, new AWS4Signer().SignRequest(otherPath, NewConfig(), null, AccessKey, SecretKey, SignedAt).ForAuthorizationHeader);

            var withTrailer = NewRequest();
            withTrailer.TrailingHeaders["x-amz-checksum-crc32"] = string.Empty;
            new AWS4Signer().SignRequest(withTrailer, NewConfig(), null, AccessKey, SecretKey, SignedAt);
            Assert.AreEqual("x-amz-checksum-crc32", withTrailer.Headers[HeaderKeys.XAmzTrailerHeader]);
        }

        [TestMethod]
        public void GetParametersToCanonicalize_IncludesSubResourcesOnlyWhenPresent()
        {
            var request = NewRequest();
            Assert.AreEqual(0, AWS4SignerTestHook.Parameters(request).Count);
            Assert.IsNull(request.SubResourcesIfCreated);

            request.AddSubResource("uploads");
            request.AddSubResource("partNumber", "2");
            CollectionAssert.AreEqual(
                new[] { new KeyValuePair<string, string>("uploads", null), new KeyValuePair<string, string>("partNumber", "2") },
                AWS4SignerTestHook.Parameters(request));
        }

        [TestMethod]
        public void Extensions_FallBackToThePropertiesForOtherRequestTypes()
        {
            var inner = NewRequest();
            inner.AddSubResource("acl");
            inner.AddPathResource("{Bucket}", "b");
            inner.TrailingHeaders["x"] = "y";
            // An IRequest that is not a DefaultRequest, as a caller could implement.
            var mock = new Moq.Mock<IRequest>();
            mock.SetupGet(r => r.SubResources).Returns(inner.SubResources);
            mock.SetupGet(r => r.PathResources).Returns(inner.PathResources);
            mock.SetupGet(r => r.TrailingHeaders).Returns(inner.TrailingHeaders);
            IRequest wrapper = mock.Object;

            Assert.AreSame(inner.SubResources, wrapper.GetSubResourcesIfCreated());
            Assert.AreSame(inner.PathResources, wrapper.GetPathResourcesIfCreated());
            Assert.AreSame(inner.TrailingHeaders, wrapper.GetTrailingHeadersIfCreated());
        }

        private sealed class AWS4SignerTestHook : AWS4Signer
        {
            public static List<KeyValuePair<string, string>> Parameters(IRequest request) => GetParametersToCanonicalize(request);
        }
    }
}
